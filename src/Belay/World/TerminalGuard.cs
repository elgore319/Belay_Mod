using Belay.Net;
using HarmonyLib;

namespace Belay.World
{
    /// <summary>
    /// Only the player who walks up to a terminal sits at it. When a teammate's press, trigger or
    /// room sequence is replayed here, anything it reaches that would sit our player down at a
    /// terminal (and pull them to its screen) is skipped, however deep in the event chain it is.
    /// </summary>
    internal static class TerminalGuard
    {
        internal static bool ReplayingTeammate =>
            NetSession.Instance != null && NetSession.Instance.InLobby
            && (ButtonSync.Applying || ZoneSync.Applying || DoorSync.Applying);

        internal static bool Allow(string what)
        {
            if (!ReplayingTeammate)
            {
                return true;
            }
            Plugin.Log.LogInfo("Kept a teammate's action from sitting you at a terminal (" + what + ")");
            return false;
        }
    }

    [HarmonyPatch(typeof(OS_Computer_Interface), nameof(OS_Computer_Interface.ActivateComputer))]
    internal static class TerminalGuardComputerPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(OS_Computer_Interface __instance) =>
            TerminalGuard.Allow(__instance != null ? __instance.name : "?");
    }

    [HarmonyPatch(typeof(OS_Manager), nameof(OS_Manager.Activate))]
    internal static class TerminalGuardManagerPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix() => TerminalGuard.Allow("screen");
    }
}
