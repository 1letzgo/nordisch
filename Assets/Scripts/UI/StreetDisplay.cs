using LeGamez.World;
using UnityEngine;
using UnityEngine.UI;

namespace LeGamez.UI
{
    /// <summary>Shows the street the player is on (from the area's <see cref="StreetIndex"/>) in a HUD label.</summary>
    public class StreetDisplay : MonoBehaviour
    {
        public Text label;
        public float maxDistance = 30f;
        string current;
        float next;

        void Update()
        {
            if (label == null || Time.time < next) return;
            next = Time.time + 0.3f;
            var area = AreaInfo.Current;
            var player = NavState.Player;
            if (area == null || area.streets == null || player == null) { label.text = ""; return; }
            current = area.streets.Nearest(player.position, maxDistance, current);
            label.text = current ?? "";
        }
    }
}
