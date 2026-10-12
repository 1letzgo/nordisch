using System.Collections.Generic;
using System.Linq;
using LeGamez.Core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace LeGamez.Editor
{
    /// <summary>
    /// Prepares the Kenney Mini Characters (CC0): import settings with looping clips, a shared URP
    /// material using the colour-map texture, a locomotion Animator Controller, and the scale that
    /// makes them 1.75 m tall. Results are stored on GameMaterials. Called from ProjectSetup.Run.
    /// </summary>
    public static class CharacterSetup
    {
        const string Folder = "Assets/ThirdParty/KenneyMiniCharacters";
        const string ControllerPath = "Assets/Generated/Character.controller";
        const float TargetHeight = 1.75f;
        static readonly HashSet<string> Looping = new HashSet<string> { "idle", "walk", "sprint", "fall", "sit", "drive", "static" };

        public static void Run(GameMaterials gm)
        {
            var paths = AssetDatabase.FindAssets("t:Model character-", new[] { Folder })
                .Select(AssetDatabase.GUIDToAssetPath).Where(p => p.EndsWith(".fbx")).OrderBy(p => p).ToList();
            if (paths.Count == 0) { Debug.LogWarning("[LeGamez] No character FBX found"); return; }

            ConfigureTexture();
            foreach (var p in paths) ConfigureModel(p);

            gm.characters = paths.Select(AssetDatabase.LoadAssetAtPath<GameObject>).ToList();
            gm.characterMaterial = CreateMaterial();
            gm.characterController = CreateController(paths[0]);
            gm.characterScale = MeasureScale(gm.characters[0], paths[0], out float minY);
            gm.characterFootOffset = -minY * gm.characterScale;
            EditorUtility.SetDirty(gm);
            Debug.Log($"[LeGamez] Characters: {paths.Count}, scale {gm.characterScale:F2}");
        }

        static void ConfigureTexture()
        {
            var path = $"{Folder}/Textures/colormap.png";
            if (AssetImporter.GetAtPath(path) is not TextureImporter ti) return;
            ti.filterMode = FilterMode.Point; // palette texture – keep colours crisp
            ti.mipmapEnabled = false;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.SaveAndReimport();
        }

        static void ConfigureModel(string path)
        {
            var mi = (ModelImporter)AssetImporter.GetAtPath(path);
            mi.animationType = ModelImporterAnimationType.Generic;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            var clips = mi.defaultClipAnimations;
            foreach (var c in clips)
                c.loopTime = Looping.Contains(c.name);
            mi.clipAnimations = clips;
            mi.SaveAndReimport();
        }

        static Material CreateMaterial()
        {
            const string path = "Assets/Generated/Materials/Character.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Simple Lit")) { name = "Character" };
                AssetDatabase.CreateAsset(mat, path);
            }
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Folder}/Textures/colormap.png");
            mat.SetTexture("_BaseMap", tex);
            mat.mainTexture = tex;
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Smoothness", 0.15f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static AnimationClip Clip(string fbxPath, string name) =>
            AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<AnimationClip>().FirstOrDefault(c => c.name == name);

        /// <summary>Speed-driven locomotion blend tree + jump/fall states. All characters share the same rig.</summary>
        static RuntimeAnimatorController CreateController(string fbxPath)
        {
            AssetDatabase.DeleteAsset(ControllerPath);
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
            ctrl.AddParameter("Jump", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("Mode", AnimatorControllerParameterType.Int); // 0 = on foot, 1 = driving, 2 = sitting

            var sm = ctrl.layers[0].stateMachine;
            var locomotion = ctrl.CreateBlendTreeInController("Locomotion", out var tree);
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            tree.AddChild(Clip(fbxPath, "idle"), 0f);
            tree.AddChild(Clip(fbxPath, "walk"), 3.0f);
            tree.AddChild(Clip(fbxPath, "sprint"), 6.5f);
            sm.defaultState = locomotion;

            var jump = sm.AddState("Jump");
            jump.motion = Clip(fbxPath, "jump");
            var fall = sm.AddState("Fall");
            fall.motion = Clip(fbxPath, "fall");
            var drive = sm.AddState("Drive");
            drive.motion = Clip(fbxPath, "drive");
            var sit = sm.AddState("Sit");
            sit.motion = Clip(fbxPath, "sit");

            var toJump = sm.AddAnyStateTransition(jump);
            toJump.AddCondition(AnimatorConditionMode.If, 0, "Jump");
            toJump.duration = 0.05f;
            toJump.canTransitionToSelf = false;

            var jumpToFall = jump.AddTransition(fall);
            jumpToFall.hasExitTime = true;
            jumpToFall.exitTime = 0.9f;
            jumpToFall.duration = 0.1f;

            var fallToGround = fall.AddTransition(locomotion);
            fallToGround.AddCondition(AnimatorConditionMode.If, 0, "Grounded");
            fallToGround.duration = 0.1f;
            var jumpToGround = jump.AddTransition(locomotion);
            jumpToGround.AddCondition(AnimatorConditionMode.If, 0, "Grounded");
            jumpToGround.hasExitTime = true;
            jumpToGround.exitTime = 0.5f;
            jumpToGround.duration = 0.1f;

            // Walking off an edge.
            var locoToFall = locomotion.AddTransition(fall);
            locoToFall.AddCondition(AnimatorConditionMode.IfNot, 0, "Grounded");
            locoToFall.duration = 0.15f;

            // Vehicles (used later): Mode 1 = drive, 2 = sit.
            var toDrive = sm.AddAnyStateTransition(drive);
            toDrive.AddCondition(AnimatorConditionMode.Equals, 1, "Mode");
            toDrive.canTransitionToSelf = false;
            var toSit = sm.AddAnyStateTransition(sit);
            toSit.AddCondition(AnimatorConditionMode.Equals, 2, "Mode");
            toSit.canTransitionToSelf = false;
            foreach (var s in new[] { drive, sit })
            {
                var back = s.AddTransition(locomotion);
                back.AddCondition(AnimatorConditionMode.Equals, 0, "Mode");
                back.duration = 0.1f;
            }

            EditorUtility.SetDirty(ctrl);
            return ctrl;
        }

        static float MeasureScale(GameObject model, string fbxPath, out float footY)
        {
            footY = 0f;
            var go = (GameObject)Object.Instantiate(model);
            try
            {
                var idle = Clip(fbxPath, "idle");
                if (idle != null) idle.SampleAnimation(go, 0f);
                float minY = float.MaxValue, maxY = float.MinValue;
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    var baked = new Mesh();
                    smr.BakeMesh(baked, true);
                    foreach (var v in baked.vertices)
                    {
                        float y = smr.transform.TransformPoint(v).y;
                        minY = Mathf.Min(minY, y);
                        maxY = Mathf.Max(maxY, y);
                    }
                    Object.DestroyImmediate(baked);
                }
                foreach (var mr in go.GetComponentsInChildren<MeshRenderer>())
                {
                    minY = Mathf.Min(minY, mr.bounds.min.y);
                    maxY = Mathf.Max(maxY, mr.bounds.max.y);
                }
                float h = maxY - minY;
                footY = minY;
                Debug.Log($"[LeGamez] Character raw height {h:F3} (min {minY:F3})");
                return h > 0.01f ? TargetHeight / h : 1f;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
