using LeGamez.Core;
using UnityEngine;

namespace LeGamez.Player
{
    /// <summary>Camera-relative third-person movement on a CharacterController, with smoothed speed and turning.</summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        [Header("Speed")]
        public float walkSpeed = 2.6f;
        public float runSpeed = 6f;
        [Tooltip("Stick deflection where walking turns into running (speed blends continuously).")]
        public float runBlendStart = 0.7f;
        public float deadZone = 0.12f;

        [Header("Smoothing")]
        public float accelerationTime = 0.18f;
        public float decelerationTime = 0.12f;
        public float turnSmoothTime = 0.12f;
        [Tooltip("Smoothing of the raw stick direction (touch input is noisy).")]
        public float inputSmoothTime = 0.06f;

        [Header("Jump")]
        public float jumpHeight = 1.1f;
        public float gravity = -20f;

        public Transform cameraTransform;

        CharacterController controller;
        float verticalVelocity;
        Vector3 planarVelocity;
        Vector3 planarVelocityRef;
        Vector2 smoothedInput;
        Vector2 smoothedInputRef;
        float yawVelocity;

        public float CurrentSpeed => planarVelocity.magnitude;
        public Vector2 SmoothedInput => smoothedInput;
        public bool IsGrounded => controller.isGrounded;
        /// <summary>When false (e.g. sitting in a vehicle) input is ignored.</summary>
        public bool InputEnabled { get; set; } = true;

        void Awake() => controller = GetComponent<CharacterController>();

        void Update()
        {
            Vector2 raw = InputEnabled ? GameInput.Move : Vector2.zero;
            smoothedInput = Vector2.SmoothDamp(smoothedInput, ApplyDeadZone(raw), ref smoothedInputRef, inputSmoothTime);

            Vector3 forward = Vector3.forward, right = Vector3.right;
            if (cameraTransform != null)
            {
                forward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
                right = Vector3.Cross(Vector3.up, forward);
            }

            float magnitude = Mathf.Clamp01(smoothedInput.magnitude);
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
                if (InputEnabled && GameInput.ConsumeJump()) verticalVelocity = Mathf.Sqrt(2f * jumpHeight * -gravity);
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

        /// <summary>Continuous speed curve: 0..runBlendStart walks, beyond blends into running.</summary>
        float SpeedFor(float magnitude)
        {
            if (magnitude <= runBlendStart) return walkSpeed * (magnitude / runBlendStart);
            return Mathf.Lerp(walkSpeed, runSpeed, Mathf.SmoothStep(0f, 1f, (magnitude - runBlendStart) / (1f - runBlendStart)));
        }

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
