using LeGamez.Core;
using UnityEngine;

namespace LeGamez.Player
{
    /// <summary>
    /// Smoothed orbit camera: swipe/mouse look with inertia, damped follow, gentle recentering
    /// behind the player only when walking straight ahead, and soft wall collision.
    /// </summary>
    public class ThirdPersonCamera : MonoBehaviour
    {
        public Transform target;
        public Vector3 pivotOffset = new Vector3(0f, 1.6f, 0f);
        public float distance = 6f;
        public float minDistance = 2.5f;
        public float maxDistance = 14f;
        public float minPitch = -10f;
        public float maxPitch = 70f;
        public float collisionRadius = 0.25f;
        public LayerMask collisionMask = ~(1 << 2); // everything except "Ignore Raycast" (player)

        [Header("Smoothing")]
        public float lookSmoothTime = 0.07f;
        public float followSmoothTime = 0.08f;
        public float collisionInTime = 0.05f;
        public float collisionOutTime = 0.35f;
        [Tooltip("Degrees/second the camera recenters behind the player while running straight.")]
        public float recenterSpeed = 35f;
        public float recenterDelay = 1.5f;

        float targetYaw, targetPitch = 16f;
        float yaw, pitch = 16f;
        float yawVel, pitchVel;
        Vector3 pivot, pivotVel;
        float currentDistance, distanceVel;
        float idleLookTime;
        Camera cam;
        PlayerController player;

        void Start()
        {
            if (target == null) return;
            targetYaw = yaw = target.eulerAngles.y;
            pivot = target.position + pivotOffset;
            currentDistance = distance;
            player = target.GetComponent<PlayerController>();
            cam = GetComponent<Camera>();
        }

        void LateUpdate()
        {
            if (target == null) return;

            // Portrait screens are narrow: widen the vertical FOV and pull back a bit.
            bool portrait = Screen.height > Screen.width;
            if (cam != null) cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, portrait ? 70f : 55f, 1f - Mathf.Exp(-8f * Time.deltaTime));
            float distanceScale = portrait ? 1.15f : 1f;

            Vector2 look = GameInput.ConsumeLook();
            if (look.sqrMagnitude > 0.0001f) idleLookTime = 0f;
            else idleLookTime += Time.deltaTime;

            targetYaw += look.x;
            targetPitch = Mathf.Clamp(targetPitch - look.y, minPitch, maxPitch);
            distance = Mathf.Clamp(distance - GameInput.Zoom() * 2f, minDistance, maxDistance);

            // Recenter only while the stick points forward – sideways input must not feed back into the camera.
            if (player != null && idleLookTime > recenterDelay)
            {
                Vector2 input = player.SmoothedInput;
                float straightness = Mathf.Clamp01((input.y - 0.5f) * 2f) * Mathf.Clamp01(1f - Mathf.Abs(input.x) * 2.5f);
                if (straightness > 0f)
                    targetYaw = Mathf.MoveTowardsAngle(targetYaw, target.eulerAngles.y, recenterSpeed * straightness * Time.deltaTime);
            }

            yaw = Mathf.SmoothDampAngle(yaw, targetYaw, ref yawVel, lookSmoothTime);
            pitch = Mathf.SmoothDamp(pitch, targetPitch, ref pitchVel, lookSmoothTime);
            pivot = Vector3.SmoothDamp(pivot, target.position + pivotOffset, ref pivotVel, followSmoothTime);

            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 back = rotation * Vector3.back;

            float wanted = distance * distanceScale;
            if (Physics.SphereCast(pivot, collisionRadius, back, out var hit, wanted, collisionMask, QueryTriggerInteraction.Ignore))
                wanted = Mathf.Max(0.8f, hit.distance - 0.1f);
            currentDistance = Mathf.SmoothDamp(currentDistance, wanted, ref distanceVel,
                wanted < currentDistance ? collisionInTime : collisionOutTime);

            transform.SetPositionAndRotation(pivot + back * currentDistance, rotation);
        }
    }
}
