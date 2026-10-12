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
        [Header("Auto follow")]
        [Tooltip("Degrees/second the camera turns with sideways stick input (turning a corner).")]
        public float turnFollowSpeed = 110f;
        [Tooltip("Degrees/second the camera recenters behind the player while walking forward.")]
        public float recenterSpeed = 60f;
        [Tooltip("Seconds after a manual swipe before auto follow takes over again.")]
        public float manualLookHold = 0.6f;
        public float defaultPitch = 16f;

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

            // Auto follow (Zelda/Genshin style): sideways input swings the camera round so the player
            // runs a curve instead of strafing across the screen; forward input recentres behind them.
            if (player != null && idleLookTime > manualLookHold)
            {
                Vector2 input = player.SmoothedInput;
                float moving = Mathf.Clamp01(player.CurrentSpeed / 2f);
                if (moving > 0f)
                {
                    // Lateral swing, eased in so tiny stick wobble doesn't rotate the view.
                    float lateral = Mathf.Sign(input.x) * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.15f, 0.9f, Mathf.Abs(input.x)));
                    if (input.y < -0.3f) lateral *= 0.3f; // walking towards the camera: don't spin
                    targetYaw += lateral * turnFollowSpeed * moving * Time.deltaTime;

                    float forwardness = Mathf.Clamp01(input.y * 1.5f) * Mathf.Clamp01(1f - Mathf.Abs(input.x) * 1.5f);
                    if (forwardness > 0f)
                        targetYaw = Mathf.MoveTowardsAngle(targetYaw, target.eulerAngles.y, recenterSpeed * forwardness * moving * Time.deltaTime);
                }
                // Drift pitch back to the default angle after looking up/down.
                if (idleLookTime > 2f)
                    targetPitch = Mathf.MoveTowards(targetPitch, defaultPitch, 20f * Time.deltaTime);
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
