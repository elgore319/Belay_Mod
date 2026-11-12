using System.Collections.Generic;
using System.IO;
using Belay.Net;
using Belay.Players;
using DarkMachine.AI;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// The host runs the enemies:
    ///  - The host streams where each moving enemy near the team is, about ten times a second. On the
    ///    other PCs the same enemy is held at the host's position, so everyone sees it in the same place.
    ///    Each PC's copy still does its own attacking, so an enemy the host moves next to you hurts you
    ///    on your own PC.
    ///  - On the host, an enemy looking for someone to chase picks the nearest player it can see,
    ///    teammates included, not only the host.
    /// </summary>
    public class EnemyMotion : MonoBehaviour
    {
        public static EnemyMotion Instance { get; private set; }

        private const float SendInterval = 0.1f;
        private const float FullRefresh = 1f;
        private const float Range = 100f;
        private const int MaxPerPacket = 40;
        private const int PerPacket = 10;
        private const float FollowTimeout = 1.5f;

        // Host
        private readonly Dictionary<AIGameEntity, int> netIds = new Dictionary<AIGameEntity, int>();
        private readonly Dictionary<AIGameEntity, (Vector3 pos, Quaternion rot, float time)> lastSent = new Dictionary<AIGameEntity, (Vector3, Quaternion, float)>();
        private int nextNetId = 1;
        private float nextSend;

        // Clients
        private class Follow
        {
            public AIGameEntity Enemy;
            public Rigidbody Body;
            public bool MadeKinematic;
            public Vector3 TargetShared;
            public Quaternion TargetRot;
            public float LastHeard;
            public float NextFind;
        }

        private readonly Dictionary<int, Follow> follows = new Dictionary<int, Follow>();
        private int runId;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.EnemyStates, HandleStates);
        }

        private static bool Live => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready && EnemySync.Instance != null;

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (net != null && net.InLobby && runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                ReleaseAll();
                netIds.Clear();
                lastSent.Clear();
            }
            if (!Live)
            {
                ReleaseAll();
                return;
            }
            if (net.IsHost)
            {
                ReleaseAll();
                if (Time.unscaledTime >= nextSend)
                {
                    nextSend = Time.unscaledTime + SendInterval;
                    SendStates();
                }
            }
        }

        // ---------------------------------------------------------------- Host

        private static List<Vector3> PlayerSpots()
        {
            var spots = new List<Vector3>();
            if (ENT_Player.playerObject != null)
            {
                spots.Add(ENT_Player.playerObject.transform.position);
            }
            foreach (RemotePlayer remote in PlayerSync.Instance.Remotes.Values)
            {
                if (remote != null)
                {
                    spots.Add(remote.LocalPosition);
                }
            }
            return spots;
        }

        private void SendStates()
        {
            List<Vector3> players = PlayerSpots();
            var batch = new List<(AIGameEntity e, float d)>();
            foreach (AIGameEntity e in EnemySync.Instance.Known())
            {
                if (!e.gameObject.activeInHierarchy)
                {
                    continue;
                }
                float nearest = float.MaxValue;
                foreach (Vector3 p in players)
                {
                    nearest = Mathf.Min(nearest, (p - e.transform.position).sqrMagnitude);
                }
                if (nearest > Range * Range)
                {
                    continue;
                }
                Vector3 pos = e.transform.position;
                Quaternion rot = e.transform.rotation;
                if (lastSent.TryGetValue(e, out var last) && Time.unscaledTime - last.time < FullRefresh
                    && (last.pos - pos).sqrMagnitude < 0.0004f && Quaternion.Angle(last.rot, rot) < 1f)
                {
                    continue; // hasn't moved: nothing to say (barnacles, turrets, sleepers)
                }
                batch.Add((e, nearest));
            }
            if (batch.Count == 0)
            {
                return;
            }
            batch.Sort((a, b) => a.d.CompareTo(b.d));
            int total = Mathf.Min(batch.Count, MaxPerPacket);
            // Small packets: unreliable Steam packets must stay under about 1200 bytes.
            for (int start = 0; start < total; start += PerPacket)
            {
                SendChunk(batch, start, Mathf.Min(PerPacket, total - start));
            }
        }

        private void SendChunk(List<(AIGameEntity e, float d)> batch, int start, int count)
        {
            NetSession.Instance.Send(PacketType.EnemyStates, w =>
            {
                w.Write((byte)count);
                for (int i = start; i < start + count; i++)
                {
                    AIGameEntity e = batch[i].e;
                    if (!netIds.TryGetValue(e, out int id))
                    {
                        id = nextNetId++;
                        netIds[e] = id;
                    }
                    Vector3 pos = e.transform.position;
                    Quaternion rot = e.transform.rotation;
                    lastSent[e] = (pos, rot, Time.unscaledTime);
                    w.Write(id);
                    w.Write(EnemySync.KindOf(e));
                    w.WriteVector(EnemySync.Instance.SpawnOf(e));
                    w.WriteVector(WorldSpace.ToShared(pos));
                    w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
                }
            }, reliable: false);
        }

        /// <summary>
        /// Host only: after an enemy looks around for someone to chase, swap its pick for a teammate if
        /// one it can see is closer than the host.
        /// </summary>
        internal static void ConsiderTeammates(AITargetComponent component, Vector3 pos, float radius, AISightComponent sight, Transform self)
        {
            if (!Live || !NetSession.Instance.IsHost || component == null)
            {
                return;
            }
            ENT_Player player = ENT_Player.playerObject;
            Transform current = component.target.targetTransform;
            bool currentIsHost = current != null && player != null && (current == player.transform || current.IsChildOf(player.transform));
            if (current != null && !currentIsHost)
            {
                return; // chasing something else entirely (an item, another creature): leave it
            }
            float best = current != null ? Vector3.Distance(pos, current.position) : radius;
            RemoteBody pick = null;
            foreach (RemoteBody body in RemoteBody.All)
            {
                if (body == null || body.Owner == null || body.Owner.Latest.Dead || !body.Collider.enabled)
                {
                    continue;
                }
                float d = Vector3.Distance(pos, body.transform.position);
                if (d >= best)
                {
                    continue;
                }
                if (sight != null && !sight.CanSeeTarget(body.transform, checkMemory: true))
                {
                    continue;
                }
                best = d;
                pick = body;
            }
            if (pick == null)
            {
                return;
            }
            bool had = component.HasTarget();
            component.target.targetTransform = pick.transform;
            component.target.targetTagger = pick.GetComponent<ObjectTagger>();
            component.target.targetEntity = null;
            if (!had)
            {
                component._OnTargetFound?.Invoke();
            }
        }

        // ---------------------------------------------------------------- Clients

        private void HandleStates(SteamId origin, BinaryReader r)
        {
            if (origin != NetSession.Instance.HostId || NetSession.Instance.IsHost || !Live)
            {
                return;
            }
            int count = r.ReadByte();
            for (int i = 0; i < count; i++)
            {
                int id = r.ReadInt32();
                string kind = r.ReadString();
                Vector3 spawn = r.ReadVector();
                Vector3 now = r.ReadVector();
                var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                if (!follows.TryGetValue(id, out Follow f))
                {
                    f = new Follow();
                    follows[id] = f;
                }
                if (f.Enemy == null)
                {
                    if (Time.unscaledTime < f.NextFind)
                    {
                        continue;
                    }
                    f.NextFind = Time.unscaledTime + 2f;
                    AIGameEntity found = EnemySync.Instance.Find(kind, spawn, now);
                    if (found == null || IsFollowed(found))
                    {
                        continue;
                    }
                    f.Enemy = found;
                    f.Body = found.GetComponent<Rigidbody>();
                    Plugin.Log.LogInfo($"Following the host's {kind}");
                }
                f.TargetShared = now;
                f.TargetRot = rot;
                f.LastHeard = Time.unscaledTime;
            }
        }

        private bool IsFollowed(AIGameEntity e)
        {
            foreach (Follow f in follows.Values)
            {
                if (f.Enemy == e)
                {
                    return true;
                }
            }
            return false;
        }

        private void LateUpdate()
        {
            if (follows.Count == 0 || !Live || NetSession.Instance.IsHost)
            {
                return;
            }
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            List<int> done = null;
            foreach (var pair in follows)
            {
                Follow f = pair.Value;
                if (f.Enemy == null)
                {
                    if (Time.unscaledTime - f.LastHeard > 10f)
                    {
                        (done ?? (done = new List<int>())).Add(pair.Key);
                    }
                    continue;
                }
                if (f.Enemy.dead || Time.unscaledTime - f.LastHeard > FollowTimeout)
                {
                    Release(f);
                    (done ?? (done = new List<int>())).Add(pair.Key);
                    continue;
                }
                if (f.Body != null && !f.Body.isKinematic)
                {
                    f.Body.isKinematic = true;
                    f.MadeKinematic = true;
                }
                Transform t = f.Enemy.transform;
                Vector3 target = WorldSpace.ToLocal(f.TargetShared);
                Vector3 pos = (t.position - target).sqrMagnitude > 64f ? target : Vector3.Lerp(t.position, target, 1f - Mathf.Exp(-15f * dt));
                Quaternion rot = Quaternion.Slerp(t.rotation, f.TargetRot, 1f - Mathf.Exp(-15f * dt));
                t.SetPositionAndRotation(pos, rot);
                if (f.Body != null)
                {
                    f.Body.position = pos;
                    f.Body.rotation = rot;
                }
            }
            if (done != null)
            {
                foreach (int id in done)
                {
                    follows.Remove(id);
                }
            }
        }

        private static void Release(Follow f)
        {
            if (f.Body != null && f.MadeKinematic)
            {
                f.Body.isKinematic = false;
            }
            f.MadeKinematic = false;
        }

        private void ReleaseAll()
        {
            foreach (Follow f in follows.Values)
            {
                Release(f);
            }
            follows.Clear();
        }
    }

    [HarmonyPatch(typeof(AITargetComponent), nameof(AITargetComponent.CheckForTarget))]
    internal static class EnemyTargetPatch
    {
        [HarmonyPostfix]
        private static void Postfix(AITargetComponent __instance, Vector3 pos, float scanRadius, AISightComponent sightComponent, Transform self)
        {
            try
            {
                EnemyMotion.ConsiderTeammates(__instance, pos, scanRadius, sightComponent, self);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't point an enemy at a teammate: " + e.Message);
            }
        }
    }
}
