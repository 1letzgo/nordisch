using LeGamez.Core;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LeGamez.UI
{
    /// <summary>Transparent zone; dragging inside rotates the camera.</summary>
    public class TouchLookArea : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public float sensitivity = 1f;
        int pointerId = int.MinValue;

        public void OnPointerDown(PointerEventData e)
        {
            if (pointerId == int.MinValue) pointerId = e.pointerId;
        }

        public void OnDrag(PointerEventData e)
        {
            if (e.pointerId != pointerId) return;
            // Normalise by screen DPI-ish scale so the feel is the same on all iPhones.
            float scale = 1080f / Mathf.Max(1f, Screen.height);
            GameInput.TouchLookDelta += e.delta * scale * sensitivity;
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (e.pointerId == pointerId) pointerId = int.MinValue;
        }
    }
}
