using System.Collections.Generic;
using System.Globalization;
using Belay.Net;
using Belay.Players;
using Steamworks;
using UnityEngine;

namespace Belay.Rope
{
    /// <summary>
    /// The co-op rope. Players are roped in a chain in lobby join order (1-2, 2-3, ...), and each rope is a
    /// simulated <see cref="TeamRope"/> that drapes, catches on ledges and can be grabbed. Each client only
    /// moves its own player: once more rope is out than you have (measured to the teammate, or to the edge
    /// the rope runs over), you get pulled back, and if that teammate is gripping a wall your fall is
    /// caught outright and you swing round the edge instead.
    /// </summary>
    public class Tether : MonoBehaviour
    {
        // ENT_Player moves by vel * fixedDeltaTime * 175, and AddForce(v) adds v / 10 to vel.
        private const float VelToMetersPerSecond = 175f;
        private const float ForceToVel = 0.1f;

        /// <summary>Spring pull (m/s^2 per meter of overstretch) when the partner is not anchored.</summary>
        private const float SlackStiffness = 6f;
        /// <summary>Overstretch beyond which an unanchored pull becomes a hard stop too.</summary>
        private const float HardLimitExtra = 4f;

        public static Tether Instance { get; private set; }

        public bool Enabled => NetSession.Instance.InLobby && NetSession.Instance.GetLobbyData(NetSession.KeyTether) == "1";

        /// <summary>Host rule: players gripping a wall can haul up teammates roped below them.</summary>
        public bool PullAllowed => Enabled && NetSession.Instance.GetLobbyData(NetSession.KeyRopePull) != "0";

        /// <summary>Speed a pulled teammate rises at (m/s).</summary>
        private const float PullSpeed = 4f;
        private const float PullAccel = 25f;

        /// <summary>For the HUD: who you're hauling, or who's hauling you.</summary>
        public string PullStatus { get; private set; } = "";

        private float nextPullSend;
        private SteamId pulledBy;
        private float pulledUntil;

        public float RopeLength
        {
            get
            {
                string raw = NetSession.Instance.GetLobbyData(NetSession.KeyRopeLength);
                return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float len) && len > 1f
                    ? len
                    : Plugin.DefaultRopeLength.Value;
            }
        }

        /// <summary>True on frames where the rope just stopped a fall (UI flash, sounds later).</summary>
        public bool CaughtThisFrame { get; private set; }

