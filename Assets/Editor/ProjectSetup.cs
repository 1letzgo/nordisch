using LeGamez.Core;
using LeGamez.Editor.OsmImport;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LeGamez.Editor
{
    /// <summary>
    /// One-shot project configuration: iOS player settings, URP quality, runtime materials.
    /// Menu: LeGamez/Setup Project. Batch: -executeMethod LeGamez.Editor.ProjectSetup.Run
    /// </summary>
    public static class ProjectSetup
    {
        public const string BundleId = "de.letzgo.legamez";
        public const string TeamId = "699G2QY959";

        [MenuItem("LeGamez/Setup Project")]
        public static void Run()
        {
            ConfigurePlayer();
            ConfigureUrp();
            CreateRuntimeMaterials();
            AssetDatabase.SaveAssets();
            Debug.Log("[LeGamez] Project setup done.");
        }

        static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "letzgo";
            PlayerSettings.productName = "LeGamez";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, BundleId);
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.iOS.buildNumber = "1";
            PlayerSettings.iOS.appleDeveloperTeamID = TeamId;
            PlayerSettings.iOS.appleEnableAutomaticSigning = true;
            PlayerSettings.iOS.targetOSVersionString = "16.0";
            PlayerSettings.iOS.targetDevice = iOSTargetDevice.iPhoneAndiPad;
            PlayerSettings.iOS.hideHomeButton = true;
            PlayerSettings.iOS.deferSystemGesturesMode = UnityEngine.iOS.SystemGestureDeferMode.BottomEdge;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.iOS, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS, new[] { GraphicsDeviceType.Metal });
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.iOS, ManagedStrippingLevel.Low);
        }

        static void ConfigureUrp()
        {
            var pipelines = new System.Collections.Generic.List<RenderPipelineAsset>();
            if (GraphicsSettings.defaultRenderPipeline != null) pipelines.Add(GraphicsSettings.defaultRenderPipeline);
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                var rp = QualitySettings.GetRenderPipelineAssetAt(i);
                if (rp != null && !pipelines.Contains(rp)) pipelines.Add(rp);
            }
            if (pipelines.Count == 0)
            {
                Debug.LogWarning("[LeGamez] No URP asset assigned – project was not created from the URP template.");
                return;
            }
            foreach (var rp in pipelines)
            {
                if (rp is not UniversalRenderPipelineAsset urp) continue;
                urp.shadowDistance = 120f;
                urp.shadowCascadeCount = 2;
                urp.msaaSampleCount = 4;
                urp.renderScale = 1f;
                urp.supportsHDR = false;
                EditorUtility.SetDirty(urp);
            }
        }

        static void CreateRuntimeMaterials()
        {
            OsmAreaImporter.EnsureFolder("Assets/Resources");
            OsmAreaImporter.EnsureFolder("Assets/Generated/Materials");
            string path = $"Assets/Resources/{GameMaterials.ResourcePath}.asset";
            var gm = AssetDatabase.LoadAssetAtPath<GameMaterials>(path);
            if (gm == null)
            {
                gm = ScriptableObject.CreateInstance<GameMaterials>();
                AssetDatabase.CreateAsset(gm, path);
            }
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            gm.playerBody = Mat("Player_Body", lit, new Color(0.15f, 0.45f, 0.85f));
            gm.playerSkin = Mat("Player_Skin", lit, new Color(0.96f, 0.80f, 0.67f));
            gm.playerAccent = Mat("Player_Accent", lit, new Color(0.9f, 0.4f, 0.3f));
            gm.poiOpen = Mat("Poi_Open", unlit, new Color(1f, 0.78f, 0.15f));
            gm.poiDone = Mat("Poi_Done", unlit, new Color(0.35f, 0.85f, 0.45f));
            EditorUtility.SetDirty(gm);
        }

        static Material Mat(string name, Shader shader, Color color)
        {
            string path = $"Assets/Generated/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(m, path);
            }
            m.shader = shader;
            m.SetColor("_BaseColor", color);
            m.color = color;
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
