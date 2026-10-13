using System.Collections.Generic;
using UnityEngine;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>Low-poly palette and layer heights. Colours are baked into vertex colours.</summary>
    public static class WorldStyle
    {
        // Material buckets (each becomes one material + collider policy).
        public const string MatBuildings = "Buildings"; // walls + roofs, MeshCollider
        public const string MatGround = "Ground";       // landuse / roads, no collider (base plane collides)
        public const string MatWater = "Water";
        public const string MatTrees = "Trees";             // crowns + bushes, no collider
        public const string MatTreeTrunks = "TreeTrunks";   // same material as Trees, MeshCollider
        public const string MatProps = "Props";             // lamps, hedges, walls, fences – MeshCollider
        public const string MatTerrain = "Terrain";         // faceted height field – MeshCollider, Ground material
        public const string MatDetails = "Details";         // window sills: Trees material (no facade data), no shadow/collider, culled near

        /// <summary>Which buckets get a MeshCollider.</summary>
        public static bool HasCollider(string mat) => mat == MatBuildings || mat == MatTreeTrunks || mat == MatProps || mat == MatTerrain;

        /// <summary>Buckets that share another bucket's material.</summary>
        public static string MaterialOf(string mat) =>
            mat == MatTreeTrunks ? MatTrees : (mat == MatProps || mat == MatTerrain) ? MatGround : mat == MatDetails ? MatTrees : mat;

        /// <summary>Render layer of <see cref="MatDetails"/>; the game camera culls it beyond a short distance.</summary>
        public const int DetailsLayer = 8;

        // Vertical layering to avoid z-fighting between overlapping flat surfaces.
        public const float YBase = 0f;
        public const float YLanduse = 0.02f;
        public const float YLeisure = 0.04f;
        public const float YWater = 0.05f;
        public const float YRoadMinor = 0.08f;
        public const float YRoadMajor = 0.10f;
        public const float YPath = 0.06f;

        public static Color32 Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }

        public static readonly Color32 Grass = Hex("#8DBF6A");
        public static readonly Color32 Sand = Hex("#E8D9A8");
        public static readonly Color32 Dune = Hex("#CFC48A");
        public static readonly Color32 Forest = Hex("#5E9A4E");
        public static readonly Color32 Residential = Hex("#C9C3B4");
        public static readonly Color32 Commercial = Hex("#D3C1BC");
        public static readonly Color32 Pedestrian = WithAlpha(Hex("#A89C88"), 90);
        public static readonly Color32 Parking = Hex("#A9A9A9");
        public static readonly Color32 Water = Hex("#5AA9D6");
        public static readonly Color32 Asphalt = Hex("#5D6166");
        public static readonly Color32 Paving = WithAlpha(Hex("#A29A8A"), 90);
        public static readonly Color32 Path = Hex("#D8C49A");
        public static readonly Color32 Cycleway = Hex("#B5574B");
        public static readonly Color32 Rail = Hex("#6E5A4A");
        public static readonly Color32 TreeLeaves = Hex("#4F8F3F");
        public static readonly Color32 TreeTrunk = Hex("#7A5638");
        public static readonly Color32 Bush = Hex("#6E9A55");
        public static readonly Color32 LampPole = Hex("#3E4247");
        public static readonly Color32 LampGlow = Hex("#FFF4D6");
        public static readonly Color32 Hedge = Hex("#55834A");
        public static readonly Color32 StoneWall = Hex("#A69C8C");
        public static readonly Color32 Fence = Hex("#8A8F94");
        public static readonly Color32 LaneMarking = Hex("#E9E6DC");

        /// <summary>Alpha carries a shader material code (90 = paving stones, see VertexColorLit).</summary>
        public static Color32 WithAlpha(Color32 c, byte a) => new Color32(c.r, c.g, c.b, a);

        public static Color32 GroundColor(string ground) => ground == "sand" ? Sand : Grass;

        // Wall colours by building type; slight random tint is applied per building.
        static readonly Color32 WallDefault = Hex("#E7DCC8");
        static readonly Color32 WallBrick = Hex("#B5654A"); // Nordwest-Klinker
        static readonly Color32 WallCommercial = Hex("#DCD6CC");
        static readonly Color32 WallChurch = Hex("#C9B79C");
        static readonly Color32 WallIndustrial = Hex("#A7ADB2");
        static readonly Color32 RoofRed = Hex("#A2433A");
        static readonly Color32 RoofGrey = Hex("#7B7F86");
        static readonly Color32 RoofDark = Hex("#4E4A48");

        public static Color32 WallColor(Dictionary<string, string> tags, System.Random rng)
        {
            var b = tags.Get("building") ?? tags.Get("building:part");
            Color32 baseColor;
            switch (b)
            {
                case "church": case "chapel": case "cathedral": baseColor = WallChurch; break;
                case "industrial": case "warehouse": case "garage": case "garages": case "shed": baseColor = WallIndustrial; break;
                case "commercial": case "retail": case "office": case "public": case "civic": baseColor = WallCommercial; break;
                default: baseColor = rng.NextDouble() < 0.55 ? WallBrick : WallDefault; break;
            }
            // Mapped colours ("black", "red", …) are often harsh – pull them halfway into the palette.
            var colour = tags.Get("building:colour");
            if (colour != null && ColorUtility.TryParseHtmlString(colour, out var parsed))
                baseColor = Color32.Lerp(parsed, baseColor, 0.55f);
            return Tint(baseColor, rng, 0.08f);
        }

        public static Color32 RoofColor(Dictionary<string, string> tags, System.Random rng, bool pitched)
        {
            Color32 baseColor = !pitched ? RoofGrey : rng.NextDouble() < 0.7 ? RoofRed : RoofDark;
            var colour = tags.Get("roof:colour");
            if (colour != null && ColorUtility.TryParseHtmlString(colour, out var parsed))
                baseColor = Color32.Lerp(parsed, baseColor, 0.45f);
            return Tint(baseColor, rng, 0.06f);
        }

        public static Color32 Tint(Color32 c, System.Random rng, float amount)
        {
            float f = 1f + ((float)rng.NextDouble() * 2f - 1f) * amount;
            return new Color32(
                (byte)Mathf.Clamp(c.r * f, 0, 255),
                (byte)Mathf.Clamp(c.g * f, 0, 255),
                (byte)Mathf.Clamp(c.b * f, 0, 255), 255);
        }
    }
}
