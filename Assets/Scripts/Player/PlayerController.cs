using LeGamez.Core;
using UnityEngine;

namespace LeGamez.Player
{
    /// <summary>Camera-relative third-person movement on a CharacterController, with smoothed speed and turning.</summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        [Header("Speed")]
        [Tooltip("Speed at full stick without sprint (brisk walk / jog).")]
        public float walkSpeed = 3.0f;
        [Tooltip("Speed at full stick with sprint enabled.")]
        public float runSpeed = 6.5f;
        [Tooltip("Seconds standing still before the sprint toggle switches itself off.")]
        public float runAutoOffDelay = 1.2f;
        public float deadZone = 0.12f;

        [Header("Smoothing")]
        public float accelerationTime = 0.12f;
        public float decelerationTime = 0.09f;
        public float turnSmoothTime = 0.08f;
        [Tooltip("Smoothing of the raw stick direction (touch input is noisy).")]
        public float inputSmoothTime = 0.03f;

        [Header("Jump")]
        public float jumpHeight = 1.1f;
        public float gravity = -20f;

        public Transform cameraTransform;

        CharacterController controller;
        ThirdPersonCamera orbit;
        float verticalVelocity;
        Vector3 planarVelocity;
        Vector3 planarVelocityRef;
        Vector2 smoothedInput;
        Vector2 smoothedInputRef;
        float yawVelocity;
        float idleTime;
        float sprintBlend;

        public float CurrentSpeed => planarVelocity.magnitude;
        public Vector2 SmoothedInput => smoothedInput;
        public bool IsGrounded => controller.isGrounded;
        /// <summary>When false (e.g. sitting in a vehicle) input is ignored.</summary>
        public bool InputEnabled { get; set; } = true;
        public event System.Action Jumped;
        /// <summary>False while riding a bicycle.</summary>
        public bool CanJump { get; set; } = true;

        void Awake() => controller = GetComponent<CharacterController>();

        void Update()
        {
            Vector2 raw = InputEnabled ? GameInput.Move : Vector2.zero;
            smoothedInput = Vector2.SmoothDamp(smoothedInput, ApplyDeadZone(raw), ref smoothedInputRef, inputSmoothTime);

            Vector3 forward = Vector3.forward, right = Vector3.right;
            if (cameraTransform != null)
            {
                // Use the camera's intended heading (not its lagging transform) as the input frame.
                if (orbit == null) orbit = cameraTransform.GetComponent<ThirdPersonCamera>();
                float yaw = orbit != null ? orbit.IntendedYaw : cameraTransform.eulerAngles.y;
                forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                right = Vector3.Cross(Vector3.up, forward);
            }

            float magnitude = Mathf.Clamp01(smoothedInput.magnitude);

            // Sprint toggle: ease in/out so speed and animation don't jump, auto-off after stopping.
            idleTime = magnitude < 0.05f ? idleTime + Time.deltaTime : 0f;
            if (idleTime > runAutoOffDelay) GameInput.RunToggled = false;
            sprintBlend = Mathf.MoveTowards(sprintBlend, InputEnabled && GameInput.Run ? 1f : 0f, Time.deltaTime * 3f);
            Vector3 wish = Vector3.zero;
            if (magnitude > 0.001f)
                wish = (forward * smoothedInput.y + right * smoothedInput.x).normalized * SpeedFor(magnitude);

            float smoothTime = wish.sqrMagnitude > planarVelocity.sqrMagnitude ? accelerationTime : decelerationTime;
            planarVelocity = Vector3.SmoothDamp(planarVelocity, wish, ref planarVelocityRef, smoothTime);

            // Face the direction of travel, smoothly.
            if (planarVelocity.sqrMagnitude > 0.04f)
            {
                float targetYaw = Mathf.Atan2(planarVelocity.x, planarVelocity.z) * Mathf.Rad2Deg;
                float yaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, targetYaw, ref yawVelocity, turnSmoothTime);
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            }

            if (controller.isGrounded)
            {
                verticalVelocity = -2f;
                if (InputEnabled && GameInput.ConsumeJump() && CanJump)
                {
                    verticalVelocity = Mathf.Sqrt(2f * jumpHeight * -gravity);
                    Jumped?.Invoke();
                }
            }
            verticalVelocity += gravity * Time.deltaTime;

            var flags = controller.Move((planarVelocity + Vector3.up * verticalVelocity) * Time.deltaTime);
            // Bleed off velocity pushed into walls so we don't "stick" and jitter against them.
            if ((flags & CollisionFlags.Sides) != 0) planarVelocity = controller.velocity.WithY(0f);

            // Safety net: never fall out of the world.
            if (transform.position.y < -20f) Teleport(transform.position + Vector3.up * 25f);
        }

        Vector2 ApplyDeadZone(Vector2 v)
        {
            float m = v.magnitude;
            if (m < deadZone) return Vector2.zero;
            return v / m * Mathf.InverseLerp(deadZone, 1f, Mathf.Min(m, 1f));
        }

        /// <summary>Stick deflection scales speed linearly up to walk or sprint top speed.</summary>
        float SpeedFor(float magnitude) => magnitude * Mathf.Lerp(walkSpeed, runSpeed, sprintBlend);

        public bool IsSprinting => sprintBlend > 0.5f;

        public void Teleport(Vector3 position)
        {
            controller.enabled = false;
            transform.position = position;
            controller.enabled = true;
            planarVelocity = planarVelocityRef = Vector3.zero;
            verticalVelocity = 0f;
        }
    }

    static class VectorExt
    {
        public static Vector3 WithY(this Vector3 v, float y) => new Vector3(v.x, y, v.z);
    }
}
