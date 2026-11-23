using System.Collections;
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
    /// Proximity spawns used to roll separately on each PC. In a lobby the host decides and others copy: trigger
    /// spawners (UT_TriggerSpawner) fire on the host when any living player is close and run with the same seed
    /// everywhere; wave zones (ENV_DenizenWaveZone) run only on the host and send each spawn; tick egg sacs
    /// (ENV_TickEggSack) burst everywhere at once. PCs without that map part yet apply it when it loads. If a
    /// teammate reaches one the host hasn't built, the host hands it to them.
    /// </summary>
    public class HostSpawns : MonoBehaviour
    {
        public static HostSpawns Instance { get; private set; }

        internal static bool Applying;
        /// <summary>While set, the next SpawnSettings.RandomCheck gives this answer (still drawing its one random number).</summary>
        internal static bool? ForcedCheck;

        private readonly Dictionary<string, bool> spawnerFired = new Dictionary<string, bool>();
        private readonly Dictionary<string, List<(string kind, Vector3 shared)>> waveSpawns = new Dictionary<string, List<(string, Vector3)>>();
        private readonly HashSet<string> sacsBurst = new HashSet<string>();
        /// <summary>Host: spawners that have gone off (or were handed to a teammate) this run.</summary>
        private readonly HashSet<string> hostFired = new HashSet<string>();
        /// <summary>Client: spawners we asked the host about.</summary>
        private readonly HashSet<string> requested = new HashSet<string>();
        /// <summary>
        /// Host: wave zones and egg sacs handed to a teammate. Client: the ones handed to us, which we run for
        /// everyone.
        /// </summary>
        private readonly HashSet<string> delegatedAreas = new HashSet<string>();
        private readonly HashSet<string> areaRequested = new HashSet<string>();
        private static readonly Dictionary<Component, string> idCache = new Dictionary<Component, string>();
        private int runId;

        private const byte AreaWave = 1;
        private const byte AreaSac = 2;
        private float nextPendingCheck;
        private int pendingStamp;
        private float lastFullPending;

        private static readonly AccessTools.FieldRef<UT_TriggerSpawner, float> SpawnerStart = AccessTools.FieldRefAccess<UT_TriggerSpawner, float>("startTime");
        private static readonly AccessTools.FieldRef<ENV_DenizenWaveZone, List<Denizen>> ZoneDenizens = AccessTools.FieldRefAccess<ENV_DenizenWaveZone, List<Denizen>>("denizensInZone");
        private static readonly AccessTools.FieldRef<ENV_DenizenWaveZone, int> ZoneTotal = AccessTools.FieldRefAccess<ENV_DenizenWaveZone, int>("totalSpawned");
        private static readonly AccessTools.FieldRef<ENV_TickEggSack, bool> SacSpawned = AccessTools.FieldRefAccess<ENV_TickEggSack, bool>("hasSpawned");

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.SpawnerFired, HandleSpawner);
            NetSession.Instance.On(PacketType.WaveSpawned, HandleWave);
            NetSession.Instance.On(PacketType.TickSackBurst, HandleSac);
            NetSession.Instance.On(PacketType.SpawnerRequest, HandleRequest);
            NetSession.Instance.On(PacketType.SpawnerDelegate, HandleDelegate);
            NetSession.Instance.On(PacketType.AreaRequest, HandleAreaRequest);
            NetSession.Instance.On(PacketType.AreaDelegate, HandleAreaDelegate);
        }

        internal static bool Live => Instance != null && NetSession.InLobbyRun && !Applying;
        internal static bool IsHost => NetSession.Instance.IsHost;

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
                spawnerFired.Clear();
                waveSpawns.Clear();
                sacsBurst.Clear();
                hostFired.Clear();
                requested.Clear();
                delegatedAreas.Clear();
                areaRequested.Clear();
                idCache.Clear();
            }
            if (Time.unscaledTime >= nextPendingCheck && (spawnerFired.Count > 0 || waveSpawns.Count > 0 || sacsBurst.Count > 0))
            {
                nextPendingCheck = Time.unscaledTime + 1f;
                // Waiting spawns only land once their level loads here: no full scan until one does.
                if (LevelStamp.Due(ref pendingStamp, ref lastFullPending, 5f))
                {
                    ApplyPending();
                }
            }
        }

        // ------------------------------------------------------------ Shared helpers

        /// <summary>Distance from a spot to the nearest living player (this PC's or a teammate's).</summary>
        internal static float NearestPlayer(Vector3 pos)
        {
            float best = float.MaxValue;
            ENT_Player me = ENT_Player.playerObject;
            if (me != null && !me.dead)
            {
                best = Vector3.Distance(me.transform.position, pos);
            }
            if (PlayerSync.Instance != null)
            {
                foreach (RemotePlayer remote in PlayerSync.Instance.Remotes.Values)
                {
                    if (remote != null && !remote.Latest.Dead && Time.unscaledTime - remote.LastReceived <= 3f)
                    {
                        best = Mathf.Min(best, Vector3.Distance(remote.LocalPosition, pos));
                    }
                }
            }
            return best;
        }

        /// <summary>Living players' positions (this PC's first).</summary>
        internal static IEnumerable<Vector3> LivingPlayers()
        {
            ENT_Player me = ENT_Player.playerObject;
            if (me != null && !me.dead)
            {
                yield return me.transform.position;
            }
            if (PlayerSync.Instance != null)
            {
                foreach (RemotePlayer remote in PlayerSync.Instance.Remotes.Values)
                {
                    if (remote != null && !remote.Latest.Dead && Time.unscaledTime - remote.LastReceived <= 3f)
                    {
                        yield return remote.LocalPosition;
                    }
                }
            }
        }

        private static int SeedFor(string id) => WorldEvents.SeedFor("spawn:" + id);

        private static T FindById<T>(string id) where T : Component => IdLookup.Find<T>(id, FindObjectsInactive.Include);

        /// <summary>One pass over every object of a type, by id (instead of a full scan for each id we wait on).</summary>
        private static Dictionary<string, T> AllById<T>() where T : Component
        {
            var map = new Dictionary<string, T>();
            foreach (T c in FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (c != null)
                {
                    map[IdOf(c)] = c;
                }
            }
            return map;
        }

        /// <summary>ButtonSync.ObjectId, worked out once per object (it builds a path string every call).</summary>
        internal static string IdOf(Component c)
        {
            if (!idCache.TryGetValue(c, out string id))
            {
                id = ButtonSync.ObjectId(c);
                idCache[c] = id;
            }
            return id;
        }

        private void ApplyPending()
        {
            Dictionary<string, UT_TriggerSpawner> spawners = spawnerFired.Count > 0 ? AllById<UT_TriggerSpawner>() : null;
            Dictionary<string, ENV_DenizenWaveZone> zones = waveSpawns.Count > 0 ? AllById<ENV_DenizenWaveZone>() : null;
            Dictionary<string, ENV_TickEggSack> sacs = sacsBurst.Count > 0 ? AllById<ENV_TickEggSack>() : null;
            foreach (string id in new List<string>(spawnerFired.Keys))
            {
                spawners.TryGetValue(id, out UT_TriggerSpawner s);
                if (s != null)
                {
                    bool pass = spawnerFired[id];
                    spawnerFired.Remove(id);
                    RunSpawner(s, pass);
                }
            }
            foreach (string id in new List<string>(waveSpawns.Keys))
            {
                zones.TryGetValue(id, out ENV_DenizenWaveZone z);
                if (z != null)
                {
                    List<(string kind, Vector3 shared)> list = waveSpawns[id];
                    waveSpawns.Remove(id);
                    foreach (var (kind, shared) in list)
                    {
                        SpawnInZone(z, kind, WorldSpace.ToLocal(shared));
                    }
                }
            }
            foreach (string id in new List<string>(sacsBurst))
            {
                sacs.TryGetValue(id, out ENV_TickEggSack sac);
                if (sac != null)
                {
                    sacsBurst.Remove(id);
                    Burst(sac);
                }
            }
        }

        // ------------------------------------------------------------ Trigger spawners

        /// <summary>Replaces UT_TriggerSpawner.FixedUpdate in a lobby. Returns false to skip the game's own.</summary>
        internal bool SpawnerTick(UT_TriggerSpawner s)
        {
            if (SpawnerStart(s) > Time.time || CL_GameManager.gMan == null || CL_GameManager.gMan.localPlayer == null)
            {
                return false;
            }
            if (!IsHost)
            {
                // The host says when. If our player is right there, make sure the host knows (it may not
                // have built this part of the map yet).
                ENT_Player me = ENT_Player.playerObject;
                if (me != null && !me.dead && Vector3.Distance(me.transform.position, s.transform.position) <= s.playerVicinity)
                {
                    string rid = ButtonSync.ObjectId(s);
                    if (requested.Add(rid))
                    {
                        NetSession.Instance.Send(PacketType.SpawnerRequest, w => w.Write(rid));
                    }
                }
                return false;
            }
            if (NearestPlayer(s.transform.position) > s.playerVicinity)
            {
                return false;
            }
            string id = ButtonSync.ObjectId(s);
            if (!hostFired.Add(id))
            {
                return false; // a teammate already ran this one
            }
            FireAndShare(s, id);
            return false;
        }

        /// <summary>Rolls this spawner's own chance with the shared seed, tells everyone, and runs it here.</summary>
        private static void FireAndShare(UT_TriggerSpawner s, string id)
        {
            Random.State state = Random.state;
            bool pass;
            Random.InitState(SeedFor(id));
            try
            {
                pass = s.spawnSettings.RandomCheck(runOnSpawn: false);
            }
            finally
            {
                Random.state = state;
            }
            NetSession.Instance.Send(PacketType.SpawnerFired, w => { w.Write(id); w.Write(pass); });
            RunSpawner(s, pass);
        }

        /// <summary>Host: a teammate is at a spawner. If we haven't built it, they run it.</summary>
        private void HandleRequest(SteamId origin, BinaryReader r)
        {
            string id = r.ReadString();
            if (!IsHost || hostFired.Contains(id))
            {
                return;
            }
            if (FindById<UT_TriggerSpawner>(id) != null)
            {
                return; // we have it: it goes off here as soon as they're close enough
            }
            hostFired.Add(id);
            ulong to = origin.Value;
            NetSession.Instance.Send(PacketType.SpawnerDelegate, w => { w.Write(id); w.Write(to); });
            Plugin.Log.LogInfo($"Spawner not built here yet, handed to {NetSession.NameOf(origin)}: {id}");
        }

        private void HandleDelegate(SteamId origin, BinaryReader r)
        {
            string id = r.ReadString();
            ulong to = r.ReadUInt64();
            if (IsHost || to != NetSession.LocalId.Value)
            {
                return;
            }
            UT_TriggerSpawner s = FindById<UT_TriggerSpawner>(id);
            if (s != null && s.gameObject.activeInHierarchy)
            {
                FireAndShare(s, id);
            }
        }

        private static void RunSpawner(UT_TriggerSpawner s, bool pass)
        {
            string id = ButtonSync.ObjectId(s);
            Random.State state = Random.state;
            Random.InitState(SeedFor(id));
            Applying = true;
            ForcedCheck = pass;
            try
            {
                s.Spawn();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Spawner {s.name} failed: {e.Message}");
            }
            finally
            {
                ForcedCheck = null;
                Applying = false;
                Random.state = state;
            }
        }

        private void HandleSpawner(SteamId origin, BinaryReader r)
        {
            string id = r.ReadString();
            bool pass = r.ReadBoolean();
            if (origin == NetSession.LocalId)
            {
                return;
            }
            if (IsHost)
            {
                hostFired.Add(id); // a teammate ran one we handed them
            }
            UT_TriggerSpawner s = FindById<UT_TriggerSpawner>(id);
            if (s == null || !s.gameObject.activeInHierarchy)
            {
                if (s == null) spawnerFired[id] = pass; // not built here yet
                return;
            }
            RunSpawner(s, pass);
        }

        // ------------------------------------------------------------ Wave zones

        /// <summary>Host: spawns the wave as usual, then sends what came out.</summary>
        internal void AfterHostWave(ENV_DenizenWaveZone zone, List<Denizen> before)
        {
            List<Denizen> now = ZoneDenizens(zone);
            if (now == null)
            {
                return;
            }
            string id = IdOf(zone);
            foreach (Denizen d in now)
            {
                if (d == null || before.Contains(d))
                {
                    continue;
                }
                string kind = EnemySync.KindOf(d);
                Vector3 shared = WorldSpace.ToShared(d.transform.position);
                NetSession.Instance.Send(PacketType.WaveSpawned, w =>
                {
                    w.Write(id);
                    w.Write(kind);
                    w.Write(shared.x); w.Write(shared.y); w.Write(shared.z);
                });
            }
        }

        internal static List<Denizen> Snapshot(ENV_DenizenWaveZone zone)
        {
            List<Denizen> list = ZoneDenizens(zone);
            return list != null ? new List<Denizen>(list) : new List<Denizen>();
        }

        private void HandleWave(SteamId origin, BinaryReader r)
        {
            string id = r.ReadString();
            string kind = r.ReadString();
            Vector3 shared = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (origin == NetSession.LocalId || RunsArea(id))
            {
                return;
            }
            ENV_DenizenWaveZone zone = FindById<ENV_DenizenWaveZone>(id);
            if (zone == null)
            {
                if (!waveSpawns.TryGetValue(id, out var list))
                {
                    waveSpawns[id] = list = new List<(string, Vector3)>();
                }
                list.Add((kind, shared));
                return;
            }
            SpawnInZone(zone, kind, WorldSpace.ToLocal(shared));
        }

        /// <summary>The same thing the zone's own spawn does, for one enemy of this kind at this spot.</summary>
        private static void SpawnInZone(ENV_DenizenWaveZone zone, string kind, Vector3 position)
        {
            Denizen prefab = null;
            foreach (ENV_DenizenWaveZone.DenizenSpawnInfo info in zone.denizenSpawnInfos)
            {
                if (info?.denizenPrefab != null && info.denizenPrefab.name == kind)
                {
                    prefab = info.denizenPrefab;
                    break;
                }
            }
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"Wave zone {zone.name}: no {kind} to spawn here");
                return;
            }
            Applying = true;
            try
            {
                Denizen d = Instantiate(prefab, position, Quaternion.identity, zone.transform);
                ZoneTotal(zone)++;
                List<Denizen> inZone = ZoneDenizens(zone);
                inZone?.Add(d);
                d._OnDeath = (System.Action<GameEntity>)System.Delegate.Combine(d._OnDeath, (System.Action<GameEntity>)(_ => inZone?.Remove(d)));
                zone.DenizenSpawnEvent?.Invoke(d);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Wave zone {zone.name} spawn failed: {e.Message}");
            }
            finally
            {
                Applying = false;
            }
        }

        // ------------------------------------------------------------ Tick egg sacs

        internal void OnHostBurst(ENV_TickEggSack sac)
        {
            string id = IdOf(sac);
            NetSession.Instance.Send(PacketType.TickSackBurst, w => w.Write(id));
        }

        private void HandleSac(SteamId origin, BinaryReader r)
        {
            string id = r.ReadString();
            if (origin == NetSession.LocalId || RunsArea(id))
            {
                return;
            }
            ENV_TickEggSack sac = FindById<ENV_TickEggSack>(id);
            if (sac == null)
            {
                sacsBurst.Add(id);
                return;
            }
            Burst(sac);
        }

        // ------------------------------------------------------------ Areas the host doesn't have

        /// <summary>Whether this PC runs this wave zone or egg sac (the host, unless it handed it on).</summary>
        internal bool RunsArea(string id)
        {
            return IsHost ? !delegatedAreas.Contains(id) : delegatedAreas.Contains(id);
        }

        internal static bool RunsHere(Component c) => Instance != null && Instance.RunsArea(IdOf(c));

        /// <summary>
        /// Client: our player reached a wave zone or egg sac the host may not have built; ask once to be handed
        /// it.
        /// </summary>
        internal void NoteNear(Component c, byte kind)
        {
            if (IsHost)
            {
                return;
            }
            string id = IdOf(c);
            if (delegatedAreas.Contains(id) || !areaRequested.Add(id))
            {
                return;
            }
            NetSession.Instance.Send(PacketType.AreaRequest, w => { w.Write(kind); w.Write(id); });
        }

        private void HandleAreaRequest(SteamId origin, BinaryReader r)
        {
            byte kind = r.ReadByte();
            string id = r.ReadString();
            if (!IsHost || delegatedAreas.Contains(id))
            {
                return;
            }
            Component here = kind == AreaWave ? FindById<ENV_DenizenWaveZone>(id) : (Component)FindById<ENV_TickEggSack>(id);
            if (here != null && here.gameObject.activeInHierarchy)
            {
                return; // we have it running: it goes off here
            }
            if (kind == AreaSac && here != null && SacSpawned((ENV_TickEggSack)here))
            {
                return; // already burst
            }
            delegatedAreas.Add(id);
            ulong to = origin.Value;
            NetSession.Instance.Send(PacketType.AreaDelegate, w => { w.Write(id); w.Write(to); });
            Plugin.Log.LogInfo($"{(kind == AreaWave ? "Wave zone" : "Egg sac")} not running here, handed to {NetSession.NameOf(origin)}: {id}");
        }

        private void HandleAreaDelegate(SteamId origin, BinaryReader r)
        {
            string id = r.ReadString();
            ulong to = r.ReadUInt64();
            if (IsHost || origin != NetSession.Instance.HostId)
            {
                return;
            }
            if (to == NetSession.LocalId.Value)
            {
                delegatedAreas.Add(id);
                Plugin.Log.LogInfo("Running a wave zone or egg sac for the team: " + id);
            }
        }

        private void Burst(ENV_TickEggSack sac)
        {
            if (!sac.canSpawn || SacSpawned(sac))
            {
                return;
            }
            SacSpawned(sac) = true;
            StartCoroutine(BurstSoon(sac));
        }

        private static IEnumerator BurstSoon(ENV_TickEggSack sac)
        {
            yield return new WaitForSeconds(Random.Range(0f, sac.burstDelay));
            if (sac != null)
            {
                sac.onSpawnStartEvent?.Invoke();
            }
        }
    }

    [HarmonyPatch]
    internal static class HostSpawnPatches
    {
        [HarmonyPatch(typeof(SpawnTable.SpawnSettings), nameof(SpawnTable.SpawnSettings.RandomCheck))]
        [HarmonyPrefix]
        private static bool RandomCheck(SpawnTable.SpawnSettings __instance, bool runOnSpawn, ref bool __result)
        {
            if (!HostSpawns.ForcedCheck.HasValue)
            {
                return true;
            }
            Random.value.GetHashCode(); // keep the random sequence the same as the game's own check
            __result = HostSpawns.ForcedCheck.Value;
            HostSpawns.ForcedCheck = null; // only the spawner's own check: its spawn table rolls as usual
            if (__result && runOnSpawn)
            {
                __instance.OnSpawn();
            }
            return false;
        }

        [HarmonyPatch(typeof(UT_TriggerSpawner), "FixedUpdate")]
        [HarmonyPrefix]
        private static bool SpawnerFixedUpdate(UT_TriggerSpawner __instance)
        {
            if (!HostSpawns.Live)
            {
                return true;
            }
            try
            {
                return HostSpawns.Instance.SpawnerTick(__instance);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Spawner {__instance.name}: {e.Message}");
                return true;
            }
        }

        // Wave zones run on the host only, counting every living player.
        [HarmonyPatch(typeof(ENV_DenizenWaveZone), "Update")]
        [HarmonyPrefix]
        private static bool WaveUpdate(ENV_DenizenWaveZone __instance)
        {
            if (!HostSpawns.Live)
            {
                return true;
            }
            if (HostSpawns.RunsHere(__instance))
            {
                return true;
            }
            ENT_Player me = ENT_Player.playerObject;
            if (me != null && !me.dead && __instance.waveZoneBounds.Contains(__instance.transform.InverseTransformPoint(me.transform.position)))
            {
                HostSpawns.Instance.NoteNear(__instance, 1);
            }
            return false;
        }

        [HarmonyPatch(typeof(ENV_DenizenWaveZone), nameof(ENV_DenizenWaveZone.IsPlayerInWaveZone))]
        [HarmonyPostfix]
        private static void InZone(ENV_DenizenWaveZone __instance, ref bool __result)
        {
            if (__result || !HostSpawns.Live)
            {
                return;
            }
            foreach (Vector3 p in HostSpawns.LivingPlayers())
            {
                if (__instance.waveZoneBounds.Contains(__instance.transform.InverseTransformPoint(p)))
                {
                    __result = true;
                    return;
                }
            }
        }

        [HarmonyPatch(typeof(ENV_DenizenWaveZone), nameof(ENV_DenizenWaveZone.GetManhattenDistanceFromPlayer))]
        [HarmonyPostfix]
        private static void SpawnDistance(Vector3 position, ref float __result)
        {
            if (!HostSpawns.Live)
            {
                return;
            }
            foreach (Vector3 p in HostSpawns.LivingPlayers())
            {
                Vector3 v = position - p;
                __result = Mathf.Min(__result, Mathf.Abs(v.x) + Mathf.Abs(v.y) + Mathf.Abs(v.z));
            }
        }

        [HarmonyPatch(typeof(ENV_DenizenWaveZone), "SpawnDenizens")]
        [HarmonyPrefix]
        private static void WavePrefix(ENV_DenizenWaveZone __instance, out List<Denizen> __state)
        {
            __state = HostSpawns.Live && HostSpawns.RunsHere(__instance) ? HostSpawns.Snapshot(__instance) : null;
        }

        [HarmonyPatch(typeof(ENV_DenizenWaveZone), "SpawnDenizens")]
        [HarmonyPostfix]
        private static void WavePostfix(ENV_DenizenWaveZone __instance, List<Denizen> __state)
        {
            if (__state == null || !HostSpawns.Live)
            {
                return;
            }
            try
            {
                HostSpawns.Instance.AfterHostWave(__instance, __state);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Wave not shared ({__instance.name}): {e.Message}");
            }
        }

        // Tick egg sacs: burst on the host when anyone is close, on every PC at once.
        [HarmonyPatch(typeof(ENV_TickEggSack), "Update")]
        [HarmonyPrefix]
        private static bool SacUpdate(ENV_TickEggSack __instance, bool ___hasSpawned, out bool __state)
        {
            __state = ___hasSpawned;
            if (!HostSpawns.Live || HostSpawns.RunsHere(__instance))
            {
                return true;
            }
            ENT_Player me = ENT_Player.playerObject;
            if (!___hasSpawned && me != null && !me.dead
                && Vector3.Distance(me.transform.position, __instance.transform.position) <= __instance.playerProximitySpawnRadius * 1.5f)
            {
                HostSpawns.Instance.NoteNear(__instance, 2);
            }
            return false;
        }

        [HarmonyPatch(typeof(ENV_TickEggSack), "Update")]
        [HarmonyPostfix]
        private static void SacUpdatePost(ENV_TickEggSack __instance, bool ___hasSpawned, bool __state)
        {
            if (!__state && ___hasSpawned && HostSpawns.Live && HostSpawns.RunsHere(__instance))
            {
                HostSpawns.Instance.OnHostBurst(__instance);
            }
        }

        [HarmonyPatch(typeof(ENV_TickEggSack), nameof(ENV_TickEggSack.CheckForPlayer))]
        [HarmonyPostfix]
        private static void SacCheck(ENV_TickEggSack __instance, ref bool __result)
        {
            if (__result || !HostSpawns.Live || CL_GameManager.HasActiveFlag("pacifyroaches"))
            {
                return;
            }
            __result = HostSpawns.NearestPlayer(__instance.transform.position) <= __instance.playerProximitySpawnRadius;
        }
    }
}
