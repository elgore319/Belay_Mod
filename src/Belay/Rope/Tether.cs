using System.Collections.Generic;
using System.Globalization;
using Belay.Net;
using Belay.Players;
using Steamworks;
using UnityEngine;

namespace Belay.Rope
{
    /// <summary>
    /// The co-op rope. Players are roped in a chain in lobby join order (1-2, 2-3, ...). Each client only
    /// moves its own player: once you are farther than the rope length from a rope partner, you get
    /// pulled back toward them, and if that partner is gripping a wall your fall is caught outright.
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

        private readonly Dictionary<SteamId, LineRenderer> ropes = new Dictionary<SteamId, LineRenderer>();
        private Material ropeMaterial;

        private void Awake()
        {
            Instance = this;
        }

        public List<RemotePlayer> RopePartners()
        {
            var partners = new List<RemotePlayer>(2);
            List<SteamId> order = NetSession.Instance.MemberOrder();
            int me = order.IndexOf(SteamClient.SteamId);
            if (me < 0)
            {
                return partners;
            }
            TryAddPartner(order, me - 1, partners);
            TryAddPartner(order, me + 1, partners);
            return partners;
        }

        private static void TryAddPartner(List<SteamId> order, int index, List<RemotePlayer> partners)
        {
            if (index < 0 || index >= order.Count)
            {
                return;
            }
            if (PlayerSync.Instance.Remotes.TryGetValue(order[index], out RemotePlayer remote)
                && remote != null && !remote.Latest.Dead && Time.unscaledTime - remote.LastReceived < 2f)
            {
                partners.Add(remote);
            }
        }

        private void FixedUpdate()
        {
            CaughtThisFrame = false;
            if (!Enabled || !WorldSpace.Ready)
            {
                return;
            }
            ENT_Player player = ENT_Player.playerObject;
            if (player.dead || player.IsHanging() || player.IsFlying())
            {
                // Gripping players are the anchors; the game's own hang code owns their movement.
                return;
            }

            float length = RopeLength;
            float dt = Time.fixedDeltaTime;
            foreach (RemotePlayer partner in RopePartners())
            {
                Vector3 toPartner = partner.LocalPosition - player.transform.position;
                float distance = toPartner.magnitude;
                float overstretch = distance - length;
                if (overstretch <= 0f || distance < 0.01f)
                {
                    continue;
                }
                Vector3 dir = toPartner / distance;
                Vector3 vel = player.GetVelocity();
                float outward = -Vector3.Dot(vel, dir); // raw velocity units moving away from partner

                bool hardStop = partner.IsHanging || overstretch > HardLimitExtra;
                if (hardStop && outward > 0f)
                {
                    // Cancel the outward part of our velocity: the rope goes taut.
                    player.AddForce(dir * (outward / ForceToVel));
                    if (partner.IsHanging && outward * VelToMetersPerSecond > 4f)
                    {
                        CaughtThisFrame = true;
                        player.ResetFallingHeight();
                        player.AddIgnoreFallDamageTime(0.5f);
                    }
                }

                // Spring back toward the partner so the rope reels you in instead of just pinning you.
                float accel = SlackStiffness * overstretch * (partner.IsHanging ? 2f : 1f);
                player.AddForce(dir * (accel * dt / VelToMetersPerSecond / ForceToVel));
            }
        }

        private void LateUpdate()
        {
            var active = new HashSet<SteamId>();
            if (Enabled && WorldSpace.Ready && Plugin.ShowRope.Value)
            {
                ENT_Player player = ENT_Player.playerObject;
                Vector3 from = player.transform.position + Vector3.down * 0.4f;
                foreach (RemotePlayer partner in RopePartners())
                {
                    active.Add(partner.SteamId);
                    DrawRope(partner, from, partner.RopeAnchor);
                }
            }
            foreach (KeyValuePair<SteamId, LineRenderer> rope in ropes)
            {
                if (rope.Value != null)
                {
                    rope.Value.enabled = active.Contains(rope.Key);
                }
            }
        }

        private void DrawRope(RemotePlayer partner, Vector3 from, Vector3 to)
        {
            if (!ropes.TryGetValue(partner.SteamId, out LineRenderer line) || line == null)
            {
                if (ropeMaterial == null)
                {
                    ropeMaterial = new Material(Shader.Find("Sprites/Default"));
                }
                var go = new GameObject("Belay_Rope_" + partner.DisplayName);
                DontDestroyOnLoad(go);
                line = go.AddComponent<LineRenderer>();
                line.material = ropeMaterial;
                line.widthMultiplier = 0.04f;
                line.positionCount = 12;
                ropes[partner.SteamId] = line;
            }

            // Sag the rope when slack, straighten it as it nears full length.
            float length = RopeLength;
            float distance = Vector3.Distance(from, to);
            float slack = Mathf.Clamp01(1f - distance / length);
            float sag = slack * length * 0.25f;
            float tension = 1f - slack;
            Color color = Color.Lerp(new Color(0.85f, 0.75f, 0.55f), new Color(1f, 0.3f, 0.2f), tension * tension);
            line.startColor = color;
            line.endColor = color;
            for (int i = 0; i < line.positionCount; i++)
            {
                float t = i / (float)(line.positionCount - 1);
                Vector3 point = Vector3.Lerp(from, to, t) + Vector3.down * (sag * 4f * t * (1f - t));
                line.SetPosition(i, point);
            }
        }
    }
}