        private readonly List<TeamRope> ropes = new List<TeamRope>();

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.RopePull, (origin, r) =>
            {
                if (!PullAllowed) return;
                foreach (RemotePlayer partner in RopePartners())
                {
                    if (partner.SteamId == origin)
                    {
                        pulledBy = origin;
                        pulledUntil = Time.unscaledTime + 0.3f;
                        return;
                    }
                }
            });
        }

        private void Update()
        {
            PullStatus = "";
            if (!PullAllowed || !WorldSpace.Ready)
            {
                return;
            }
            ENT_Player player = ENT_Player.playerObject;
            if (player == null || player.dead)
            {
                return;
            }
            if (Time.unscaledTime < pulledUntil)
            {
                PullStatus = NetSession.NameOf(pulledBy) + " is pulling you up" + (player.IsHanging() ? " (let go to be pulled)" : "");
                return;
            }
            if (!GrippingWall(player))
            {
                return;
            }
            // Gripping: you can haul up partners hanging below you on the rope.
            RemotePlayer below = null;
            foreach (RemotePlayer partner in RopePartners())
            {
                if (partner.LocalPosition.y < player.transform.position.y - 1.5f && (below == null || partner.LocalPosition.y < below.LocalPosition.y))
                {
                    below = partner;
                }
            }
            if (below == null)
            {
                return;
            }
            if (Input.GetKey(Plugin.RopePullKey.Value))
            {
                PullStatus = "Pulling " + below.DisplayName + " up";
                if (Time.unscaledTime >= nextPullSend)
                {
                    nextPullSend = Time.unscaledTime + 0.1f;
                    NetSession.Instance.Send(PacketType.RopePull, w => { });
                }
            }
            else
            {
                PullStatus = $"[{Plugin.RopePullKey.Value}] pull {below.DisplayName} up";
            }
        }

        public List<RemotePlayer> RopePartners()
        {
            var partners = new List<RemotePlayer>(2);
            List<SteamId> order = NetSession.Instance.MemberOrder();
            int me = order.IndexOf(NetSession.LocalId);
            if (me < 0)
            {
                return partners;
            }
            TryAddPartner(order, me - 1, partners);
            TryAddPartner(order, me + 1, partners);
            return partners;
        }

        private void TryAddPartner(List<SteamId> order, int index, List<RemotePlayer> partners)
        {
            if (index < 0 || index >= order.Count)
            {
                return;
            }
            RemotePlayer remote = LivePlayer(order[index]);
            if (remote != null)
            {
                partners.Add(remote);
            }
        }

        /// <summary>A teammate who is alive and still sending updates, or null.</summary>
        public RemotePlayer LivePlayer(SteamId id)
        {
            return PlayerSync.Instance != null && PlayerSync.Instance.Remotes.TryGetValue(id, out RemotePlayer remote)
                && remote != null && !remote.Latest.Dead && Time.unscaledTime - remote.LastReceived < 2f
                ? remote
                : null;
        }

        /// <summary>A gripping hand that isn't on a rope: hanging from the rope itself holds nobody up.</summary>
        public bool IsAnchor(RemotePlayer remote)
        {
            return remote.Latest.LeftHanging && !OnAnyRope(remote.HandPosition(0))
                || remote.Latest.RightHanging && !OnAnyRope(remote.HandPosition(1));
        }

        private bool OnAnyRope(Vector3 hand)
        {
            foreach (TeamRope rope in ropes)
            {
                if (rope != null && rope.Touches(hand, 0.3f))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool GrippingWall(ENT_Player player)
        {
            foreach (ENT_Player.Hand hand in player.hands)
            {
                if (hand != null && hand.IsHolding() && !(hand.currentInteract is RopeHold))
                {
                    return true;
                }
            }
            return false;
        }

        private TeamRope RopeWith(SteamId partner)
        {
            foreach (TeamRope rope in ropes)
            {
                if (rope != null && rope.Live && rope.AttachedTo(partner) && rope.AttachedTo(NetSession.LocalId))
                {
                    return rope;
                }
            }
            return null;
        }

        private static Vector3 Harness(ENT_Player player) => player.transform.position + Vector3.down * 0.4f;

        /// <summary>One rope per neighbouring pair in the lobby order; everyone simulates (and can grab) all of them.</summary>
        private void SyncRopes(bool on)
        {
            List<SteamId> order = on ? NetSession.Instance.MemberOrder() : new List<SteamId>();
            float length = RopeLength;
            for (int i = ropes.Count - 1; i >= 0; i--)
            {
                TeamRope rope = ropes[i];
                int a = rope != null ? order.IndexOf(rope.EndA) : -1;
                if (rope == null || a < 0 || a + 1 >= order.Count || order[a + 1] != rope.EndB)
                {
                    if (rope != null)
                    {
                        Destroy(rope.gameObject);
                    }
                    ropes.RemoveAt(i);
                }
            }
            for (int i = 0; i + 1 < order.Count; i++)
            {
                bool found = false;
                foreach (TeamRope rope in ropes)
                {
                    if (rope.Joins(order[i], order[i + 1]))
                    {
                        rope.SetLength(length);
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    ropes.Add(TeamRope.Create(order[i], order[i + 1], length));
                }
            }
        }

        private void FixedUpdate()
        {
            CaughtThisFrame = false;
            bool on = Enabled && WorldSpace.Ready;
            SyncRopes(on);
            if (!on)
            {
                return;
            }
            if (CL_GameManager.gMan != null && CL_GameManager.gMan.isPaused)
            {
                return;
            }
            bool visible = Plugin.ShowRope.Value;
            foreach (TeamRope rope in ropes)
            {
                rope.Step(Time.fixedDeltaTime, visible);
            }

            ENT_Player player = ENT_Player.playerObject;
            if (player.dead || player.IsHanging() || player.IsFlying())
            {
                // Gripping players are the anchors (or are hanging on a rope, which carries them);
                // the game's own hang code owns their movement.
                return;
            }
            Vector3 me = Harness(player);

            if (Time.unscaledTime < pulledUntil && PlayerSync.Instance.Remotes.TryGetValue(pulledBy, out RemotePlayer puller) && puller != null)
            {
                // Being hauled up: rise toward the teammate holding the rope, or the edge the rope runs over.
                Vector3 target = puller.LocalPosition + Vector3.down * 1.2f;
                TeamRope pullRope = RopeWith(pulledBy);
                if (pullRope != null)
                {
                    pullRope.LocalPivot(me, out Vector3 pivot, out _, out bool wrapped);
                    if (wrapped)
                    {
                        target = pivot + Vector3.up * 0.6f;
                    }
                }
                Vector3 to = target - player.transform.position;
                if (to.sqrMagnitude > 1f)
                {
                    Vector3 dir = to.normalized;
                    float along = Vector3.Dot(player.GetVelocity(), dir) * VelToMetersPerSecond;
                    float add = Mathf.Min(PullSpeed - along, PullAccel * Time.fixedDeltaTime);
                    if (add > 0f)
                    {
                        player.AddForce(dir * (add / VelToMetersPerSecond / ForceToVel));
                    }
                    player.ResetFallingHeight();
                }
            }

            float dt = Time.fixedDeltaTime;
            foreach (RemotePlayer partner in RopePartners())
            {
                // The rope pulls toward the teammate, or toward the ledge it bends over: past that point you
                // swing round it like a pendulum instead of being dragged through the rock.
                Vector3 pivot = partner.RopeAnchor;
                float available = RopeLength;
                bool wrapped = false;
                TeamRope rope = RopeWith(partner.SteamId);
                if (rope != null)
                {
                    rope.LocalPivot(me, out pivot, out available, out wrapped);
                }
                Vector3 toPivot = pivot - me;
                float distance = toPivot.magnitude;
                float overstretch = distance - available;
                if (overstretch <= 0f || distance < 0.01f)
                {
                    continue;
                }
                Vector3 dir = toPivot / distance;
                Vector3 vel = player.GetVelocity();
                float outward = -Vector3.Dot(vel, dir); // raw velocity units moving away from the pivot

                bool anchored = IsAnchor(partner);
                bool hardStop = anchored || overstretch > HardLimitExtra;
                if (hardStop && outward > 0f)
                {
                    // Cancel the outward part of our velocity: the rope goes taut and the rest becomes swing.
                    player.AddForce(dir * (outward / ForceToVel));
                    if (anchored && outward * VelToMetersPerSecond > 4f)
                    {
                        CaughtThisFrame = true;
                        player.ResetFallingHeight();
                        player.AddIgnoreFallDamageTime(0.5f);
                    }
                }

                // Spring back toward the pivot so the rope reels you in instead of just pinning you.
                float accel = SlackStiffness * overstretch * (anchored ? 2f : 1f);
                player.AddForce(dir * (accel * dt / VelToMetersPerSecond / ForceToVel));
            }
        }
    }
}
