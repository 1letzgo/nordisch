using LeGamez.Core;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LeGamez.UI
{
    /// <summary>
    /// Floating joystick: touch anywhere inside this (transparent) zone, the stick appears under
    /// the finger. Writes to <see cref="GameInput.TouchMove"/>.
    /// </summary>
    public class VirtualJoystick : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public RectTransform background;
        public RectTransform handle;
        public float radius = 125f;
        [Tooltip("Stick deflection (0..1 of radius) that already counts as full input.")]
        public float fullAt = 0.85f;

        int pointerId = int.MinValue;
        Vector2 restPosition;
        bool restCaptured;

        RectTransform Zone => (RectTransform)transform;

        // Fields are assigned after AddComponent, so capture the rest position lazily.
        void CaptureRest()
        {
            if (restCaptured || background == null) return;
            restPosition = background.anchoredPosition;
            restCaptured = true;
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (pointerId != int.MinValue || background == null) return;
            CaptureRest();
            pointerId = e.pointerId;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(Zone, e.position, e.pressEventCamera, out var local))
                background.position = Zone.TransformPoint(local);
            UpdateStick(e);
        }

        public void OnDrag(PointerEventData e)
        {
            if (e.pointerId == pointerId) UpdateStick(e);
        }

        void UpdateStick(PointerEventData e)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(background, e.position, e.pressEventCamera, out var local)) return;
            // Dragging past the rim pulls the whole stick along, so the thumb never "loses" it.
            if (local.magnitude > radius)
            {
                Vector2 excess = local - local.normalized * radius;
                background.position += background.TransformVector(excess);
                local -= excess;
            }
            handle.anchoredPosition = local;
            GameInput.TouchMove = Vector2.ClampMagnitude(local / (radius * fullAt), 1f);
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (e.pointerId != pointerId) return;
            Release();
        }

        /// <summary>Called when the layout changes (rotation) so the stick re-anchors.</summary>
        public void ResetRest(Vector2 anchoredRest)
        {
            restPosition = anchoredRest;
            restCaptured = true;
            Release();
        }

        void Release()
        {
            pointerId = int.MinValue;
            if (handle != null) handle.anchoredPosition = Vector2.zero;
            if (background != null && restCaptured) background.anchoredPosition = restPosition;
            GameInput.TouchMove = Vector2.zero;
        }

        void OnDisable() => Release();
    }
}
