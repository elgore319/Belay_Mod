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
    /// Enemies are shared:
    ///  - Hits add up: when you hurt an enemy, its health drops on every PC, so teammates can wear the
    ///    same enemy down together.
    ///  - When an enemy dies on one PC, it dies on every PC.
    /// Each enemy is recognised by its kind and where it spawned (the seeded level puts it at the same
    /// spot for everyone), falling back to the nearest enemy of that kind close to where it was hit.
    /// Roaches are left alone: picking them up is your own money.
    /// </summary>
    public class EnemySync : MonoBehaviour
    {
        public static EnemySync Instance { get; private set; }

        internal static bool Applying;

        private class Entry
        {
            public AIGameEntity Enemy;
            public string Kind;
            public Vector3 SpawnShared;
        }

        private readonly Dictionary<AIGameEntity, Entry> entries = new Dictionary<AIGameEntity, Entry>();
        private int runId;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.EnemyHurt, HandleHurt);
            NetSession.Instance.On(PacketType.EnemyKilled, HandleKilled);
        }

        internal static bool Live => Instance != null && NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready && !Applying;

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (net != null && net.InLobby && runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                entries.Clear();
            }
        }

        internal static bool Tracked(GameEntity e) => e is AIGameEntity && !(e is DEN_Roach);

        internal static string KindOf(GameEntity e) => e.name.Replace("(Clone)", "").Trim();

        internal void Track(AIGameEntity enemy) => Register(enemy);

        /// <summary>Where this enemy spawned, in shared space.</summary>
        internal Vector3 SpawnOf(AIGameEntity enemy) => Register(enemy).SpawnShared;

        /// <summary>Every live enemy this PC knows about.</summary>
        internal IEnumerable<AIGameEntity> Known()
        {
            foreach (AIGameEntity e in entries.Keys)
            {
                if (e != null && !e.dead)
                {
                    yield return e;
                }
            }
        }

        private Entry Register(AIGameEntity enemy)
        {
            if (entries.TryGetValue(enemy, out Entry known))
            {
                return known;
            }
            var entry = new Entry { Enemy = enemy, Kind = KindOf(enemy), SpawnShared = WorldSpace.ToShared(enemy.transform.position) };
            entries[enemy] = entry;
            return entry;
        }

        /// <summary>The same enemy here: same kind, spawned at (nearly) the same spot, or else nearest to where it was.</summary>
        internal AIGameEntity Find(string kind, Vector3 spawnShared, Vector3 nowShared)
        {
            AIGameEntity bySpawn = null, byNow = null;
            float bestSpawn = 2.5f, bestNow = 5f;
            var stale = new List<AIGameEntity>();
            foreach (var pair in entries)
            {
                Entry e = pair.Value;
                if (pair.Key == null)
                {
                    stale.Add(pair.Key);
                    continue;
                }
                if (e.Kind != kind || pair.Key.dead)
                {
                    continue;
                }
                float ds = Vector3.Distance(e.SpawnShared, spawnShared);
                if (ds < bestSpawn)
                {
                    bestSpawn = ds;
                    bySpawn = pair.Key;
                }
                float dn = Vector3.Distance(WorldSpace.ToShared(pair.Key.transform.position), nowShared);
                if (dn < bestNow)
                {
                    bestNow = dn;
                    byNow = pair.Key;
                }
            }
            foreach (AIGameEntity s in stale)
            {
                entries.Remove(s);
            }
            if (bySpawn != null || byNow != null)
            {
                return bySpawn ?? byNow;
            }
            // Not tracked yet here: look through every enemy that's loaded.
            AIGameEntity best = null;
            float bestDist = 6f;
            foreach (AIGameEntity candidate in FindObjectsByType<AIGameEntity>(FindObjectsSortMode.None))
            {
                if (candidate == null || candidate.dead || !Tracked(candidate) || KindOf(candidate) != kind)
                {
                    continue;
                }
                Vector3 here = WorldSpace.ToShared(candidate.transform.position);
                float d = Mathf.Min(Vector3.Distance(here, spawnShared), Vector3.Distance(here, nowShared));
                if (entries.TryGetValue(candidate, out Entry known))
                {
                    d = Mathf.Min(d, Vector3.Distance(known.SpawnShared, spawnShared));
                }
                if (d < bestDist)
                {
                    bestDist = d;
                    best = candidate;
                }
            }
            if (best != null)
            {
                Register(best);
            }
            return best;
        }

        private static void WriteWho(BinaryWriter w, Entry e)
        {
            w.Write(e.Kind);
            w.WriteVector(e.SpawnShared);
            w.WriteVector(WorldSpace.ToShared(e.Enemy.transform.position));
        }

        // ---------------------------------------------------------------- Local events

        internal void OnLocalHurt(AIGameEntity enemy, float healthAfter)
        {
            Entry e = Register(enemy);
            NetSession.Instance.Send(PacketType.EnemyHurt, w =>
            {
                WriteWho(w, e);
                w.Write(healthAfter);
            });
        }

        internal void OnLocalKill(AIGameEntity enemy, string type)
        {
            Entry e = Register(enemy);
            Plugin.Log.LogInfo($"Enemy died here, sharing: {e.Kind}");
            NetSession.Instance.Send(PacketType.EnemyKilled, w =>
            {
                WriteWho(w, e);
                w.Write(type ?? "");
            });
        }

        // ---------------------------------------------------------------- Teammates

        private void HandleHurt(SteamId origin, BinaryReader r)
        {
            string kind = r.ReadString();
            Vector3 spawn = r.ReadVector();
            Vector3 now = r.ReadVector();
            float health = r.ReadSingle();
            if (!WorldSpace.Ready || origin == NetSession.LocalId)
            {
                return;
            }
            AIGameEntity enemy = Find(kind, spawn, now);
            if (enemy == null || enemy.dead)
            {
                return;
            }
            var field = Traverse.Create(enemy).Field("health");
            float current = field.GetValue<float>();
            if (health < current)
            {
                field.SetValue(health);
            }
            if (health <= 0f)
            {
                KillHere(enemy, "belay-sync");
            }
        }

        private void HandleKilled(SteamId origin, BinaryReader r)
        {
            string kind = r.ReadString();
            Vector3 spawn = r.ReadVector();
            Vector3 now = r.ReadVector();
            string type = r.ReadString();
            if (!WorldSpace.Ready || origin == NetSession.LocalId)
            {
                return;
            }
            AIGameEntity enemy = Find(kind, spawn, now);
            if (enemy == null)
            {
                Plugin.Log.LogInfo($"Teammate killed a {kind} that isn't here");
                return;
            }
            if (!enemy.dead)
            {
                Plugin.Log.LogInfo($"Teammate killed a {kind}");
                KillHere(enemy, string.IsNullOrEmpty(type) ? "belay-sync" : type);
            }
        }

        private static void KillHere(AIGameEntity enemy, string type)
        {
            Applying = true;
            try
            {
                enemy.Kill(type, null);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't kill {enemy.name} here: {e.Message}");
                enemy.gameObject.SetActive(false);
            }
            finally
            {
                Applying = false;
            }
        }
    }

    [HarmonyPatch]
    internal static class EnemySyncPatches
    {
        // Remember where each enemy spawned, before it moves.
        [HarmonyPatch(typeof(GameEntity), nameof(GameEntity.Start))]
        [HarmonyPrefix]
        private static void EntityStart(GameEntity __instance)
        {
            // Levels generated as the run loads start their enemies before the player exists, so this
            // doesn't wait for the world to be ready (the world offset is still zero then).
            if (EnemySync.Instance != null && !EnemySync.Applying && NetSession.Instance != null && NetSession.Instance.InLobby
                && EnemySync.Tracked(__instance))
            {
                EnemySync.Instance.Track((AIGameEntity)__instance);
            }
        }

        [HarmonyPatch(typeof(GameEntity), nameof(GameEntity.Damage))]
        [HarmonyPrefix]
        private static void DamagePrefix(GameEntity __instance, out float __state)
        {
            __state = __instance != null ? Traverse.Create(__instance).Field("health").GetValue<float>() : 0f;
        }

        /// <summary>Share hits the local player dealt; the kill itself goes out through Kill.</summary>
        [HarmonyPatch(typeof(GameEntity), nameof(GameEntity.Damage))]
        [HarmonyPostfix]
        private static void DamagePostfix(GameEntity __instance, Damageable.DamageInfo info, float __state)
        {
            if (!EnemySync.Live || !EnemySync.Tracked(__instance) || __instance.dead || info == null)
            {
                return;
            }
            ENT_Player player = ENT_Player.playerObject;
            bool fromMe = player != null && (info.sourceEntity == player
                || (info.sourceObject != null && info.sourceObject == player.gameObject) || info.HasTag("player"));
            float now = Traverse.Create(__instance).Field("health").GetValue<float>();
            if (fromMe && now < __state)
            {
                EnemySync.Instance.OnLocalHurt((AIGameEntity)__instance, now);
            }
        }

        // A few enemies override Kill without calling the base one, so they're patched too.
        [HarmonyPatch(typeof(GameEntity), nameof(GameEntity.Kill), new[] { typeof(string), typeof(Damageable.DamageInfo) })]
        [HarmonyPrefix]
        private static void KillPrefix(GameEntity __instance, string type)
        {
            if (EnemySync.Live && EnemySync.Tracked(__instance) && !__instance.dead)
            {
                try
                {
                    EnemySync.Instance.OnLocalKill((AIGameEntity)__instance, type);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Enemy death not shared ({__instance.name}): {e.Message}");
                }
            }
        }
    }

    [HarmonyPatch]
    internal static class EnemyKillOverridePatches
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var args = new[] { typeof(string), typeof(Damageable.DamageInfo) };
            yield return AccessTools.Method(typeof(DEN_Bloodbug), nameof(DEN_Bloodbug.Kill), args);
            yield return AccessTools.Method(typeof(DEN_Mother), nameof(DEN_Mother.Kill), args);
            yield return AccessTools.Method(typeof(DEN_Turret), nameof(DEN_Turret.Kill), args);
        }

        [HarmonyPrefix]
        private static void Prefix(GameEntity __instance, string type)
        {
            if (EnemySync.Live && EnemySync.Tracked(__instance) && !__instance.dead)
            {
                try
                {
                    EnemySync.Instance.OnLocalKill((AIGameEntity)__instance, type);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Enemy death not shared ({__instance.name}): {e.Message}");
                }
            }
        }
    }
}
