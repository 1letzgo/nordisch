using UnityEngine;
using UnityEngine.EventSystems;

namespace LeGamez.UI
{
    /// <summary>Fires on touch-down (not release) for responsive action buttons.</summary>
    public class TapButton : MonoBehaviour, IPointerDownHandler
    {
        public System.Action onPress;

        public void OnPointerDown(PointerEventData e) => onPress?.Invoke();
    }
}
