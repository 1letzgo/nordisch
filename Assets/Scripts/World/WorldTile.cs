using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.World
{
    /// <summary>
    /// Geometry of one square of an area (<see cref="WorldStreamer.TileSize"/> metres), written by the importer
    /// (Assets/Generated/&lt;id&gt;/tiles/, meshes as sub-assets) and loaded/unloaded at runtime by <see cref="WorldStreamer"/>.
    /// </summary>
    public class WorldTile : ScriptableObject
    {
        public const byte FlagCollider = 1, FlagShadows = 2;
        /// <summary>Material indices (GameMaterials.worldMaterials), same order as the importer's material names.</summary>
        public const int MaterialBuildings = 0, MaterialGround = 1, MaterialWater = 2, MaterialTrees = 3;
        public static readonly string[] MaterialNames = { "Buildings", "Ground", "Water", "Trees" };

        [System.Serializable]
        public struct Part
        {
            public Mesh mesh;
            /// <summary>Index into <see cref="WorldStreamer.materials"/> (= GameMaterials.worldMaterials, see MaterialNames).</summary>
            public int material;
            public byte flags;
            public int layer;
            /// <summary>Group name in the hierarchy (importer material key: Buildings, Terrain, Trees …).</summary>
            public string group;
        }

        /// <summary>One tree crown, trunk, bush or lamp drawn from a shared prototype mesh (<see cref="InstancePrototypes"/>,
        /// GPU instancing via <see cref="InstancedProps"/>) instead of geometry baked into the tile.</summary>
        [System.Serializable]
        public struct Instance
        {
            /// <summary>Prototype index (InstancePrototypes.Trunk, Crown …).</summary>
            public byte proto;
            /// <summary>World position of the prototype's origin (trunk/bush/lamp foot, crown centre, conifer crown base).</summary>
            public Vector3 position;
            /// <summary>Degrees, clockwise from north (Quaternion.Euler(0, yaw, 0)).</summary>
            public float yaw;
            /// <summary>Non-uniform scale: crown radius / height factor / crown radius, trunk radius / length / radius …</summary>
            public Vector3 scale;
            /// <summary>Actual colour; the tint is colour / prototype reference colour.</summary>
            public Color32 color;
        }

        public Vector2Int key;
        public List<Part> parts = new List<Part>();
        public List<Instance> instances = new List<Instance>();
    }
}
