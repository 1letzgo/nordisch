using System.IO;
using LeGamez.Vehicles;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LeGamez.Editor
{
    /// <summary>
    /// Renders the procedural car and bicycle from a few angles to Builds/Snapshots/vehicle_*.png.
    /// Batch: -executeMethod LeGamez.Editor.VehicleSnapshot.Render
    /// </summary>
    public static class VehicleSnapshot
    {
        [MenuItem("LeGamez/Render Vehicle Snapshots")]
        public static void Render()
        {
            string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Snapshots"));
            Directory.CreateDirectory(outDir);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sky = AssetDatabase.LoadAssetAtPath<Material>("Assets/Generated/Materials/Sky.mat");
            if (sky != null) RenderSettings.skybox = sky;
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.94f, 0.84f);
            sun.intensity = 1.3f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(38f, -35f, 0f);
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.74f, 0.88f);
            RenderSettings.ambientEquatorColor = new Color(0.58f, 0.62f, 0.60f);
            RenderSettings.ambientGroundColor = new Color(0.35f, 0.33f, 0.30f);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.localScale = Vector3.one * 5f;
            var groundMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Generated/Materials/World_Ground.mat");
            if (groundMat != null) ground.GetComponent<Renderer>().sharedMaterial = groundMat;

            Car.Create(new Vector3(-1.4f, 0f, 0f), 0f, 0);
            Car.Create(new Vector3(2.2f, 0f, 1.5f), 180f, 2);
            Bicycle.Create(new Vector3(-4.2f, 0f, 0.5f), 90f);

            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.fieldOfView = 40f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            var gm = LeGamez.Core.GameMaterials.Instance;
            var volume = new GameObject("Volume").AddComponent<Volume>();
            volume.isGlobal = true;
            if (gm != null) volume.sharedProfile = gm.postProfile;

            void Shot(string name, Vector3 pos, Vector3 look)
            {
                cam.transform.position = pos;
                cam.transform.LookAt(look);
                SceneSnapshot.RenderTo(cam, Path.Combine(outDir, $"vehicle_{name}.png"), 1600, 900);
            }
            Shot("front", new Vector3(3.5f, 2.0f, 7.5f), new Vector3(-0.5f, 0.7f, 0.5f));
            Shot("rear", new Vector3(-6.0f, 2.2f, -6.5f), new Vector3(-0.5f, 0.7f, 0.5f));
            Shot("bike", new Vector3(-4.2f, 1.3f, 2.8f), new Vector3(-4.2f, 0.55f, 0.5f));
        }
    }
}
