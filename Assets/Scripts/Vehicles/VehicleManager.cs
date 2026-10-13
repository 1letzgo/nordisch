using System.Collections.Generic;
using LeGamez.Core;
using LeGamez.Player;
using LeGamez.UI;
using LeGamez.World;
using UnityEngine;

namespace LeGamez.Vehicles
{
    /// <summary>
    /// Places parked vehicles (bicycles at OSM bike parkings + next to the spawn, cars at the area's car
    /// spots), offers the mount button nearby and switches the player into the vehicle's ride mode.
    /// </summary>
    public class VehicleManager : MonoBehaviour
    {
        public PlayerController player;
        public Hud hud;

        readonly List<Vehicle> parked = new List<Vehicle>();
        Vehicle riding;
        PlayerAvatar avatar;
        Transform avatarModel;
        ThirdPersonCamera cam;
        CharacterController controller;
        float walkSpeed, runSpeed, turnSmooth, ccRadius, ccHeight;
        Vector3 ccCenter;

        void Start()
        {
            var area = AreaInfo.Current;
            if (area == null || player == null) return;
            avatar = player.GetComponent<PlayerAvatar>();
            avatarModel = player.transform.Find("Avatar");
            controller = player.GetComponent<CharacterController>();
            cam = player.cameraTransform != null ? player.cameraTransform.GetComponent<ThirdPersonCamera>() : null;

            if (area.IsVehicleAllowed("bicycle"))
            {
                // One bike right next to the spawn (spot pre-checked by the importer) + bike parkings.
                var p = player.transform;
                var near = area.spawnBikeSpot != Vector3.zero ? area.spawnBikeSpot : p.position + p.right * 1.6f;
                Park(Bicycle.Create(GroundAt(near), p.eulerAngles.y + 15f));
                foreach (var s in area.bikeSpots) Park(Bicycle.Create(GroundAt(s), Random.Range(0f, 360f)));
            }
            if (area.IsVehicleAllowed("car"))
            {
                int seed = 0;
                foreach (var c in area.carSpots) Park(Car.Create(GroundAt(c), c.w, seed++));
            }

            PositionMemory.RidingKind = () => riding is Car ? 2 : riding is Bicycle ? 1 : 0;
            // Resume in/on the vehicle the player left the area with.
            int kind = PositionMemory.LastRidingKind(area.MemoryKey);
            if (kind == 1 && area.IsVehicleAllowed("bicycle")) Mount(Bicycle.Create(player.transform.position, player.transform.eulerAngles.y), true);
            else if (kind == 2 && area.IsVehicleAllowed("car")) Mount(Car.Create(player.transform.position, player.transform.eulerAngles.y, 1), true);
        }

        void Park(Vehicle v)
        {
            v.Dismount(v.transform.position); // parked pose
            parked.Add(v);
        }

        static Vector3 GroundAt(Vector3 p)
        {
            const int mask = ~((1 << 2) | (1 << 4));
            return Physics.Raycast(p + Vector3.up * 30f, Vector3.down, out var hit, 60f, mask, QueryTriggerInteraction.Ignore) ? hit.point : p;
        }

        void Update()
        {
            if (player == null || hud == null) return;
            if (riding != null)
            {
                riding.SteerAngle = player.SteerAngle;
                riding.Animate(player.SignedSpeed);
                hud.SetGear(player.Reversing);
                hud.SetInteract(UiKit.IconKind.Walk);
                if (GameInput.ConsumeInteract()) Dismount();
                return;
            }

            Vehicle nearest = null;
            float best = float.MaxValue;
            foreach (var v in parked)
            {
                float d = v.DistanceTo(player.transform.position);
                if (d < v.MountReach && d < best) { best = d; nearest = v; }
            }
            hud.SetInteract(nearest != null ? nearest.MountIcon : (UiKit.IconKind?)null);
            if (nearest != null && GameInput.ConsumeInteract()) Mount(nearest);
        }

        void Mount(Vehicle v, bool silent = false)
        {
            parked.Remove(v);
            riding = v;
            // Take the vehicle's heading and position, then sit on/in it.
            player.transform.rotation = Quaternion.Euler(0, v.transform.eulerAngles.y, 0);
            player.Teleport(GroundAt(v.transform.position) + Vector3.up * 0.1f);
            v.Mount(player.transform);

            walkSpeed = player.walkSpeed; runSpeed = player.runSpeed; turnSmooth = player.turnSmoothTime;
            player.walkSpeed = v.CruiseSpeed;
            player.runSpeed = v.SprintSpeed;
            player.turnSmoothTime = v.TurnSmoothTime;
            player.CanJump = false;
            player.Steering = v.Steering();

            if (controller != null)
            {
                ccRadius = controller.radius; ccHeight = controller.height; ccCenter = controller.center;
                controller.radius = v.ColliderRadius;
                controller.height = Mathf.Max(ccHeight, v.ColliderRadius * 2f + 0.05f);
                controller.center = new Vector3(0, controller.height * 0.5f, 0);
            }
            if (avatarModel != null)
            {
                avatarModel.localPosition += v.SeatOffset;
                if (v.HideRider) foreach (var r in avatarModel.GetComponentsInChildren<Renderer>()) r.enabled = false;
            }
            avatar?.SetMode(v.AvatarMode);
            if (cam != null) cam.distanceBonus = v.CameraBonus;
            GameInput.RunToggled = false;          // start in the relaxed gear
            hud.SetDriveMode(true, v.ReverseSpeed > 0f);
            if (!silent) hud.ShowToast("Gute Fahrt!", 1.5f);
        }

        void Dismount()
        {
            var v = riding;
            riding = null;
            var t = player.transform;

            if (v is Car)
            {
                // The car stays where it is; the driver steps out on the left (driver's door).
                var carPos = t.position;
                var left = -t.right;
                v.Dismount(GroundAt(carPos));
                RestoreRider(v);
                Placement.TryFindFreeGround(carPos + left * 1.9f, t, out var door, 6f);
                player.Teleport(door + Vector3.up * 0.1f);
            }
            else
            {
                v.Dismount(GroundAt(t.position + t.right * 0.9f));
                RestoreRider(v);
            }
            parked.Add(v);
            hud.SetInteract(null);
        }

        void RestoreRider(Vehicle v)
        {
            player.walkSpeed = walkSpeed;
            player.runSpeed = runSpeed;
            player.turnSmoothTime = turnSmooth;
            player.CanJump = true;
            player.Steering = null;
            if (controller != null)
            {
                controller.radius = ccRadius;
                controller.height = ccHeight;
                controller.center = ccCenter;
            }
            if (avatarModel != null)
            {
                avatarModel.localPosition -= v.SeatOffset;
                foreach (var r in avatarModel.GetComponentsInChildren<Renderer>()) r.enabled = true;
            }
            avatar?.SetMode(0);
            if (cam != null) cam.distanceBonus = 0f;
            GameInput.RunToggled = false;
            hud.SetDriveMode(false, false);
            hud.SetRunIcon(UiKit.IconKind.Run);
        }
    }
}
