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
        public const string MatTrees = "Trees";

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
        public static readonly Color32 Pedestrian = Hex("#D9D2C5");
        public static readonly Color32 Parking = Hex("#A9A9A9");
        public static readonly Color32 Water = Hex("#5AA9D6");
        public static readonly Color32 Asphalt = Hex("#5D6166");
        public static readonly Color32 Paving = Hex("#BDB5A6");
        public static readonly Color32 Path = Hex("#D8C49A");
        public static readonly Color32 Cycleway = Hex("#B5574B");
        public static readonly Color32 Rail = Hex("#6E5A4A");
        public static readonly Color32 TreeLeaves = Hex("#4F8F3F");
        public static readonly Color32 TreeTrunk = Hex("#7A5638");

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
