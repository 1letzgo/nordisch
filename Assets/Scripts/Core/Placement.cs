using UnityEngine;

namespace LeGamez.Core
{
    /// <summary>
    /// Finds a safe spot for the player: on the terrain surface (not a roof, prop or water) with room
    /// for the character capsule. Used for beaming and for resuming a saved position.
    /// </summary>
    public static class Placement
    {
        public const int PlayerLayer = 2;   // "Ignore Raycast": player, thin props, bikes
        public const int WaterLayer = 4;    // built-in "Water": draped water surfaces (collider, not walkable)
        const float CapsuleRadius = 0.45f;
        const float CapsuleHeight = 1.8f;

        static readonly RaycastHit[] Hits = new RaycastHit[16];
        static readonly Collider[] Overlaps = new Collider[16];

        /// <summary>Nearest valid ground point to <paramref name="wanted"/> (searching rings up to <paramref name="maxRadius"/>).</summary>
        public static bool TryFindFreeGround(Vector3 wanted, Transform ignore, out Vector3 spot, float maxRadius = 40f)
        {
            for (float r = 0f; r <= maxRadius; r += 1.5f)
            {
                int steps = r == 0f ? 1 : Mathf.CeilToInt(2f * Mathf.PI * r / 1.5f);
                float phase = r * 0.37f; // rotate rings a little so they don't align with street grids
                for (int i = 0; i < steps; i++)
                {
                    float a = phase + i * Mathf.PI * 2f / steps;
                    var p = wanted + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (IsFree(p, ignore, out spot)) return true;
                }
            }
            spot = wanted;
            return false;
        }

        /// <summary>True if the column at p reaches the terrain first and the character fits there.</summary>
        public static bool IsFree(Vector3 p, Transform ignore, out Vector3 ground)
        {
            ground = p;
            // 1. Topmost surface must be terrain (roofs, props and water are rejected).
            int n = Physics.RaycastNonAlloc(new Vector3(p.x, 400f, p.z), Vector3.down, Hits, 800f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            Collider top = null;
            for (int i = 0; i < n; i++)
            {
                var c = Hits[i].collider;
                if (ignore != null && c.transform.IsChildOf(ignore)) continue;
                if (Hits[i].distance < best) { best = Hits[i].distance; top = c; ground = Hits[i].point; }
            }
            if (top == null || !IsTerrain(top)) return false;

            // 2. Room for the capsule: anything solid other than terrain blocks the spot.
            Vector3 a = ground + Vector3.up * (CapsuleRadius + 0.15f);
            Vector3 b = ground + Vector3.up * (CapsuleHeight - CapsuleRadius);
            int m = Physics.OverlapCapsuleNonAlloc(a, b, CapsuleRadius + 0.1f, Overlaps, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < m; i++)
            {
                var c = Overlaps[i];
                if (IsTerrain(c)) continue;
                if (ignore != null && c.transform.IsChildOf(ignore)) continue;
                return false;
            }
            return true;
        }

        static bool IsTerrain(Collider c) => c.name.StartsWith("Terrain") || c.name == "GroundCollider";
    }
}
