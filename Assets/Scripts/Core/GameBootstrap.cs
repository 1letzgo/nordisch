using LeGamez.Player;
using LeGamez.Quests;
using LeGamez.UI;
using LeGamez.World;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace LeGamez.Core
{
    /// <summary>
    /// Placed in every area scene by the importer. Spawns player, camera, HUD and gameplay
    /// systems at runtime so area scenes contain only world data.
    /// </summary>
    public class GameBootstrap : MonoBehaviour
    {
        void Start()
        {
            Application.targetFrameRate = 60;
            // Player/props layer walks through water surfaces (they only exist for placement checks).
            Physics.IgnoreLayerCollision(Placement.PlayerLayer, Placement.WaterLayer, true);
            var area = AreaInfo.Current;
            Vector3 spawn = area != null && area.spawnPoint != null ? area.spawnPoint.position : Vector3.up;

            EnsureEventSystem();
            // Resume where the player left this area (if the saved spot is still inside it).
            bool resumed = false;
            Vector3 saved = default;
            float savedYaw = 0f;
            if (area != null && PositionMemory.TryLoad(area.MemoryKey, out saved, out savedYaw)
                && area.Contains(saved, 2f))
            {
                // Re-validate: data may have changed since saving (new buildings, terrain, props).
                Placement.TryFindFreeGround(saved, null, out var safe, 20f);
                spawn = safe + Vector3.up * 0.15f;
                resumed = true;
            }
            var player = CreatePlayer(spawn);
            if (resumed) player.transform.rotation = Quaternion.Euler(0f, savedYaw, 0f);
            if (area != null) player.gameObject.AddComponent<PositionMemory>().areaId = area.MemoryKey;
            var cam = CreateCamera(player.transform);
            player.cameraTransform = cam.transform;

            NavState.Player = player.transform;
            NavState.View = cam.transform;

            var hud = Hud.Create(area != null ? area.displayName : "Nordisch",
                area != null ? area.attribution : "© OpenStreetMap-Mitwirkende");

            // Current street name under the area title.
            var street = hud.gameObject.AddComponent<StreetDisplay>();
            street.label = hud.infoLabel;

            var vehicles = new GameObject("Vehicles").AddComponent<LeGamez.Vehicles.VehicleManager>();
            vehicles.player = player;
            vehicles.hud = hud;
        }

        static void EnsureEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
            go.AddComponent<InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }

        static PlayerController CreatePlayer(Vector3 position)
        {
            var root = new GameObject("Player");
            root.layer = 2; // Ignore Raycast – keeps the camera collision probe off the player
            root.transform.position = position;
            var cc = root.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.35f;
            cc.center = new Vector3(0, 0.9f, 0);
            cc.stepOffset = 0.4f;
            cc.slopeLimit = 50f;

            if (PlayerAvatar.TryCreate(root)) return root.AddComponent<PlayerController>();

            // Fallback placeholder figure if no character assets are available.
            var mats = GameMaterials.Instance;
            var body = Part(root.transform, PrimitiveType.Capsule, new Vector3(0, 0.85f, 0), new Vector3(0.6f, 0.65f, 0.45f), mats.playerBody);
            Part(root.transform, PrimitiveType.Sphere, new Vector3(0, 1.62f, 0), Vector3.one * 0.36f, mats.playerSkin);
            Part(root.transform, PrimitiveType.Cube, new Vector3(0, 1.62f, 0.18f), new Vector3(0.12f, 0.08f, 0.08f), mats.playerAccent);
            body.AddComponent<PlayerBob>();

            return root.AddComponent<PlayerController>();
        }

        static GameObject Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.layer = 2;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
            return go;
        }

        static Camera CreateCamera(Transform target)
        {
            var existing = Camera.main;
            var cam = existing != null ? existing : new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
            cam.tag = "MainCamera";
            cam.nearClipPlane = 0.2f;
            cam.farClipPlane = 750f; // fog ends at 650 m – nothing beyond needs drawing
            cam.fieldOfView = 55f;
            // Small detail geometry (window sills – layer 8 from the importer) only up close.
            var cull = new float[32];
            cull[8] = 120f;
            cam.layerCullDistances = cull;
            cam.layerCullSpherical = true;
            cam.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            cam.backgroundColor = RenderSettings.fog ? RenderSettings.fogColor : new Color(0.7f, 0.82f, 0.95f);
            var urpCam = cam.GetUniversalAdditionalCameraData();
            urpCam.renderPostProcessing = true;
            urpCam.antialiasing = AntialiasingMode.None; // MSAA from the pipeline asset

            var profile = GameMaterials.Instance != null ? GameMaterials.Instance.postProfile : null;
            if (profile != null)
            {
                var volume = new GameObject("PostFX").AddComponent<Volume>();
                volume.isGlobal = true;
                volume.sharedProfile = profile;
            }
            var follow = cam.gameObject.AddComponent<ThirdPersonCamera>();
            follow.target = target;
            return cam;
        }
    }
}
