using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Loose physics objects (boxes, crates, barrels: the game's CL_Prop) move the same for everyone.
    ///  - Each prop gets the same id on every PC (its place in the level, taken as it spawns).
    ///  - Whoever moves a prop owns it: the player holding it, or else the nearest player when it gets
    ///    knocked loose. The owner streams its position; everyone else plays it back (kinematic) and hands
    ///    it back to their own physics once it comes to rest.
    ///  - Two people grabbing the same prop: the holder beats a pusher, then the lower Steam id wins, and
    ///    the other hand lets go.
    ///  - When a prop breaks, it breaks for everyone.
    /// Items you can pick up into your inventory are left to <see cref="ItemSync"/>.
    /// </summary>
    public class PropSync : MonoBehaviour
    {
        private const float SendInterval = 1f / 15f;
        private const float ScanInterval = 0.1f;
        private const float RestBeforeRelease = 0.75f;
        private const float RemoteTimeout = 1.5f;
        private const float MaxClaimDistance = 120f;

        private const byte FlagHeld = 1;
        private const byte FlagResting = 2;

        private class Entry
        {
            public uint Id;
            public string Name;
            public CL_Prop Prop;
            public Rigidbody Body;
            public bool LocalOwner;
            public float RestTime;
            public bool LocalHeld;

            public bool Remote;
            public SteamId RemoteOwner;
            public bool RemoteHeld;
            public bool WasKinematic;
            public float LastReceived;
            public Vector3 TargetPos;
            public Quaternion TargetRot;
            public Vector3 TargetVel;
            public Vector3 TargetAngVel;
        }

        public static PropSync Instance { get; private set; }

        private readonly Dictionary<uint, Entry> byId = new Dictionary<uint, Entry>();
        private readonly Dictionary<CL_Prop, Entry> byProp = new Dictionary<CL_Prop, Entry>();
        private readonly List<Entry> scratch = new List<Entry>();
        private int runId;
        private float scanTimer;
        private float sendTimer;

        internal static bool Applying;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.PropState, HandleState);
            NetSession.Instance.On(PacketType.PropKill, HandleKill);
        }

        internal static bool Live => Instance != null && Plugin.SyncProps.Value && NetSession.Instance != null
            && NetSession.Instance.InLobby && WorldSpace.Ready;

        // ---------------------------------------------------------------- Registry

        internal void OnPropStarted(CL_Prop prop)
        {
            if (prop == null || byProp.ContainsKey(prop) || prop.GetComponent<Item_Object>() != null
                || prop.GetComponentInParent<M_Level>() == null)
            {
                return;
            }
            Rigidbody rb = prop.GetRigidbody();
            if (rb == null)
            {
                return;
            }
            string name = "prop:" + ButtonSync.ObjectId(prop);
            uint id = Hash(name);
            if (byId.TryGetValue(id, out Entry clash) && clash.Prop != null && clash.Prop != prop)
            {
                Plugin.Log.LogWarning($"Prop id clash, {name} stays local (same as {clash.Name})");
                return;
            }
            var entry = new Entry { Id = id, Name = name, Prop = prop, Body = rb };
            byId[id] = entry;
            byProp[prop] = entry;
            IgnoreTeammateBodies(prop);
        }

        /// <summary>
        /// Teammate bodies don't shove props on our PC: the teammate isn't pushing it on theirs, so it would
        /// only drift apart. Props move through holding, hits and knocks, which do sync.
        /// </summary>
        private static void IgnoreTeammateBodies(CL_Prop prop)
        {
            List<Collider> colliders = prop.GetColliders();
            if (colliders == null)
            {
                return;
            }
            foreach (RemoteBody body in RemoteBody.All)
            {
                IgnorePair(body.Collider, colliders);
            }
        }

        internal void OnBodyCreated(RemoteBody body)
        {
            foreach (Entry entry in byId.Values)
            {
                if (entry.Prop != null)
                {
                    IgnorePair(body.Collider, entry.Prop.GetColliders());
                }
            }
        }

        /// <summary>
        /// Local test bots only exist on this PC, so nothing else would ever push a prop for them: their
        /// bodies keep colliding with props here.
        /// </summary>
        private static bool IsLocalBotBody(Collider body)
        {
            RemoteBody rb = RemoteBody.Of(body);
            return rb != null && rb.Owner != null && Net.NetSession.Instance != null && Net.NetSession.Instance.IsLocalBot(rb.Owner.SteamId);
        }

        private static void IgnorePair(Collider body, List<Collider> colliders)
        {
            if (body == null || colliders == null || IsLocalBotBody(body))
            {
                return;
            }
            foreach (Collider c in colliders)
            {
                if (c != null)
                {
                    Physics.IgnoreCollision(body, c, true);
                }
            }
        }

        private static uint Hash(string s)
        {
            uint h = 2166136261;
            foreach (char ch in s)
            {
                h ^= ch;
                h *= 16777619;
            }
            return h == 0 ? 1u : h;
        }

        // ---------------------------------------------------------------- Ownership and sending

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (net == null || !net.InLobby)
            {
                return;
            }
            if (runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                byId.Clear();
                byProp.Clear();
            }
            if (!Live)
            {
                return;
            }

            scanTimer -= Time.unscaledDeltaTime;
            if (scanTimer <= 0f)
            {
                scanTimer = ScanInterval;
                Scan();
            }

            sendTimer -= Time.unscaledDeltaTime;
            if (sendTimer <= 0f)
            {
                sendTimer = SendInterval;
                foreach (Entry entry in byId.Values)
                {
                    if (entry.LocalOwner && Alive(entry))
                    {
                        SendState(entry, false);
                    }
                }
            }
        }

        private static bool Alive(Entry e)
        {
            return e.Prop != null && e.Body != null && !e.Prop.dead && e.Prop.gameObject.activeInHierarchy;
        }

        private void Scan()
        {
            ENT_Player player = ENT_Player.playerObject;
            Vector3 me = player.transform.position;
            scratch.Clear();
            scratch.AddRange(byId.Values);
            foreach (Entry e in scratch)
            {
                if (!Alive(e))
                {
                    if (e.Prop == null)
                    {
                        byId.Remove(e.Id);
                    }
                    continue;
                }
                e.LocalHeld = HeldLocally(player, e.Prop) >= 0;

                if (e.Remote)
                {
                    if (Time.unscaledTime - e.LastReceived > RemoteTimeout)
                    {
                        // The owner went quiet (left, or the prop left their loaded area): our physics takes over.
                        ReleaseRemote(e, true);
                    }
                    continue;
                }

                bool moving = !e.Body.isKinematic && !e.Body.IsSleeping()
                    && (e.Body.linearVelocity.sqrMagnitude > 0.04f || e.Body.angularVelocity.sqrMagnitude > 0.25f);
                if (e.LocalHeld || moving)
                {
                    e.RestTime = 0f;
                }
                else
                {
                    e.RestTime += ScanInterval;
                }

                if (e.LocalOwner)
                {
                    if (!e.LocalHeld && e.RestTime >= RestBeforeRelease)
                    {
                        // Settled: send where it came to rest and let everyone's physics have it back.
                        SendState(e, true);
                        e.LocalOwner = false;
                    }
                    continue;
                }

                if (e.LocalHeld || moving && (e.Prop.transform.position - me).sqrMagnitude < MaxClaimDistance * MaxClaimDistance
                    && NearestToMe(e.Prop.transform.position))
                {
                    e.LocalOwner = true;
                    e.RestTime = 0f;
                    SendState(e, false);
                }
            }
        }

        private static bool NearestToMe(Vector3 position)
        {
            float mine = (ENT_Player.playerObject.transform.position - position).sqrMagnitude;
            if (PlayerSync.Instance == null)
            {
                return true;
            }
            foreach (RemotePlayer remote in PlayerSync.Instance.Remotes.Values)
            {
                if (remote == null || remote.Latest.Dead || NetSession.Instance.IsLocalBot(remote.SteamId))
                {
                    continue;
                }
                if ((remote.LocalPosition - position).sqrMagnitude < mine)
                {
                    return false;
                }
            }
            return true;
        }

        private static int HeldLocally(ENT_Player player, CL_Prop prop)
        {
            for (int i = 0; i < player.hands.Length; i++)
            {
                if (player.hands[i].grabTarget == prop)
                {
                    return i;
                }
            }
            return -1;
        }

        private void SendState(Entry e, bool resting)
        {
            Transform t = e.Prop.transform;
            Vector3 pos = WorldSpace.ToShared(t.position);
            Quaternion rot = t.rotation;
            Vector3 vel = resting || e.Body.isKinematic ? Vector3.zero : e.Body.linearVelocity;
            Vector3 ang = resting || e.Body.isKinematic ? Vector3.zero : e.Body.angularVelocity;
            byte flags = 0;
            if (e.LocalHeld) flags |= FlagHeld;
            if (resting) flags |= FlagResting;
            // Settling is the update that matters most, so it goes reliably.
            NetSession.Instance.Send(PacketType.PropState, w =>
            {
                w.Write(e.Id);
                w.Write(flags);
                w.WriteVector(pos);
                w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
                w.WriteVector(vel);
                w.WriteVector(ang);
            }, reliable: resting);
        }

        // ---------------------------------------------------------------- Receiving

        private void HandleState(SteamId origin, System.IO.BinaryReader r)
        {
            uint id = r.ReadUInt32();
            byte flags = r.ReadByte();
            Vector3 pos = r.ReadVector();
            var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            Vector3 vel = r.ReadVector();
            Vector3 ang = r.ReadVector();
            if (!Live || origin == NetSession.LocalId || !byId.TryGetValue(id, out Entry e) || !Alive(e))
            {
                return;
            }
            bool theirsHeld = (flags & FlagHeld) != 0;
            bool resting = (flags & FlagResting) != 0;
            ENT_Player player = ENT_Player.playerObject;
            int myHand = HeldLocally(player, e.Prop);

            if (e.LocalOwner && !resting)
            {
                bool mineHeld = myHand >= 0;
                bool theyWin = theirsHeld && !mineHeld
                    || theirsHeld == mineHeld && origin.Value < NetSession.LocalId.Value;
                if (!theyWin)
                {
                    return;
                }
                e.LocalOwner = false;
            }
            else if (e.LocalOwner && resting)
            {
                // Their old update after we took over; ours is newer.
                return;
            }

            if (myHand >= 0 && !resting)
            {
                Plugin.Log.LogInfo($"{NetSession.NameOf(origin)} has hold of {e.Prop.name}; letting go");
                player.StopInteraction(myHand);
            }

            if (!e.Remote)
            {
                if (e.Prop.stuck)
                {
                    e.Prop.Unstick();
                }
                e.WasKinematic = e.Body.isKinematic;
                e.Remote = true;
                e.Body.isKinematic = true;
            }
            e.RemoteOwner = origin;
            e.RemoteHeld = theirsHeld;
            e.LastReceived = Time.unscaledTime;
            e.TargetPos = WorldSpace.ToLocal(pos);
            e.TargetRot = rot;
            e.TargetVel = vel;
            e.TargetAngVel = ang;
            e.RestTime = 0f;

            if (resting)
            {
                e.Prop.transform.SetPositionAndRotation(e.TargetPos, e.TargetRot);
                ReleaseRemote(e, false);
            }
        }

        private static void ReleaseRemote(Entry e, bool keepMotion)
        {
            if (!e.Remote)
            {
                return;
            }
            e.Remote = false;
            e.RemoteOwner = default;
            if (e.Body == null)
            {
                return;
            }
            e.Body.isKinematic = e.WasKinematic;
            if (!e.Body.isKinematic)
            {
                e.Body.linearVelocity = keepMotion ? e.TargetVel : Vector3.zero;
                e.Body.angularVelocity = keepMotion ? e.TargetAngVel : Vector3.zero;
                if (!keepMotion)
                {
                    e.Body.Sleep();
                }
            }
        }

        private void FixedUpdate()
        {
            if (!Live)
            {
                return;
            }
            float dt = Time.fixedDeltaTime;
            float t = 1f - Mathf.Exp(-18f * dt);
            foreach (Entry e in byId.Values)
            {
                if (!e.Remote || !Alive(e))
                {
                    continue;
                }
                // Carry on along the last known velocity between updates, then ease toward it.
                e.TargetPos += e.TargetVel * dt;
                if (e.TargetAngVel.sqrMagnitude > 1e-4f)
                {
                    e.TargetRot = Quaternion.AngleAxis(e.TargetAngVel.magnitude * Mathf.Rad2Deg * dt, e.TargetAngVel.normalized) * e.TargetRot;
                }
                Vector3 current = e.Body.position;
                if ((e.TargetPos - current).sqrMagnitude > 9f)
                {
                    e.Body.position = e.TargetPos;
                    e.Body.rotation = e.TargetRot;
                    continue;
                }
                e.Body.MovePosition(Vector3.Lerp(current, e.TargetPos, t));
                e.Body.MoveRotation(Quaternion.Slerp(e.Body.rotation, e.TargetRot, t));
            }
        }

        // ---------------------------------------------------------------- Breaking

        internal void OnLocalKill(CL_Prop prop)
        {
            if (Applying || !Live || !byProp.TryGetValue(prop, out Entry e))
            {
                return;
            }
            Plugin.Log.LogInfo("Broke " + e.Name);
            NetSession.Instance.Send(PacketType.PropKill, w => w.Write(e.Id));
        }

        private void HandleKill(SteamId origin, System.IO.BinaryReader r)
        {
            uint id = r.ReadUInt32();
            if (!Live || !byId.TryGetValue(id, out Entry e) || e.Prop == null || e.Prop.dead)
            {
                return;
            }
            Applying = true;
            try
            {
                var info = Damageable.DamageInfo.CreateDamageInfo(999f, "belay-sync");
                info.tags.Add("SkipPropDelayedKill");
                e.Prop.Kill("belay-sync", info);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Couldn't break {e.Name} here: {ex.Message}");
                e.Prop.gameObject.SetActive(false);
            }
            finally
            {
                Applying = false;
            }
        }
    }

    [HarmonyPatch]
    internal static class PropSyncPatches
    {
        [HarmonyPatch(typeof(CL_Prop), nameof(CL_Prop.Start))]
        [HarmonyPostfix]
        private static void PropStart(CL_Prop __instance)
        {
            if (PropSync.Live)
            {
                try
                {
                    PropSync.Instance.OnPropStarted(__instance);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Prop sync skipped {__instance.name}: {e.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(CL_Prop), nameof(CL_Prop.Kill))]
        [HarmonyPrefix]
        private static void PropKill(CL_Prop __instance)
        {
            if (PropSync.Live && !__instance.dead)
            {
                try
                {
                    PropSync.Instance.OnLocalKill(__instance);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Prop break not shared ({__instance.name}): {e.Message}");
                }
            }
        }
    }
}
