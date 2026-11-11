using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Interludes and breakrooms wait for the whole team: their buttons and levers can't be used until
    /// every living teammate is inside the room. The death goo already follows the lowest living
    /// player, so nobody gets left behind by someone racing ahead and pressing on.
    /// </summary>
    internal static class InterludeGate
    {
        /// <summary>How far outside a level's bounds still counts as "in the room" (meters).</summary>
        private const float BoundsSlack = 2f;

        private static float lastNoticeTime = -10f;

        /// <summary>For the HUD: teammates still missing from the room you're in, or 0.</summary>
        public static int MissingInCurrentRoom
        {
            get
            {
                if (!InLobbyRun || WorldLoader.instance == null || WorldLoader.instance.GetCurrentLevel() == null)
                {
                    return 0;
                }
                M_Level level = WorldLoader.instance.GetCurrentLevel().GetLevel();
                return IsGatedRoom(level) ? Missing(level).Count : 0;
            }
        }

        private static bool InLobbyRun => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        public static bool IsGatedRoom(M_Level level)
        {
            return level != null && (level.safeArea || level.name.ToLowerInvariant().Contains("interlude"));
        }

        private static bool Inside(M_Level level, Vector3 worldPos)
        {
            Vector3 local = level.transform.worldToLocalMatrix.MultiplyPoint(worldPos);
            Bounds b = level.GetLocalBounds();
            b.Expand(BoundsSlack * 2f);
            return b.Contains(local);
        }

        /// <summary>Living teammates (not counting you) who aren't inside this level yet.</summary>
        internal static List<string> Missing(M_Level level)
        {
            var missing = new List<string>();
            foreach (RemotePlayer remote in PlayerSync.Instance.Remotes.Values)
            {
                if (remote == null || remote.Latest.Dead || Time.unscaledTime - remote.LastReceived > 3f)
                {
                    continue;
                }
                if (!Inside(level, remote.LocalPosition))
                {
                    missing.Add(remote.DisplayName);
                }
            }
            return missing;
        }

        /// <summary>True when this control is in an interlude that's still waiting for teammates.</summary>
        public static bool Blocked(Component control, bool notify)
        {
            if (!InLobbyRun || control == null || ButtonSync.Applying)
            {
                return false;
            }
            M_Level level = control.GetComponentInParent<M_Level>();
            if (!IsGatedRoom(level))
            {
                return false;
            }
            List<string> missing = Missing(level);
            if (missing.Count == 0)
            {
                return false;
            }
            if (notify && Time.unscaledTime - lastNoticeTime > 2f)
            {
                lastNoticeTime = Time.unscaledTime;
                Run.RunFlow.Instance?.ShowNotice("Wait for your team: " + string.Join(", ", missing) + (missing.Count == 1 ? " hasn't" : " haven't") + " reached the room yet");
            }
            return true;
        }
    }

    [HarmonyPatch]
    internal static class InterludeGatePatches
    {
        // Hide the interact prompt while the room is waiting.
        [HarmonyPatch(typeof(CL_Button), nameof(CL_Button.CanInteract), new[] { typeof(Interaction) })]
        [HarmonyPostfix]
        private static void ButtonCanInteract(CL_Button __instance, ref bool __result)
        {
            if (__result && InterludeGate.Blocked(__instance, notify: false)) __result = false;
        }

        [HarmonyPatch(typeof(CL_Button), nameof(CL_Button.CanInteract), new System.Type[0])]
        [HarmonyPostfix]
        private static void ButtonCanInteractNoArgs(CL_Button __instance, ref bool __result)
        {
            if (__result && InterludeGate.Blocked(__instance, notify: false)) __result = false;
        }

        [HarmonyPatch(typeof(CL_HoldButton), nameof(CL_HoldButton.CanInteract), new[] { typeof(Interaction) })]
        [HarmonyPostfix]
        private static void HoldCanInteract(CL_HoldButton __instance, ref bool __result)
        {
            if (__result && InterludeGate.Blocked(__instance, notify: false)) __result = false;
        }

        // And refuse the press itself, in case something calls Interact directly.
        [HarmonyPatch(typeof(CL_Button), nameof(CL_Button.Interact), new System.Type[0])]
        [HarmonyPrefix]
        private static bool ButtonInteract(CL_Button __instance) => !InterludeGate.Blocked(__instance, notify: true);

        [HarmonyPatch(typeof(CL_Button), nameof(CL_Button.Interact), new[] { typeof(Clickable.InteractionInfo) })]
        [HarmonyPrefix]
        private static bool ButtonInteractInfo(CL_Button __instance) => !InterludeGate.Blocked(__instance, notify: true);

        [HarmonyPatch(typeof(CL_HoldButton), nameof(CL_HoldButton.Interact), new System.Type[0])]
        [HarmonyPrefix]
        private static bool HoldInteract(CL_HoldButton __instance) => !InterludeGate.Blocked(__instance, notify: true);

        [HarmonyPatch(typeof(CL_HoldButton), nameof(CL_HoldButton.Interact), new[] { typeof(Clickable.InteractionInfo) })]
        [HarmonyPrefix]
        private static bool HoldInteractInfo(CL_HoldButton __instance) => !InterludeGate.Blocked(__instance, notify: true);
    }
}
