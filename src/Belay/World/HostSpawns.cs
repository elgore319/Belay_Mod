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
    /// Things in a level that spawn when the player comes close used to go off on each PC separately,
    /// when that PC's own player got near, with each PC's own dice: different numbers of enemies, in
    /// different spots, or none at all for a teammate standing right next to the host's swarm.
    ///
    /// In a lobby the host decides and the others copy it:
    ///  - Trigger spawners (UT_TriggerSpawner): go off on the host when any living player is close. The
    ///    others run the same spawn with the same seed and the same pass/fail, so the same things appear
    ///    in the same places (enemies are then moved by the host as usual).
    ///  - Wave zones (ENV_DenizenWaveZone): only the host's copy runs, counting any player inside the zone.
    ///    Each enemy it spawns is sent (kind and spot) and spawned the same on the other PCs.
    ///  - Tick egg sacs (ENV_TickEggSack): burst on the host when any player comes close, and on every PC
    ///    at the same moment.
    /// A PC that hasn't built that part of the map yet applies it once the level loads.
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
        private int runId;
        private float nextPendingCheck;

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
        }

        internal static bool Live => Instance != null && NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready && !Applying;
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
            }
            if (!net.IsHost && Time.unscaledTime >= nextPendingCheck && (spawnerFired.Count > 0 || waveSpawns.Count > 0 || sacsBurst.Count > 0))
            {
                nextPendingCheck = Time.unscaledTime + 1f;
                ApplyPending();
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

        private static T FindById<T>(string id) where T : Component
        {
            foreach (T c in FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (c != null && ButtonSync.ObjectId(c) == id)
                {
                    return c;
                }
            }
            return null;
        }

        private void ApplyPending()
        {
            foreach (string id in new List<string>(spawnerFired.Keys))
            {
                UT_TriggerSpawner s = FindById<UT_TriggerSpawner>(id);
                if (s != null)
                {
                    bool pass = spawnerFired[id];
                    spawnerFired.Remove(id);
                    RunSpawner(s, pass);
                }
            }
            foreach (string id in new List<string>(waveSpawns.Keys))
            {
                ENV_DenizenWaveZone z = FindById<ENV_DenizenWaveZone>(id);
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
                ENV_TickEggSack sac = FindById<ENV_TickEggSack>(id);
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
            if (!IsHost)
            {
                return false; // the host says when
            }
            if (SpawnerStart(s) > Time.time || CL_GameManager.gMan == null || CL_GameManager.gMan.localPlayer == null)
            {
                return false;
            }
            if (NearestPlayer(s.transform.position) > s.playerVicinity)
            {
                return false;
            }
            string id = ButtonSync.ObjectId(s);
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
            return false;
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
            if (IsHost)
            {
                return;
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
            string id = ButtonSync.ObjectId(zone);
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
            if (IsHost)
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
            string id = ButtonSync.ObjectId(sac);
            NetSession.Instance.Send(PacketType.TickSackBurst, w => w.Write(id));
        }

        private void HandleSac(SteamId origin, BinaryReader r)
        {
            string id = r.ReadString();
            if (IsHost)
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
        private static bool WaveUpdate() => !HostSpawns.Live || HostSpawns.IsHost;

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
            __state = HostSpawns.Live && HostSpawns.IsHost ? HostSpawns.Snapshot(__instance) : null;
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
        private static bool SacUpdate(bool ___hasSpawned, out bool __state)
        {
            __state = ___hasSpawned;
            return !HostSpawns.Live || HostSpawns.IsHost;
        }

        [HarmonyPatch(typeof(ENV_TickEggSack), "Update")]
        [HarmonyPostfix]
        private static void SacUpdatePost(ENV_TickEggSack __instance, bool ___hasSpawned, bool __state)
        {
            if (!__state && ___hasSpawned && HostSpawns.Live && HostSpawns.IsHost)
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
