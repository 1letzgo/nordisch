using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace LeGamez.Core
{
    /// <summary>
    /// Merges on-screen controls (joystick, look area, buttons) with keyboard/mouse/gamepad
    /// so gameplay code reads a single source.
    /// </summary>
    public static class GameInput
    {
        // Written by on-screen UI controls each frame.
        public static Vector2 TouchMove;
        public static Vector2 TouchLookDelta;
        static bool touchJumpQueued;
        static bool touchInteractQueued;

        public static void QueueJump() => touchJumpQueued = true;
        public static void QueueInteract() => touchInteractQueued = true;

        public static Vector2 Move
        {
            get
            {
                Vector2 v = TouchMove;
#if ENABLE_INPUT_SYSTEM
                var kb = Keyboard.current;
                if (kb != null)
                {
                    Vector2 k = Vector2.zero;
                    if (kb.wKey.isPressed || kb.upArrowKey.isPressed) k.y += 1;
                    if (kb.sKey.isPressed || kb.downArrowKey.isPressed) k.y -= 1;
                    if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) k.x += 1;
                    if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) k.x -= 1;
                    if (k != Vector2.zero) v = k.normalized * (kb.leftShiftKey.isPressed ? 1f : 0.6f);
                }
                var pad = Gamepad.current;
                if (pad != null && pad.leftStick.ReadValue().sqrMagnitude > 0.02f) v = pad.leftStick.ReadValue();
#endif
                return Vector2.ClampMagnitude(v, 1f);
            }
        }

        /// <summary>Camera rotation delta in degrees for this frame.</summary>
        public static Vector2 ConsumeLook()
        {
            Vector2 d = TouchLookDelta * 0.25f;
            TouchLookDelta = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.isPressed) d += mouse.delta.ReadValue() * 0.2f;
            var pad = Gamepad.current;
            if (pad != null) d += pad.rightStick.ReadValue() * 180f * Time.deltaTime;
#endif
            return d;
        }

        public static bool ConsumeJump()
        {
            bool j = touchJumpQueued;
            touchJumpQueued = false;
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) j = true;
            if (Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame) j = true;
#endif
            return j;
        }

        public static bool ConsumeInteract()
        {
            bool i = touchInteractQueued;
            touchInteractQueued = false;
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame) i = true;
            if (Gamepad.current != null && Gamepad.current.buttonWest.wasPressedThisFrame) i = true;
#endif
            return i;
        }

        public static float Zoom()
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null) return Mouse.current.scroll.ReadValue().y * 0.01f;
#endif
            return 0f;
        }
    }
}
