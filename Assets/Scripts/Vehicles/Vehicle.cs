using LeGamez.UI;
using UnityEngine;

namespace LeGamez.Vehicles
{
    /// <summary>Something the player can get on/in and move with (bicycle, car).</summary>
    public abstract class Vehicle : MonoBehaviour
    {
        public bool Ridden { get; protected set; }

        // Ride tuning (applied to the PlayerController while mounted).
        public abstract float CruiseSpeed { get; }
        public abstract float SprintSpeed { get; }
        public abstract float TurnSmoothTime { get; }
        public abstract float CameraBonus { get; }
        /// <summary>Animator "Mode" while riding (1 = drive, 2 = sit).</summary>
        public abstract int AvatarMode { get; }
        /// <summary>Offset of the rider model while mounted.</summary>
        public abstract Vector3 SeatOffset { get; }
        /// <summary>Hide the rider (inside a car).</summary>
        public virtual bool HideRider => false;
        /// <summary>Steering geometry: wheelbase and distance from the pivot back to the rear axle (m).</summary>
        public abstract float AxleBase { get; }
        public abstract float RearAxle { get; }
        public virtual float MaxSteer => 32f;
        public virtual float LateralAccel => 6f;
        // Pedals: top speed at full gas, seconds to reach it, brake/coast deceleration, reverse (0 = none).
        public abstract float TopSpeed { get; }
        public virtual float AccelTime => 3f;
        public virtual float BrakeDecel => 9f;
        public virtual float CoastDecel => 1.5f;
        public virtual float ReverseSpeed => 0f;

        /// <summary>Front wheel angle set by the rider each frame (degrees, + = right), for the wheel visuals.</summary>
        [System.NonSerialized] public float SteerAngle;

        public LeGamez.Player.PlayerController.VehicleSteering Steering() => new LeGamez.Player.PlayerController.VehicleSteering
        {
            axleBase = AxleBase, rearAxle = RearAxle, maxSteer = MaxSteer, lateralAccel = LateralAccel,
            maxSpeed = TopSpeed, accelTime = AccelTime, brakeDecel = BrakeDecel, coastDecel = CoastDecel,
            reverseSpeed = ReverseSpeed,
        };

        /// <summary>CharacterController radius while riding (cars are wide).</summary>
        public virtual float ColliderRadius => 0.35f;

        public abstract UiKit.IconKind MountIcon { get; }
        public abstract UiKit.IconKind SprintIcon { get; }

        public virtual void Mount(Transform rider)
        {
            Ridden = true;
            transform.SetParent(rider, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
        }

        public virtual void Dismount(Vector3 parkPosition)
        {
            Ridden = false;
            var yaw = transform.eulerAngles.y;
            transform.SetParent(null, true);
            transform.SetPositionAndRotation(parkPosition, Quaternion.Euler(0, yaw, 0));
        }

        /// <summary>Distance from a point to the vehicle (default: to its pivot). Cars measure to their body.</summary>
        public virtual float DistanceTo(Vector3 p) => Vector3.Distance(transform.position, p);

        /// <summary>How close the player must be to get on/in.</summary>
        public virtual float MountReach => 2.6f;

        /// <summary>Per-frame visuals while ridden (wheels, lean).</summary>
        public abstract void Animate(float speed);

        /// <summary>"Ignore Raycast": the camera probe and placement checks must not hit the own vehicle.</summary>
        protected static void SetLayerRecursive(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>()) t.gameObject.layer = 2;
        }
    }
}
