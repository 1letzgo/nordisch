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

        /// <summary>
        /// Set while riding: the vehicle moves only along its heading and turns like a real one (bicycle model) –
        /// front wheel steers, the rear axle is the pivot, turn rate limited by wheelbase/steer angle and grip.
        /// </summary>
        public class VehicleSteering
        {
            public float axleBase = 2.5f;      // wheelbase (m)
            public float rearAxle = 1.25f;     // pivot -> rear axle (m)
            public float maxSteer = 32f;       // front wheel angle at standstill (degrees)
            public float lateralAccel = 6f;    // m/s², caps the turn rate at speed
            public float maxSpeed = 14f;       // full gas (m/s)
            public float accelTime = 3f;       // 0 -> maxSpeed at full gas (s, roughly)
            public float brakeDecel = 9f;      // m/s² at full brake
            public float coastDecel = 1.5f;    // m/s² rolling without pedals
            public float reverseSpeed = 0f;    // 0 = no reverse gear
        }

        public VehicleSteering Steering { get; set; }
        float driveSpeed;          // signed, along the heading (negative = rolling backwards)
        float steerSmoothed, brakeHold;

        /// <summary>Current front wheel angle (degrees, + = right) – for wheel visuals.</summary>
        public float SteerAngle { get; private set; }
        /// <summary>Reverse gear engaged (brake held at standstill).</summary>
        public bool Reversing { get; private set; }

        public float CurrentSpeed => planarVelocity.magnitude;
        /// <summary>Speed along the heading; negative while reversing a vehicle.</summary>
        public float SignedSpeed => Steering != null ? driveSpeed : CurrentSpeed;
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

            Vector3 pivotShift = Vector3.zero;
            if (Steering != null)
                pivotShift = Drive();
            else
            {
                float smoothTime = wish.sqrMagnitude > planarVelocity.sqrMagnitude ? accelerationTime : decelerationTime;
                planarVelocity = Vector3.SmoothDamp(planarVelocity, wish, ref planarVelocityRef, smoothTime);

                // Face the direction of travel, smoothly.
                if (planarVelocity.sqrMagnitude > 0.04f)
                {
                    float targetYaw = Mathf.Atan2(planarVelocity.x, planarVelocity.z) * Mathf.Rad2Deg;
                    float yaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, targetYaw, ref yawVelocity, turnSmoothTime);
                    transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                }
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

            var flags = controller.Move((planarVelocity + Vector3.up * verticalVelocity) * Time.deltaTime + pivotShift);
            // Bleed off velocity pushed into walls so we don't "stick" and jitter against them.
            if ((flags & CollisionFlags.Sides) != 0)
            {
                if (Steering != null)
                {
                    driveSpeed = Vector3.Dot(controller.velocity.WithY(0f), transform.forward);
                    planarVelocity = transform.forward * driveSpeed;
                }
                else planarVelocity = controller.velocity.WithY(0f);
            }

            // Safety net: never fall out of the world.
            if (transform.position.y < -20f) Teleport(transform.position + Vector3.up * 25f);
        }

        /// <summary>
        /// Vehicle step with pedals: gas/brake change the speed along the heading (brake held at standstill engages
        /// reverse, gas engages drive again), ◀ ▶ turn the front wheels – smoothly in, quicker back to centre, less
        /// at speed. Turn rate from the bicycle model (speed / wheelbase · tan steer), capped by grip.
        /// Returns the extra move that keeps the rear axle on its path.
        /// </summary>
        Vector3 Drive()
        {
            var s = Steering;
            float dt = Time.deltaTime;
            float gas = InputEnabled ? GameInput.Throttle : 0f;
            float brake = InputEnabled ? GameInput.Brake : 0f;
            bool onGas = gas > 0.05f, onBrake = brake > 0.05f;

            // Gears: gas at (near) standstill -> D; brake held a moment at standstill -> R.
            if (onGas && driveSpeed > -0.3f) Reversing = false;
            if (onBrake && !onGas && s.reverseSpeed > 0f && Mathf.Abs(driveSpeed) < 0.3f)
            {
                brakeHold += dt;
                if (brakeHold > 0.3f) Reversing = true;
            }
            else brakeHold = 0f;

            float accel = s.maxSpeed / Mathf.Max(0.1f, s.accelTime);
            if (!Reversing)
            {
                if (driveSpeed < 0f) driveSpeed = Mathf.MoveTowards(driveSpeed, 0f, s.brakeDecel * Mathf.Max(gas, brake, 0.2f) * dt);
                else if (onGas)
                {
                    // Pull fades towards top speed instead of hitting a wall.
                    float pull = accel * gas * Mathf.Clamp01(1.15f - driveSpeed / s.maxSpeed);
                    driveSpeed = Mathf.MoveTowards(driveSpeed, s.maxSpeed * gas, pull * dt);
                }
                if (onBrake && driveSpeed > 0f) driveSpeed = Mathf.MoveTowards(driveSpeed, 0f, s.brakeDecel * brake * dt);
            }
            else
            {
                // Reverse gear: the brake pedal drives backwards, gas brakes.
                if (onBrake && !onGas) driveSpeed = Mathf.MoveTowards(driveSpeed, -s.reverseSpeed * brake, accel * dt);
                if (onGas) driveSpeed = Mathf.MoveTowards(driveSpeed, 0f, s.brakeDecel * gas * dt);
            }
            if (!onGas && !onBrake) driveSpeed = Mathf.MoveTowards(driveSpeed, 0f, s.coastDecel * dt);

            // Steering: build up while held, return to centre faster when released.
            float steerIn = InputEnabled ? GameInput.Steer : 0f;
            bool steeringOut = Mathf.Abs(steerIn) > Mathf.Abs(steerSmoothed) && steerIn * steerSmoothed >= 0f;
            steerSmoothed = Mathf.MoveTowards(steerSmoothed, steerIn, (steeringOut ? 3f : 4.5f) * dt);
            float v = Mathf.Abs(driveSpeed);
            SteerAngle = steerSmoothed * s.maxSteer * Mathf.Lerp(1f, 0.5f, Mathf.Clamp01(v / s.maxSpeed));

            float rate = driveSpeed / s.axleBase * Mathf.Tan(SteerAngle * Mathf.Deg2Rad) * Mathf.Rad2Deg;
            float grip = s.lateralAccel / Mathf.Max(v, 0.1f) * Mathf.Rad2Deg;
            float dYaw = Mathf.Clamp(rate, -grip, grip) * dt;

            Vector3 heading = transform.forward.WithY(0f).normalized;
            transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y + dYaw, 0f);
            Vector3 newHeading = Quaternion.Euler(0f, dYaw, 0f) * heading;
            planarVelocity = newHeading * driveSpeed;
            // Rotate about the rear axle, not the vehicle's centre.
            return (newHeading - heading) * s.rearAxle;
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
            driveSpeed = steerSmoothed = brakeHold = 0f;
            SteerAngle = 0f;
            Reversing = false;
            verticalVelocity = 0f;
        }
    }

    static class VectorExt
    {
        public static Vector3 WithY(this Vector3 v, float y) => new Vector3(v.x, y, v.z);
    }
}
