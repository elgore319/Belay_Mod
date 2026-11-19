using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Enemies aimed at "the player" only chased the host in co-op. On the host, drones, bloodbugs and the rat
    /// king now go after the nearest living player; each copy still only hurts whoever it reaches. Personal
    /// creatures (the mask, the remains, ticks) react to YOUR view or body, so each PC runs its own instead of
    /// copying the host.
    /// </summary>
    public class EnemyTargets : MonoBehaviour
    {
        private const float RetargetEvery = 0.5f;
        private float nextRetarget;

        private static bool Live => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        /// <summary>Creatures each PC runs for its own player instead of copying the host's.</summary>
        internal static bool Personal(AIGameEntity e) =>
            e is DEN_Mask || e is DEN_Remains_Active || e is DEN_Hopper_Tick;

        private static readonly FieldInfo DroneTarget = AccessTools.Field(typeof(DEN_Drone), "droneTarget");
        private static readonly FieldInfo BugTarget = AccessTools.Field(typeof(DEN_Bloodbug), "bloodbugTarget");

        private void Update()
        {
            if (!Live || !NetSession.Instance.IsHost || Time.unscaledTime < nextRetarget || RemoteBody.All.Count == 0)
            {
                return;
            }
            nextRetarget = Time.unscaledTime + RetargetEvery;
            try
            {
                foreach (DEN_Drone d in FindObjectsByType<DEN_Drone>(FindObjectsSortMode.None))
                {
                    if (d == null || d.dead) continue;
                    if (DroneTarget.GetValue(d) is DEN_Drone.Target t)
                    {
                        Retarget(d, d.alwaysTargetPlayer, ref t.transform, ref t.tagger, ref t.isVisible);
                    }
                }
                foreach (DEN_Bloodbug b in FindObjectsByType<DEN_Bloodbug>(FindObjectsSortMode.None))
                {
                    if (b == null || b.dead) continue;
                    if (BugTarget.GetValue(b) is DEN_Bloodbug.Target t)
                    {
                        Retarget(b, b.alwaysTargetPlayer, ref t.transform, ref t.tagger, ref t.isVisible);
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't point enemies at the team: " + e.Message);
                nextRetarget = Time.unscaledTime + 10f;
            }
        }

        private static bool IsPlayer(Transform t)
        {
            if (t == null) return false;
            ENT_Player me = ENT_Player.playerObject;
            return (me != null && t == me.transform) || t.GetComponent<RemoteBody>() != null;
        }

        /// <summary>Only an enemy that's after a player (or always is) changes target, to the nearest one.</summary>
        private static void Retarget(Component self, bool always, ref Transform target, ref ObjectTagger tagger, ref bool visible)
        {
            if (!always && !IsPlayer(target))
            {
                return;
            }
            Transform pick = TeethSync.Nearest(self.transform.position, IsPlayer(target) ? target : null);
            if (pick == null || pick == target)
            {
                return;
            }
            target = pick;
            ENT_Player me = ENT_Player.playerObject;
            tagger = me != null && pick == me.transform ? me.GetTagger() : pick.GetComponent<ObjectTagger>();
            visible = true;
            Plugin.Log.LogInfo($"{EnemySync.KindOf(self as GameEntity)} is after {TeethSync.Who(pick)}");
        }

        // ---------------------------------------------------------------- Rat king

        private static readonly Dictionary<Component, (Transform t, float until)> picks = new Dictionary<Component, (Transform, float)>();

        /// <summary>Stands in for <c>ENT_Player.GetPlayer().transform</c> in the rat king: the nearest living player on the host.</summary>
        public static Transform TargetOf(Component self)
        {
            ENT_Player me = ENT_Player.GetPlayer();
            Transform mine = me != null ? me.transform : null;
            if (!Live || !NetSession.Instance.IsHost || self == null || RemoteBody.All.Count == 0)
            {
                return mine;
            }
            if (picks.TryGetValue(self, out var known) && known.t != null && Time.unscaledTime < known.until)
            {
                return known.t;
            }
            Transform pick = TeethSync.Nearest(self.transform.position, known.t) ?? mine;
            picks[self] = (pick, Time.unscaledTime + RetargetEvery);
            return pick;
        }

        private static readonly MethodInfo GetPlayer = AccessTools.Method(typeof(ENT_Player), nameof(ENT_Player.GetPlayer));
        private static readonly MethodInfo GetTransform = AccessTools.PropertyGetter(typeof(Component), nameof(Component.transform));
        private static readonly MethodInfo Target = AccessTools.Method(typeof(EnemyTargets), nameof(TargetOf));

        internal static IEnumerable<CodeInstruction> SwapPlayerTransform(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            int swapped = 0;
            for (int i = 0; i < list.Count - 1; i++)
            {
                if (list[i].Calls(GetPlayer) && list[i + 1].Calls(GetTransform))
                {
                    var load = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(list[i]);
                    list[i] = load;
                    list[i + 1] = new CodeInstruction(OpCodes.Call, Target);
                    swapped++;
                }
            }
            if (swapped == 0)
            {
                Plugin.Log.LogWarning("Rat king: no player lookups found to retarget");
            }
            return list;
        }
    }

    [HarmonyPatch(typeof(DEN_RatKing), "Update")]
    internal static class RatKingTargetPatch
    {
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => EnemyTargets.SwapPlayerTransform(instructions);
    }
}
