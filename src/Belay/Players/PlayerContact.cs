using System.Collections.Generic;
using Belay.Net;
using Steamworks;
using UnityEngine;

namespace Belay.Players
{
    /// <summary>
    /// Teammates are physical:
    ///  - Their bodies are solid (<see cref="RemoteBody"/>). You bump into them and can stand on them.
    ///  - Grip a teammate like a handhold. If you're only hanging off them, you ride along as they climb.
    ///    If you're standing, or gripping the wall with your other hand, you lift them instead: they're
    ///    carried to the spot in front of your face, and letting go while swinging your view throws them.
    ///    The carried player jumps to wriggle free.
    ///  - Hit a teammate with a hammer: the hit connects and knocks them back, but never hurts.
    /// Each PC still only moves its own player; the grip and the carry point ride along in PlayerState.
    /// </summary>
    public class PlayerContact : MonoBehaviour
    {
        // ENT_Player moves by vel * fixedDeltaTime * 175, and AddForce(v) adds v / 10 to vel.
        private const float VelToMetersPerSecond = 175f;
        private const float ForceToVel = 0.1f;

        private const float CarryDistance = 1.05f;
        private const float CarryStiffness = 9f;
        private const float CarryMaxSpeed = 14f;
        private const float EscapeTime = 1.2f;

        public static PlayerContact Instance { get; private set; }

        private static int bodyLayer = -1;

        /// <summary>
        /// Layer for teammate bodies: one the hands' grab casts see and the player capsule collides with.
        /// The game's masks come from its scenes, so this is worked out from the live player.
        /// </summary>
        public static int BodyLayer
        {
            get
            {
                if (bodyLayer >= 0)
                {
                    return bodyLayer;
                }
                ENT_Player player = ENT_Player.playerObject;
                if (player == null)
                {
                    return 0;
                }
                int playerLayer = player.gameObject.layer;
                int mask = player.interactMask.value;
                bodyLayer = 0;
                if ((mask & 1) == 0 || Physics.GetIgnoreLayerCollision(0, playerLayer))
                {
                    for (int layer = 0; layer < 32; layer++)
                    {
                        if ((mask & (1 << layer)) != 0 && layer != playerLayer && !Physics.GetIgnoreLayerCollision(layer, playerLayer))
                        {
                            bodyLayer = layer;
                            break;
                        }
                    }
                }
                Plugin.Log.LogInfo($"Teammate bodies use layer {bodyLayer} ({LayerMask.LayerToName(bodyLayer)}); player layer {playerLayer}");
                return bodyLayer;
            }
        }

        private readonly Dictionary<SteamId, float> escapedUntil = new Dictionary<SteamId, float>();
        private SteamId carriedBy;

