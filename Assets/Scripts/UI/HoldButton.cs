using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LeGamez.UI
{
    /// <summary>
    /// Button that reports whether it is held (pedals, steering). Multi-touch safe: each button tracks its own
    /// finger; sliding a finger onto the button while pressing also counts, sliding off releases.
    /// </summary>
    public class HoldButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public bool Held => pointer != int.MinValue;
        public Graphic tint;
        public Color idleColor, heldColor;

        int pointer = int.MinValue;

        public void OnPointerDown(PointerEventData e) => Press(e.pointerId);
        public void OnPointerUp(PointerEventData e) { if (e.pointerId == pointer) Release(); }
        public void OnPointerExit(PointerEventData e) { if (e.pointerId == pointer) Release(); }

        public void OnPointerEnter(PointerEventData e)
        {
            // A finger already down elsewhere (e.g. sliding from ◀ to ▶) takes this button over.
            if (!Held && e.eligibleForClick && e.pointerPress != null) Press(e.pointerId);
        }

        void Press(int id)
        {
            pointer = id;
            if (tint != null) tint.color = heldColor;
        }

        void Release()
        {
            pointer = int.MinValue;
            if (tint != null) tint.color = idleColor;
        }

        void OnDisable() => Release();
    }
}
