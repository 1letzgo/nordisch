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
        public float radius = 110f;

        RectTransform zone;
        Vector2 restPosition;
        int pointerId = int.MinValue;

        void Awake()
        {
            zone = (RectTransform)transform;
            restPosition = background.anchoredPosition;
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (pointerId != int.MinValue) return;
            pointerId = e.pointerId;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(zone, e.position, e.pressEventCamera, out var local);
            background.position = zone.TransformPoint(local);
            OnDrag(e);
        }

        public void OnDrag(PointerEventData e)
        {
            if (e.pointerId != pointerId) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(background, e.position, e.pressEventCamera, out var local);
            float scaledRadius = radius;
            Vector2 clamped = Vector2.ClampMagnitude(local, scaledRadius);
            handle.anchoredPosition = clamped;
            GameInput.TouchMove = clamped / scaledRadius;
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (e.pointerId != pointerId) return;
            pointerId = int.MinValue;
            handle.anchoredPosition = Vector2.zero;
            background.anchoredPosition = restPosition;
            GameInput.TouchMove = Vector2.zero;
        }

        void OnDisable()
        {
            pointerId = int.MinValue;
            GameInput.TouchMove = Vector2.zero;
        }
    }
}
