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

        [System.Serializable]
        public struct Part
        {
            public Mesh mesh;
            /// <summary>Index into <see cref="WorldStreamer.materials"/>.</summary>
            public int material;
            public byte flags;
            public int layer;
            /// <summary>Group name in the hierarchy (importer material key: Buildings, Terrain, Trees …).</summary>
            public string group;
        }

        public Vector2Int key;
        public List<Part> parts = new List<Part>();
    }
}
