using System.Collections.Generic;
using LeGamez.Core;
using LeGamez.Player;
using LeGamez.UI;
using LeGamez.World;
using UnityEngine;

namespace LeGamez.Vehicles
{
    /// <summary>
    /// Places parked bicycles (OSM bicycle parkings + one next to the spawn), offers "Aufsteigen"
    /// nearby and switches the player into riding mode: faster, wider turns, seated pose, no jumping.
    /// </summary>
    public class VehicleManager : MonoBehaviour
    {
        const float MountRadius = 2.6f;
        const float RideSpeed = 7.5f, RideSprint = 11f, RideTurn = 0.2f;

        public PlayerController player;
        public Hud hud;

        readonly List<Bicycle> parked = new List<Bicycle>();
        Bicycle riding;
        PlayerAvatar avatar;
        Transform avatarModel;
        ThirdPersonCamera cam;
        float walkSpeed, runSpeed, turnSmooth;

        void Start()
        {
            var area = AreaInfo.Current;
            if (area == null || player == null || !area.IsVehicleAllowed("bicycle")) return;
            avatar = player.GetComponent<PlayerAvatar>();
            avatarModel = player.transform.Find("Avatar");
            cam = player.cameraTransform != null ? player.cameraTransform.GetComponent<ThirdPersonCamera>() : null;

            // One bike right next to where the player starts (spot pre-checked by the importer).
            var p = player.transform;
            var near = area.spawnBikeSpot != Vector3.zero ? area.spawnBikeSpot : p.position + p.right * 1.6f;
            SpawnParked(GroundAt(near), p.eulerAngles.y + 15f);
            foreach (var s in area.bikeSpots) SpawnParked(GroundAt(s), Random.Range(0f, 360f));

            PositionMemory.IsRiding = () => riding != null;
            // Resume on the bike if the player left the area while riding.
            if (PositionMemory.WasRiding(area.MemoryKey))
            {
                var own = Bicycle.Create(player.transform.position, player.transform.eulerAngles.y);
                Mount(own, silent: true);
            }
        }

        void SpawnParked(Vector3 pos, float yaw)
        {
            var bike = Bicycle.Create(pos, yaw);
            bike.Dismount(pos); // parked pose
            parked.Add(bike);
        }

        static Vector3 GroundAt(Vector3 p)
        {
            const int mask = ~(1 << 2);
            return Physics.Raycast(p + Vector3.up * 30f, Vector3.down, out var hit, 60f, mask, QueryTriggerInteraction.Ignore) ? hit.point : p;
        }

        void Update()
        {
            if (player == null || hud == null) return;
            if (riding != null)
            {
                riding.Animate(player.CurrentSpeed);
                hud.SetInteract(UiKit.IconKind.Walk);
                if (GameInput.ConsumeInteract()) Dismount();
                return;
            }

            Bicycle nearest = null;
            float best = MountRadius * MountRadius;
            foreach (var b in parked)
            {
                float d = (b.transform.position - player.transform.position).sqrMagnitude;
                if (d < best) { best = d; nearest = b; }
            }
            hud.SetInteract(nearest != null ? UiKit.IconKind.Bike : (UiKit.IconKind?)null);
            if (nearest != null && GameInput.ConsumeInteract()) Mount(nearest);
        }

        void Mount(Bicycle bike, bool silent = false)
        {
            parked.Remove(bike);
            riding = bike;
            // Face the bike's direction, then sit on it.
            player.transform.rotation = Quaternion.Euler(0, bike.transform.eulerAngles.y, 0);
            player.Teleport(GroundAt(bike.transform.position) + Vector3.up * 0.1f);
            bike.Mount(player.transform);

            walkSpeed = player.walkSpeed; runSpeed = player.runSpeed; turnSmooth = player.turnSmoothTime;
            player.walkSpeed = RideSpeed;
            player.runSpeed = RideSprint;
            player.turnSmoothTime = RideTurn;
            player.CanJump = false;
            if (avatarModel != null) avatarModel.localPosition += new Vector3(0, 0.52f, -0.12f);
            avatar?.SetMode(2);
            if (cam != null) cam.distanceBonus = 1.5f;
            GameInput.RunToggled = false;              // start in the relaxed gear
            hud.SetRunIcon(UiKit.IconKind.BikeFast);   // sprint button now shifts the bike gear
            if (!silent) hud.ShowToast("Gute Fahrt!", 1.5f);
            hud.jumpButton.gameObject.SetActive(false); // no jumping on the bike
        }

        void Dismount()
        {
            var bike = riding;
            riding = null;
            var t = player.transform;
            bike.Dismount(GroundAt(t.position + t.right * 0.9f));
            parked.Add(bike);
            player.walkSpeed = walkSpeed;
            player.runSpeed = runSpeed;
            player.turnSmoothTime = turnSmooth;
            player.CanJump = true;
            if (avatarModel != null) avatarModel.localPosition -= new Vector3(0, 0.52f, -0.12f);
            avatar?.SetMode(0);
            if (cam != null) cam.distanceBonus = 0f;
            GameInput.RunToggled = false;
            hud.SetRunIcon(UiKit.IconKind.Run);
            hud.jumpButton.gameObject.SetActive(true);
            hud.SetInteract(null);
        }
    }
}
