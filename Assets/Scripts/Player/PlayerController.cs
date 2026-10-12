using LeGamez.Core;
using UnityEngine;

namespace LeGamez.Player
{
    /// <summary>Camera-relative third-person movement on a CharacterController.</summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        public float walkSpeed = 2.2f;
        public float runSpeed = 6f;
        public float runThreshold = 0.85f;
        public float turnSpeed = 720f;
        public float jumpHeight = 1.1f;
        public float gravity = -20f;
        public Transform cameraTransform;

        CharacterController controller;
        float verticalVelocity;
        Vector3 planarVelocity;

        public float CurrentSpeed => planarVelocity.magnitude;
        public bool IsGrounded => controller.isGrounded;
        /// <summary>When false (e.g. sitting in a vehicle) input is ignored.</summary>
        public bool InputEnabled { get; set; } = true;

        void Awake() => controller = GetComponent<CharacterController>();

        void Update()
        {
            Vector2 input = InputEnabled ? GameInput.Move : Vector2.zero;
            Vector3 forward = Vector3.forward, right = Vector3.right;
            if (cameraTransform != null)
            {
                forward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
                right = Vector3.Cross(Vector3.up, forward);
            }

            float magnitude = input.magnitude;
            float speed = magnitude >= runThreshold ? runSpeed : walkSpeed * Mathf.InverseLerp(0f, runThreshold, magnitude) * 1.6f;
            Vector3 wish = (forward * input.y + right * input.x).normalized * speed;
            planarVelocity = Vector3.MoveTowards(planarVelocity, wish, 30f * Time.deltaTime);

            if (wish.sqrMagnitude > 0.01f)
            {
                var target = Quaternion.LookRotation(wish, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * Time.deltaTime);
            }

            if (controller.isGrounded)
            {
                verticalVelocity = -2f;
                if (InputEnabled && GameInput.ConsumeJump()) verticalVelocity = Mathf.Sqrt(2f * jumpHeight * -gravity);
            }
            verticalVelocity += gravity * Time.deltaTime;

            controller.Move((planarVelocity + Vector3.up * verticalVelocity) * Time.deltaTime);

            // Safety net: never fall out of the world.
            if (transform.position.y < -20f) Teleport(transform.position + Vector3.up * 25f);
        }

        public void Teleport(Vector3 position)
        {
            controller.enabled = false;
            transform.position = position;
            controller.enabled = true;
            planarVelocity = Vector3.zero;
            verticalVelocity = 0f;
        }
    }
}
