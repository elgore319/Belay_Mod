using System.Collections.Generic;
using System.Text.RegularExpressions;
using Belay.Net;
using DarkMachine.AI;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// The gantry drone in the middle of the stress test areas (DEN_GantryTurret). In co-op it goes after the
    /// highest living player, so it looks like it's keeping the team from climbing further. Stress test three is
    /// the exception: there it stays on the lowest living player, because the team has to jump on it to get
    /// higher. Host only (the host runs it and the others copy it). Gantries outside the stress tests are left as
    /// they were.
    /// </summary>
    internal static class GantryTargets
    {
        private enum Mode { Normal, Highest, Lowest }

        /// <summary>Only switch to someone else when they're this much higher (or lower), so it doesn't flip back and forth.</summary>
        private const float SwitchMargin = 2f;

        private static readonly Regex StressTest = new Regex("stress ?test", RegexOptions.IgnoreCase);
        private static readonly Regex StressTestThree = new Regex(@"stress ?test ?(0*3|three|iii)(\D|$)", RegexOptions.IgnoreCase);

        private static readonly Dictionary<DEN_GantryTurret, Mode> modes = new Dictionary<DEN_GantryTurret, Mode>();
        private static readonly Dictionary<DEN_GantryTurret, Transform> lastPick = new Dictionary<DEN_GantryTurret, Transform>();

        private static Mode ModeOf(DEN_GantryTurret g)
        {
            if (modes.TryGetValue(g, out Mode mode))
            {
                return mode;
            }
            var names = new List<string>();
            M_Level level = g.GetComponentInParent<M_Level>(true);
            if (level == null && WorldLoader.instance != null)
            {
                level = WorldLoader.GetClosestLevelToPosition(g.transform.position)?.GetLevel();
            }
            if (level != null)
            {
                names.Add(level.levelName);
                names.Add(level.name);
                if (level.subRegion != null) names.Add(level.subRegion.name);
                if (level.region != null) names.Add(level.region.name);
            }
            mode = Mode.Normal;
            foreach (string raw in names)
            {
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }
                string n = raw.Replace('_', ' ').Replace('-', ' ');
                if (StressTestThree.IsMatch(n))
                {
                    mode = Mode.Lowest;
                    break;
                }
                if (StressTest.IsMatch(n))
                {
                    mode = Mode.Highest;
                }
            }
            modes[g] = mode;
            Plugin.Log.LogInfo($"Gantry drone in [{string.Join(" / ", names)}]: {(mode == Mode.Lowest ? "follows the lowest player" : mode == Mode.Highest ? "follows the highest player" : "unchanged")}");
            return mode;
        }

        /// <summary>Before the gantry's Update on the host: while it's after a player, make that the highest (or lowest) one.</summary>
        internal static void Retarget(DEN_GantryTurret g)
        {
            if (!NetSession.InLobbyRun || !NetSession.Instance.IsHost || g == null || g.dead)
            {
                return;
            }
            AITargetComponent component = g.targetComponent;
            if (component == null || !component.HasTarget())
            {
                return; // only once it's awake and hunting; this never wakes it
            }
            Mode mode = ModeOf(g);
            if (mode == Mode.Normal)
            {
                return;
            }
            ENT_Player me = ENT_Player.playerObject;
            Transform current = component.target.targetTransform;
            bool currentIsPlayer = current != null && ((me != null && (current == me.transform || current.IsChildOf(me.transform)))
                || current.GetComponent<RemoteBody>() != null);
            if (current != null && !currentIsPlayer)
            {
                return; // after something else entirely: leave it
            }
            Transform best = null;
            float bestScore = float.MinValue;
            void Consider(Transform t)
            {
                float score = mode == Mode.Highest ? t.position.y : -t.position.y;
                if (score > bestScore) { bestScore = score; best = t; }
            }
            if (me != null && !me.dead)
            {
                Consider(me.transform);
            }
            foreach (RemoteBody body in RemoteBody.All)
            {
                if (body != null && body.Owner != null && !body.Owner.Latest.Dead && body.Collider != null && body.Collider.enabled
                    && Time.unscaledTime - body.Owner.LastReceived <= 3f)
                {
                    Consider(body.transform);
                }
            }
            // Stick with the one picked last unless someone else is clearly higher (or lower).
            if (lastPick.TryGetValue(g, out Transform kept) && kept != null && kept != best)
            {
                float keptScore = mode == Mode.Highest ? kept.position.y : -kept.position.y;
                bool keptAlive = kept == me?.transform ? !me.dead : kept.GetComponent<RemoteBody>() is RemoteBody b && b.Owner != null && !b.Owner.Latest.Dead;
                if (keptAlive && bestScore - keptScore < SwitchMargin)
                {
                    best = kept;
                }
            }
            if (best == null || best == current)
            {
                return;
            }
            component.target.targetTransform = best;
            component.target.targetTagger = best.GetComponent<ObjectTagger>();
            component.target.targetEntity = me != null && best == me.transform ? me : null;
            if (!lastPick.TryGetValue(g, out Transform last) || last != best)
            {
                lastPick[g] = best;
                Plugin.Log.LogInfo($"Gantry drone is after the {(mode == Mode.Highest ? "highest" : "lowest")} player: {TeethSync.Who(best)}");
            }
        }
    }

    [HarmonyPatch(typeof(DEN_GantryTurret), nameof(DEN_GantryTurret.Update))]
    internal static class GantryTargetPatch
    {
        [HarmonyPrefix]
        private static void Prefix(DEN_GantryTurret __instance)
        {
            try
            {
                GantryTargets.Retarget(__instance);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't point the gantry drone at the team: " + e.Message);
            }
        }
    }
}
