using LeGamez.Core;
using UnityEngine;

namespace LeGamez.Player
{
    /// <summary>Orbit camera with swipe/mouse look, auto-follow behind the target and wall collision.</summary>
    public class ThirdPersonCamera : MonoBehaviour
    {
        public Transform target;
        public Vector3 pivotOffset = new Vector3(0f, 1.6f, 0f);
        public float distance = 6f;
        public float minDistance = 2.5f;
        public float maxDistance = 14f;
        public float minPitch = -10f;
        public float maxPitch = 70f;
        public float collisionRadius = 0.3f;
        public LayerMask collisionMask = ~(1 << 2); // everything except "Ignore Raycast" (player)
        [Tooltip("Degrees/second the camera drifts behind the moving target when not swiping.")]
        public float autoFollowSpeed = 60f;

        float yaw;
        float pitch = 18f;
        float currentDistance;
        float idleLookTime;

        void Start()
        {
            if (target != null) yaw = target.eulerAngles.y;
            currentDistance = distance;
        }

        void LateUpdate()
        {
            if (target == null) return;

            Vector2 look = GameInput.ConsumeLook();
            if (look.sqrMagnitude > 0.0001f) idleLookTime = 0f;
            else idleLookTime += Time.deltaTime;

            yaw += look.x;
            pitch = Mathf.Clamp(pitch - look.y, minPitch, maxPitch);
            distance = Mathf.Clamp(distance - GameInput.Zoom() * 2f, minDistance, maxDistance);

            // Drift behind the player while moving and the user is not steering the camera.
            var player = target.GetComponent<PlayerController>();
            float speed = player != null ? player.CurrentSpeed : 0f;
            if (idleLookTime > 1.2f && speed > 0.5f)
                yaw = Mathf.MoveTowardsAngle(yaw, target.eulerAngles.y, autoFollowSpeed * Time.deltaTime * Mathf.Clamp01(speed / 4f));

            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 pivot = target.position + pivotOffset;
            Vector3 back = rotation * Vector3.back;

            float wanted = distance;
            if (Physics.SphereCast(pivot, collisionRadius, back, out var hit, distance, collisionMask, QueryTriggerInteraction.Ignore))
                wanted = Mathf.Max(0.5f, hit.distance - 0.1f);
            // Snap in fast (avoid clipping), ease out slowly.
            currentDistance = wanted < currentDistance ? wanted : Mathf.Lerp(currentDistance, wanted, 1f - Mathf.Exp(-4f * Time.deltaTime));

            transform.SetPositionAndRotation(pivot + back * currentDistance, rotation);
        }
    }
}
