using System;
using System.Collections.Generic;
using System.Reflection;
using Belay.Net;
using HarmonyLib;

namespace Belay.World
{
    /// <summary>
    /// The Core's eraser terminal lights a few random buttons at each step. Presses are shared, but each PC drew
    /// its own random buttons, so teammates saw different ones lit. The draw now comes from the lobby seed and
    /// the step number, so every PC lights the same buttons.
    /// </summary>
    [HarmonyPatch(typeof(Sequence_Core_Eraser))]
    internal static class EraserSync
    {
        private static readonly AccessTools.FieldRef<Sequence_Core_Eraser, int> CurrentProgress =
            AccessTools.FieldRefAccess<Sequence_Core_Eraser, int>("currentProgress");

        private static System.Random rng;

        [HarmonyPatch(typeof(Sequence_Core_Eraser), nameof(Sequence_Core_Eraser.Progress))]
        [HarmonyPrefix]
        private static void ProgressPrefix(Sequence_Core_Eraser __instance)
        {
            rng = NetSession.InLobbyRun
                ? new System.Random(WorldEvents.SeedFor("eraser:" + ButtonSync.ObjectId(__instance) + ":" + (CurrentProgress(__instance) + 1)))
                : null;
        }

        internal static int Pick(int min, int max) => rng != null ? rng.Next(min, max) : UnityEngine.Random.Range(min, max);
    }

    /// <summary>Swaps the eraser animation's button draws for the shared one.</summary>
    [HarmonyPatch]
    internal static class EraserPickPatch
    {

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var found = new List<MethodBase>();
            void Scan(Type t)
            {
                foreach (Type inner in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (inner.Name.Contains("g__Animation"))
                    {
                        MethodInfo move = AccessTools.Method(inner, "MoveNext");
                        if (move != null) found.Add(move);
                    }
                    Scan(inner);
                }
            }
            Scan(typeof(Sequence_Core_Eraser));
            return found;
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo range = AccessTools.Method(typeof(UnityEngine.Random), nameof(UnityEngine.Random.Range), new[] { typeof(int), typeof(int) });
            MethodInfo pick = AccessTools.Method(typeof(EraserSync), nameof(EraserSync.Pick));
            int swapped = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.Calls(range))
                {
                    ins.operand = pick;
                    swapped++;
                }
                yield return ins;
            }
            Plugin.Log.LogInfo($"Eraser buttons synced ({swapped} pick(s))");
        }
    }
}
