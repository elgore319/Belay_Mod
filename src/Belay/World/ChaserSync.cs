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
    /// The Hunter and chase-killers only targeted the host's player. On the host they now chase the nearest living
    /// player. Their kill used to fire on "my target is within 2 m", which would have killed the host from afar,
    /// so it now only lands on this PC's own player. Each PC's copy still does its own killing, and a chase-killer
    /// no longer vanishes when the host dies.
    /// </summary>
    public class ChaserSync : MonoBehaviour
    {
        private const float RetargetEvery = 0.5f;
        private float nextRetarget;

        private static bool Live => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        private void Update()
        {
            if (!Live || !NetSession.Instance.IsHost || Time.unscaledTime < nextRetarget)
            {
                return;
            }
            nextRetarget = Time.unscaledTime + RetargetEvery;
            try
            {
                if (DEN_Hunter.activeHunters != null)
                {
                    foreach (DEN_Hunter hunter in DEN_Hunter.activeHunters)
                    {
                        if (hunter == null || hunter.dead) continue;
                        Transform pick = TeethSync.Nearest(hunter.transform.position, hunter.target);
                        if (pick != null && pick != hunter.target)
                        {
                            hunter.target = pick;
                            hunter.WipeTrackPositions();
                            Plugin.Log.LogInfo("The Hunter is after " + TeethSync.Who(pick));
                        }
                    }
                }
                foreach (DEN_SimpleChaseKiller chaser in FindObjectsByType<DEN_SimpleChaseKiller>(FindObjectsSortMode.None))
                {
                    // The Tracker has its own behaviour built around the local player's handholds: leave it be.
                    if (chaser == null || chaser.dead || chaser.GetType() != typeof(DEN_SimpleChaseKiller)) continue;
                    Transform pick = TeethSync.Nearest(chaser.transform.position, chaser.target);
                    if (pick != null && pick != chaser.target)
                    {
                        chaser.target = pick;
                        Traverse.Create(chaser).Field("playerTrackPositions").GetValue<Queue<Vector3>>()?.Clear();
                        Plugin.Log.LogInfo($"{EnemySync.KindOf(chaser)} is after {TeethSync.Who(pick)}");
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't point a chaser at the team: " + e.Message);
                nextRetarget = Time.unscaledTime + 10f;
            }
        }

        // ---------------------------------------------------------------- Called from the patched game code

        /// <summary>Stands in for <c>ENT_Player.playerObject.Kill(type, info)</c> in the chasers' movement.</summary>
        public static void KillIfMine(GameEntity player, string type, Denizen self)
        {
            if (ShouldKill(player, self)) player.Kill(type);
        }

        public static void KillIfMineInfo(GameEntity player, string type, Damageable.DamageInfo info, Denizen self)
        {
            if (ShouldKill(player, self)) player.Kill(type, info);
        }

        private static bool ShouldKill(GameEntity player, Denizen self)
        {
            if (player == null)
            {
                return false;
            }
            if (Live && self != null && ENT_Player.playerObject != null)
            {
                Transform mine = ENT_Player.playerObject.transform;
                bool targetIsMine = self.target == null || self.target == mine || self.target.IsChildOf(mine);
                if (!targetIsMine && Vector3.Distance(self.transform.position, mine.position) >= 2f)
                {
                    return false; // it reached a teammate: their own PC does the kill
                }
            }
            return true;
        }

        /// <summary>Stands in for <c>ENT_Player.playerObject</c> where the Hunter lays its trail: it follows its target.</summary>
        public static Component TrailOf(Denizen self)
        {
            if (Live && self != null && self.target != null)
            {
                return self.target;
            }
            return ENT_Player.playerObject;
        }

        /// <summary>Stands in for the chase-killer's "the player is dead, go away": only once the whole team is down.</summary>
        public static bool DeadAndTeamDown(ENT_Player player)
        {
            if (player == null || !player.IsDead())
            {
                return false;
            }
            if (!Live)
            {
                return true;
            }
            foreach (Players.RemotePlayer remote in Players.PlayerSync.Instance.Remotes.Values)
            {
                if (remote != null && !remote.Latest.Dead && remote.Present)
                {
                    return false;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------- Transpiler helpers

        private static readonly FieldInfo PlayerObject = AccessTools.Field(typeof(ENT_Player), nameof(ENT_Player.playerObject));
        private static readonly MethodInfo KillMine = AccessTools.Method(typeof(ChaserSync), nameof(KillIfMine));
        private static readonly MethodInfo KillMineInfo = AccessTools.Method(typeof(ChaserSync), nameof(KillIfMineInfo));

        /// <summary>Swap the kill of the local player that follows a load of ENT_Player.playerObject for KillIfMine(..., this).</summary>
        internal static IEnumerable<CodeInstruction> SwapKill(IEnumerable<CodeInstruction> instructions, string where)
        {
            var list = new List<CodeInstruction>(instructions);
            int swapped = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if ((list[i].opcode == OpCodes.Callvirt || list[i].opcode == OpCodes.Call) && list[i].operand is MethodInfo m
                    && m.Name == "Kill" && !m.IsStatic && m.GetParameters().Length >= 1 && m.GetParameters().Length <= 2
                    && m.GetParameters()[0].ParameterType == typeof(string)
                    && (m.GetParameters().Length == 1 || m.GetParameters()[1].ParameterType == typeof(Damageable.DamageInfo))
                    && LoadsPlayerBefore(list, i))
                {
                    var self = new CodeInstruction(OpCodes.Ldarg_0);
                    list[i].MoveLabelsTo(self);
                    list.Insert(i, self);
                    i++;
                    list[i] = new CodeInstruction(OpCodes.Call, m.GetParameters().Length == 1 ? KillMine : KillMineInfo);
                    swapped++;
                }
            }
            if (swapped == 0)
            {
                Plugin.Log.LogWarning("Couldn't find the player kill in " + where + "; it still kills the host only");
            }
            return list;
        }

        private static bool LoadsPlayerBefore(List<CodeInstruction> list, int callIndex)
        {
            for (int j = callIndex - 1; j >= 0 && j >= callIndex - 6; j--)
            {
                if (list[j].opcode == OpCodes.Ldsfld && Equals(list[j].operand, PlayerObject))
                {
                    return true;
                }
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(DEN_Hunter), "Movement")]
    internal static class HunterKillPatch
    {
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ChaserSync.SwapKill(instructions, "DEN_Hunter.Movement");
    }

    [HarmonyPatch(typeof(DEN_Hunter), "TrackPlayerPosition")]
    internal static class HunterTrailPatch
    {
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo playerObject = AccessTools.Field(typeof(ENT_Player), nameof(ENT_Player.playerObject));
            MethodInfo trail = AccessTools.Method(typeof(ChaserSync), nameof(ChaserSync.TrailOf));
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.opcode == OpCodes.Ldsfld && Equals(ins.operand, playerObject))
                {
                    var self = new CodeInstruction(OpCodes.Ldarg_0);
                    ins.MoveLabelsTo(self);
                    yield return self;
                    yield return new CodeInstruction(OpCodes.Call, trail);
                    continue;
                }
                yield return ins;
            }
        }
    }

    [HarmonyPatch(typeof(DEN_SimpleChaseKiller), "Movement")]
    internal static class ChaseKillerKillPatch
    {
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ChaserSync.SwapKill(instructions, "DEN_SimpleChaseKiller.Movement");
    }

    [HarmonyPatch(typeof(DEN_SimpleChaseKiller), nameof(DEN_SimpleChaseKiller.Update))]
    internal static class ChaseKillerStayPatch
    {
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo stay = AccessTools.Method(typeof(ChaserSync), nameof(ChaserSync.DeadAndTeamDown));
            foreach (CodeInstruction ins in instructions)
            {
                if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call) && ins.operand is MethodInfo m && m.Name == "IsDead" && m.GetParameters().Length == 0)
                {
                    yield return new CodeInstruction(OpCodes.Call, stay).MoveLabelsFrom(ins);
                    continue;
                }
                yield return ins;
            }
        }
    }
}
