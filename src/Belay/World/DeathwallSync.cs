using System.Collections.Generic;
using System.IO;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// The Core's mass is a set of moving walls (DEN_Deathwall), not the rising goo. Each PC ran its own walls,
    /// started them when its own player got there and sped them up by how far ahead its own player was, so the
    /// walls were in different places on every PC and caught whoever was behind. Now:
    /// - A wall in an interlude or break room waits until every living player is in that room.
    /// - The host's walls are the real ones: their positions stream to the other PCs, which start the same walls
    ///   and hold theirs where the host's are. Each PC's wall still kills its own player.
    /// </summary>
    public class DeathwallSync : MonoBehaviour
    {
        public static DeathwallSync Instance { get; private set; }

        private const float SendInterval = 0.25f;
        private const float SnapDistance = 4f;
        private const float StaleAfter = 2f;

        private static readonly AccessTools.FieldRef<DEN_DeathwallManager, List<DEN_Deathwall>> Running =
            AccessTools.FieldRefAccess<DEN_DeathwallManager, List<DEN_Deathwall>>("runningDeathwalls");
        private static readonly System.Reflection.MethodInfo StartWall = AccessTools.Method(typeof(DEN_DeathwallManager), "StartDeathwall");

        private struct HostWall
        {
            public Vector3 Shared;
            public float Time;
        }

        private readonly Dictionary<string, HostWall> hostWalls = new Dictionary<string, HostWall>();
        private readonly Dictionary<string, DEN_Deathwall> wallsById = new Dictionary<string, DEN_Deathwall>();
        private readonly List<DEN_DeathwallManager> managers = new List<DEN_DeathwallManager>();
        private float nextScan;
        private float sendTimer;
        private int runId = -1;

        /// <summary>Gated rooms whose walls were let go (everyone arrived), by level id.</summary>
        private static readonly HashSet<string> Released = new HashSet<string>();
        private static readonly HashSet<string> AnnouncedWaits = new HashSet<string>();

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.DeathwallState, HandleState);
        }

        internal static bool Live => NetSession.InLobbyRun && WorldSpace.Ready;

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
                Released.Clear();
                AnnouncedWaits.Clear();
                hostWalls.Clear();
                wallsById.Clear();
            }
            if (!Live)
            {
                return;
            }
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 2f;
                Rescan();
            }
            if (net.IsHost)
            {
                sendTimer -= Time.unscaledDeltaTime;
                if (sendTimer <= 0f)
                {
                    sendTimer = SendInterval;
                    SendWalls();
                }
            }
        }

        private void Rescan()
        {
            managers.Clear();
            managers.AddRange(FindObjectsByType<DEN_DeathwallManager>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            wallsById.Clear();
            foreach (DEN_Deathwall wall in FindObjectsByType<DEN_Deathwall>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (wall != null)
                {
                    wallsById[ButtonSync.ObjectId(wall)] = wall;
                }
            }
        }

        // ---------------------------------------------------------------- Host

        private void SendWalls()
        {
            var running = new List<DEN_Deathwall>();
            foreach (DEN_DeathwallManager m in managers)
            {
                List<DEN_Deathwall> list = m != null ? Running(m) : null;
                if (list == null) continue;
                foreach (DEN_Deathwall w in list)
                {
                    if (w != null && running.Count < 24) running.Add(w);
                }
            }
            if (running.Count == 0)
            {
                return;
            }
            NetSession.Instance.Send(PacketType.DeathwallState, w =>
            {
                w.Write((byte)running.Count);
                foreach (DEN_Deathwall wall in running)
                {
                    w.Write(ButtonSync.ObjectId(wall));
                    w.WriteVector(WorldSpace.ToShared(wall.transform.position));
                }
            }, reliable: false);
        }

        // ---------------------------------------------------------------- Other PCs

        private void HandleState(SteamId origin, BinaryReader r)
        {
            if (origin != NetSession.Instance.HostId)
            {
                return;
            }
            int count = r.ReadByte();
            for (int i = 0; i < count; i++)
            {
                string id = r.ReadString();
                Vector3 shared = r.ReadVector();
                hostWalls[id] = new HostWall { Shared = shared, Time = Time.unscaledTime };
            }
        }

        /// <summary>After this PC moved its walls: start the host's running walls here and hold them where the host's are.</summary>
        internal void FollowHost()
        {
            if (!Live || NetSession.Instance.IsHost || hostWalls.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<string, HostWall> pair in hostWalls)
            {
                if (Time.unscaledTime - pair.Value.Time > StaleAfter)
                {
                    continue;
                }
                if (!wallsById.TryGetValue(pair.Key, out DEN_Deathwall wall) || wall == null)
                {
                    continue;
                }
                DEN_DeathwallManager owner = wall.GetComponentInParent<DEN_DeathwallManager>(true);
                List<DEN_Deathwall> running = owner != null ? Running(owner) : null;
                if (running == null)
                {
                    continue;
                }
                if (!running.Contains(wall) && wall.points != null && wall.pointIndex < wall.points.Count)
                {
                    try
                    {
                        StartWall?.Invoke(owner, new object[] { wall });
                        Plugin.Log.LogInfo("Started the host's mass wall here: " + pair.Key);
                    }
                    catch (System.Exception e)
                    {
                        Plugin.Log.LogWarning($"Couldn't start the host's mass wall ({pair.Key}): {e.Message}");
                    }
                }
                Vector3 target = WorldSpace.ToLocal(pair.Value.Shared);
                Transform t = wall.transform;
                float gap = Vector3.Distance(t.position, target);
                t.position = gap > SnapDistance ? target : Vector3.MoveTowards(t.position, target, Mathf.Max(gap * 0.5f, 0.05f));
            }
        }

        // ---------------------------------------------------------------- Waiting in gated rooms

        /// <summary>True while this level is an interlude or break room that not every living player has reached.</summary>
        internal static bool Hold(M_Level level)
        {
            if (!Live || level == null || !InterludeGate.IsGatedRoom(level))
            {
                return false;
            }
            string id = ButtonSync.ObjectId(level);
            if (Released.Contains(id))
            {
                return false;
            }
            List<string> missing = InterludeGate.Missing(level);
            ENT_Player me = ENT_Player.playerObject;
            bool meThere = me == null || me.dead || InterludeGate.InGatedRoom(me.transform.position);
            if (missing.Count == 0 && meThere)
            {
                Released.Add(id);
                Plugin.Log.LogInfo("Everyone reached " + level.name + ": the mass walls move now");
                return false;
            }
            if (AnnouncedWaits.Add(id))
            {
                Plugin.Log.LogInfo($"Holding the mass walls in {level.name} until everyone is there (waiting on {(missing.Count > 0 ? string.Join(", ", missing) : "you")})");
            }
            return true;
        }
    }

    [HarmonyPatch]
    internal static class DeathwallHoldPatch
    {
        private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(DEN_DeathwallManager), "UpdateDeathwallsInLevel");

        [HarmonyPrefix]
        private static bool Prefix(object __1)
        {
            if (!DeathwallSync.Live || __1 == null)
            {
                return true;
            }
            M_Level level = Traverse.Create(__1).Field("level").GetValue<M_Level>();
            return !DeathwallSync.Hold(level);
        }
    }

    [HarmonyPatch(typeof(DEN_DeathwallManager), "Update")]
    internal static class DeathwallFollowPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            DeathwallSync.Instance?.FollowHost();
        }
    }
}
