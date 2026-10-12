using UnityEngine;

namespace LeGamez.Core
{
    /// <summary>
    /// Materials created by the editor setup and loaded at runtime from Resources, so their shaders
    /// are guaranteed to be included in player builds (Shader.Find alone would get stripped).
    /// </summary>
    public class GameMaterials : ScriptableObject
    {
        public const string ResourcePath = "GameMaterials";

        public Material playerBody;
        public Material playerSkin;
        public Material playerAccent;
        public Material poiOpen;
        public Material poiDone;
        public UnityEngine.Rendering.VolumeProfile postProfile;

        [Header("Player character (Kenney Mini Characters, CC0)")]
        public System.Collections.Generic.List<GameObject> characters = new System.Collections.Generic.List<GameObject>();
        public Material characterMaterial;
        public RuntimeAnimatorController characterController;
        public float characterScale = 1f;
        public float characterFootOffset;
        public Material pedestalMaterial;
        public Material vehicleMaterial;

        static GameMaterials instance;

        public static GameMaterials Instance
        {
            get
            {
                if (instance == null) instance = Resources.Load<GameMaterials>(ResourcePath);
                if (instance == null) Debug.LogError("[LeGamez] Resources/GameMaterials missing – run LeGamez/Setup Project.");
                return instance;
            }
        }
    }
}
