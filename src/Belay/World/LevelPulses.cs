using System;
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
    /// Level effects on their own random clock now fire at once for everyone: lightning arcs, slow random timers
    /// (sparks, bursts, flickers; anything under 1.5 s stays local), and gantry turret drone launches. The host
    /// decides, or the first nearby PC if the host isn't close.
    /// </summary>
    public class LevelPulses : MonoBehaviour
    {
        public static LevelPulses Instance { get; private set; }

        internal const float MinTimerInterval = 1.5f;
        private const byte KindLightning = 0, KindTimer = 1;

        internal static bool Applying;

        private static readonly Dictionary<string, float> lastHeard = new Dictionary<string, float>();

        private static readonly AccessTools.FieldRef<ENV_LightningDischarge, float> StrikeWait =
            AccessTools.FieldRefAccess<ENV_LightningDischarge, float>("strikeWaitTime");
        private static readonly AccessTools.FieldRef<UT_RandomTimer, float> TimerLeft =
            AccessTools.FieldRefAccess<UT_RandomTimer, float>("timer");
        private static readonly AccessTools.FieldRef<UT_RandomTimer, bool> TimerRunning =
            AccessTools.FieldRefAccess<UT_RandomTimer, bool>("running");
        private static readonly System.Reflection.MethodInfo Discharge = AccessTools.Method(typeof(ENV_LightningDischarge), "Discharge");

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.LevelPulse, HandlePulse);
            NetSession.Instance.On(PacketType.GantryDrones, HandleDrones);
        }

        internal static bool InLobby => NetSession.Instance != null && NetSession.Instance.InLobby;
        internal static bool Live => InLobby && WorldSpace.Ready && !Applying;
        internal static bool IsHost => NetSession.Instance != null && NetSession.Instance.IsHost;

        // ---------------------------------------------------------------- Lightning and timers

        /// <summary>A client's clock runs a full cycle later than the host's, so the host's call usually comes first.</summary>
        internal static void DelayLightning(ENV_LightningDischarge l)
        {
            if (InLobby && !IsHost)
            {
                StrikeWait(l) += l.strikeWaitMax;
            }
        }

        internal static void DelayTimer(UT_RandomTimer t)
        {
            if (InLobby && !IsHost)
            {
                TimerLeft(t) += t.randomMax;
            }
        }

        internal static bool Eligible(UT_RandomTimer t)
        {
            return t != null && t.repeat && t.randomMin >= MinTimerInterval
                && t.GetComponentInParent<HandItem>() == null && t.GetComponentInParent<ENT_Player>() == null;
        }

        internal static bool AboutToStrike(ENV_LightningDischarge l) => StrikeWait(l) <= 0f;

        internal static bool AboutToRun(UT_RandomTimer t)
        {
            return TimerRunning(t) && CL_GameManager.gMan != null && !CL_GameManager.gMan.isPaused && TimerLeft(t) - Time.deltaTime <= 0f;
        }

        internal static void SharePulse(byte kind, Component c)
        {
            string id = ButtonSync.ObjectId(c);
            lastHeard[id] = Time.unscaledTime;
            NetSession.Instance.Send(PacketType.LevelPulse, w =>
            {
                w.Write(kind);
                w.Write(id);
            });
        }

        private void HandlePulse(SteamId origin, BinaryReader r)
        {
            byte kind = r.ReadByte();
            string id = r.ReadString();
            if (origin == NetSession.LocalId || !WorldSpace.Ready)
            {
                return;
            }
            lastHeard[id] = Time.unscaledTime;
            Applying = true;
            try
            {
                if (kind == KindLightning)
                {
                    ENV_LightningDischarge l = FindById<ENV_LightningDischarge>(id);
                    if (l != null && Discharge != null)
                    {
                        StrikeWait(l) = UnityEngine.Random.Range(l.strikeWaitMin, l.strikeWaitMax);
                        DelayLightning(l);
                        l.StartCoroutine((IEnumerator)Discharge.Invoke(l, null));
                    }
                }
                else if (kind == KindTimer)
                {
                    UT_RandomTimer t = FindById<UT_RandomTimer>(id);
                    if (t != null)
                    {
                        t.Reset();
                        DelayTimer(t);
                        t.runEvent?.Invoke();
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't play a shared level effect ({id}): {e.Message}");
            }
            finally
            {
                Applying = false;
            }
        }

        private static T FindById<T>(string id) where T : Component => IdLookup.Find<T>(id, FindObjectsInactive.Exclude);

        // ---------------------------------------------------------------- Gantry drones

        private const float DroneTakeover = 45f;

        private static string DroneKey(DEN_GantryTurret g) => "gantry:" + EnemySync.KindOf(g) + EnemySync.Instance.SpawnOf(g);

        /// <summary>
        /// Whether this PC launches the drones: always on the host, or on a client that hasn't heard from the host
        /// about this turret for a while.
        /// </summary>
        internal static bool LaunchesHere(DEN_GantryTurret g)
        {
            if (!Live || IsHost || EnemySync.Instance == null)
            {
                return true;
            }
            return !lastHeard.TryGetValue(DroneKey(g), out float t) || Time.unscaledTime - t > DroneTakeover;
        }

        internal static void ShareDrones(DEN_GantryTurret g)
        {
            if (!Live || EnemySync.Instance == null)
            {
                return;
            }
            lastHeard[DroneKey(g)] = Time.unscaledTime;
            string kind = EnemySync.KindOf(g);
            Vector3 spawn = EnemySync.Instance.SpawnOf(g);
            Vector3 now = WorldSpace.ToShared(g.transform.position);
            NetSession.Instance.Send(PacketType.GantryDrones, w =>
            {
                w.Write(kind);
                w.WriteVector(spawn);
                w.WriteVector(now);
            });
        }

        private void HandleDrones(SteamId origin, BinaryReader r)
        {
            string kind = r.ReadString();
            Vector3 spawn = r.ReadVector();
            Vector3 now = r.ReadVector();
            if (origin == NetSession.LocalId || !WorldSpace.Ready || EnemySync.Instance == null)
            {
                return;
            }
            if (!(EnemySync.Instance.Find(kind, spawn, now) is DEN_GantryTurret g) || g.dead)
            {
                return;
            }
            lastHeard[DroneKey(g)] = Time.unscaledTime;
            try
            {
                Launch(g);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't launch a gantry's drones: " + e.Message);
            }
        }

        /// <summary>What DroneCheck does, without its "has a target here" check (the target is on another PC).</summary>
        private void Launch(DEN_GantryTurret g)
        {
            if (g.dronePods != null)
            {
                foreach (Animator pod in g.dronePods)
                {
                    if (pod != null) pod.SetTrigger("Spawn");
                }
            }
            if (g.spawnLocations == null || g.drones == null)
            {
                return;
            }
            foreach (Transform t in g.spawnLocations)
            {
                if (t != null) StartCoroutine(SpawnDrone(g.drones, t));
            }
        }

        private static IEnumerator SpawnDrone(Denizen prefab, Transform t)
        {
            yield return new WaitForSeconds(1f);
            if (t == null)
            {
                yield break;
            }
            Denizen drone = Instantiate(prefab, t.position, t.rotation);
            yield return null;
            if (drone != null)
            {
                drone.AddForce(Vector3.up * 90f);
            }
        }
    }

    [HarmonyPatch]
    internal static class LevelPulsePatches
    {
        [HarmonyPatch(typeof(ENV_LightningDischarge), "Start")]
        [HarmonyPostfix]
        private static void LightningStart(ENV_LightningDischarge __instance) => Safe(() => LevelPulses.DelayLightning(__instance));

        [HarmonyPatch(typeof(ENV_LightningDischarge), "Update")]
        [HarmonyPrefix]
        private static void LightningBefore(ENV_LightningDischarge __instance, out bool __state)
        {
            __state = false;
            try
            {
                __state = LevelPulses.Live && LevelPulses.AboutToStrike(__instance);
            }
            catch (Exception)
            {
            }
        }

        [HarmonyPatch(typeof(ENV_LightningDischarge), "Update")]
        [HarmonyPostfix]
        private static void LightningAfter(ENV_LightningDischarge __instance, bool __state)
        {
            if (__state)
            {
                Safe(() =>
                {
                    LevelPulses.DelayLightning(__instance);
                    LevelPulses.SharePulse(0, __instance);
                });
            }
        }

        [HarmonyPatch(typeof(UT_RandomTimer), "Awake")]
        [HarmonyPostfix]
        private static void TimerAwake(UT_RandomTimer __instance)
        {
            Safe(() =>
            {
                if (LevelPulses.Eligible(__instance))
                {
                    LevelPulses.DelayTimer(__instance);
                }
            });
        }

        [HarmonyPatch(typeof(UT_RandomTimer), "Update")]
        [HarmonyPrefix]
        private static void TimerBefore(UT_RandomTimer __instance, out bool __state)
        {
            __state = false;
            try
            {
                __state = LevelPulses.Live && LevelPulses.AboutToRun(__instance) && LevelPulses.Eligible(__instance);
            }
            catch (Exception)
            {
            }
        }

        [HarmonyPatch(typeof(UT_RandomTimer), "Update")]
        [HarmonyPostfix]
        private static void TimerAfter(UT_RandomTimer __instance, bool __state)
        {
            if (__state)
            {
                Safe(() =>
                {
                    LevelPulses.DelayTimer(__instance);
                    LevelPulses.SharePulse(1, __instance);
                });
            }
        }

        [HarmonyPatch(typeof(DEN_GantryTurret), nameof(DEN_GantryTurret.DroneCheck))]
        [HarmonyPrefix]
        private static bool DroneCheck(DEN_GantryTurret __instance)
        {
            try
            {
                if (!LevelPulses.LaunchesHere(__instance))
                {
                    return false;
                }
                if (LevelPulses.Live && __instance.targetComponent != null && __instance.targetComponent.HasTarget())
                {
                    LevelPulses.ShareDrones(__instance);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Gantry drones not shared: " + e.Message);
            }
            return true;
        }

        private static void Safe(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Level effect not shared: " + e.Message);
            }
        }
    }
}