        /// <summary>Who is carrying the local player right now (default when nobody).</summary>
        public SteamId CarriedBy => carriedBy;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.Knockback, HandleKnockback);
        }

        private static bool Live => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready && Plugin.SolidTeammates.Value;

        // ---------------------------------------------------------------- Outgoing state

        /// <summary>Adds the grip/carry fields to the state we send.</summary>
        public void FillLocal(ref PlayerState state)
        {
            ENT_Player player = ENT_Player.playerObject;
            if (player == null || !Live)
            {
                return;
            }
            RemoteBody held = null;
            Vector3 gripPoint = Vector3.zero;
            bool otherHandOnWall = false;
            foreach (ENT_Player.Hand hand in player.hands)
            {
                if (hand.interactState != ENT_Player.InteractType.hanging || hand.holdTarget == null)
                {
                    continue;
                }
                RemoteBody body = hand.holdTarget.GetComponent<RemoteBody>();
                if (body != null && body.Owner != null)
                {
                    if (held == null)
                    {
                        held = body;
                        gripPoint = hand.GetHoldWorldPosition();
                    }
                }
                else
                {
                    otherHandOnWall = true;
                }
            }
            if (held == null)
            {
                return;
            }
            state.HeldTarget = held.Owner.SteamId.Value;
            state.HeldOffset = gripPoint - held.Owner.transform.position;
            Transform view = player.cam != null ? player.cam.transform : player.transform;
            state.CarryPoint = WorldSpace.ToShared(view.position + view.forward * CarryDistance);
            state.Anchored = otherHandOnWall || player.IsGrounded(true);
        }

        // ---------------------------------------------------------------- Knockback

        private void Update()
        {
            if (carriedBy.Value != 0 && Live && InputManager.GetButton("Jump").Down)
            {
                // Wriggle free.
                escapedUntil[carriedBy] = Time.time + EscapeTime;
                AddVelocity(ENT_Player.playerObject, Vector3.up * 3f);
                Plugin.Log.LogInfo("Wriggled free of " + NetSession.NameOf(carriedBy));
                carriedBy = default;
            }
        }

        /// <summary>Knock a teammate back. They move on their own PC; nobody takes damage.</summary>
        public void SendKnockback(RemotePlayer target, Vector3 direction, float metersPerSecond, string kind)
        {
            if (target == null || !NetSession.Instance.InLobby)
            {
                return;
            }
            Plugin.Log.LogInfo($"Knocked back {target.DisplayName} ({kind})");
            if (NetSession.Instance.IsLocalBot(target.SteamId))
            {
                Bot.TestBot.Instance?.Knock(target.SteamId, direction * metersPerSecond);
                return;
            }
            NetSession.Instance.Send(PacketType.Knockback, w =>
            {
                w.Write(target.SteamId.Value);
                w.WriteVector(direction);
                w.Write(metersPerSecond);
                w.Write(kind ?? "");
            });
        }

        private void HandleKnockback(SteamId origin, System.IO.BinaryReader r)
        {
            ulong target = r.ReadUInt64();
            Vector3 dir = r.ReadVector();
            float speed = Mathf.Clamp(r.ReadSingle(), 0f, 20f);
            string kind = r.ReadString();
            if (target != NetSession.LocalId.Value || !Live)
            {
                return;
            }
            ENT_Player player = ENT_Player.playerObject;
            if (player.dead)
            {
                return;
            }
            Plugin.Log.LogInfo($"{NetSession.NameOf(origin)} hit you ({kind})");
            // Gripping the wall soaks most of it up; you're not knocked off.
            float scale = player.IsHanging() ? 0.35f : 1f;
            AddVelocity(player, dir.normalized * speed * scale);
            player.AddIgnoreFallDamageTime(1f);
            CL_CameraControl.Shake(kind == "hammer" ? 0.06f : 0.035f);
        }

        private static void AddVelocity(ENT_Player player, Vector3 metersPerSecond)
        {
            player.AddForce(metersPerSecond / VelToMetersPerSecond / ForceToVel);
        }

        // ---------------------------------------------------------------- Being carried

        private void FixedUpdate()
        {
            SteamId was = carriedBy;
            carriedBy = default;
            if (!Live || PlayerSync.Instance == null)
            {
                return;
            }
            ENT_Player player = ENT_Player.playerObject;
            if (player.dead || player.IsMoveLocked())
            {
                return;
            }

            RemotePlayer carrier = FindCarrier(player);
            if (carrier == null)
            {
                return;
            }
            carriedBy = carrier.SteamId;
            if (was != carriedBy)
            {
                Plugin.Log.LogInfo(carrier.DisplayName + " picked you up");
            }

            // Pull the gripped spot on our body to the carrier's carry point.
            Vector3 grip = player.transform.position + carrier.Latest.HeldOffset;
            Vector3 error = WorldSpace.ToLocal(carrier.Latest.CarryPoint) - grip;
            Vector3 wanted = Vector3.ClampMagnitude(error * CarryStiffness, CarryMaxSpeed);
            Vector3 current = player.GetVelocity() * VelToMetersPerSecond;
            AddVelocity(player, wanted - current);
            player.ResetFallingHeight();
            player.AddIgnoreFallDamageTime(0.5f);
        }

        private RemotePlayer FindCarrier(ENT_Player player)
        {
            if (player.IsHanging())
            {
                // Holding the wall yourself: your own grip wins any tug of war.
                return null;
            }
            ulong me = NetSession.LocalId.Value;
            RemotePlayer best = null;
            foreach (RemotePlayer remote in PlayerSync.Instance.Remotes.Values)
            {
                if (remote == null || remote.Latest.Dead || remote.Latest.HeldTarget != me || !remote.Latest.Anchored
                    || Time.unscaledTime - remote.LastReceived > 0.5f || NetSession.Instance.IsLocalBot(remote.SteamId))
                {
                    continue;
                }
                if (escapedUntil.TryGetValue(remote.SteamId, out float until) && Time.time < until)
                {
                    continue;
                }
                if (IsHolding(player, remote))
                {
                    // We grip each other: nobody gets lifted.
                    continue;
                }
                if (best == null || remote.SteamId.Value < best.SteamId.Value)
                {
                    best = remote;
                }
            }
            return best;
        }

        private static bool IsHolding(ENT_Player player, RemotePlayer remote)
        {
            foreach (ENT_Player.Hand hand in player.hands)
            {
                if (hand.interactState == ENT_Player.InteractType.hanging && hand.holdTarget != null)
                {
                    RemoteBody body = hand.holdTarget.GetComponent<RemoteBody>();
                    if (body != null && body.Owner == remote)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
