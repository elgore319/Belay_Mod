using Belay.Net;
using HarmonyLib;

namespace Belay.Run
{
    /// <summary>
    /// When anyone reaches the ending, the run ends with a win for the whole team. The winner's game
    /// tells everyone else, and every other PC (alive, dead or spectating) runs the game's own win: the
    /// end-of-run fade, the win score screen and its stats. Before, only the player who got there saw the
    /// ending; a dead teammate was left spectating a body still standing in the game.
    /// </summary>
    internal static class WinSync
    {
        /// <summary>Set while Belay runs a teammate's win here, so it isn't sent back.</summary>
        internal static bool Applying;
        private static bool listening;

        internal static void Listen()
        {
            if (listening || NetSession.Instance == null)
            {
                return;
            }
            listening = true;
            NetSession.Instance.On(PacketType.RunWon, (origin, r) => ApplyTeammateWin(NetSession.NameOf(origin)));
        }

        private static void ApplyTeammateWin(string who)
        {
            CL_GameManager gMan = CL_GameManager.gMan;
            if (gMan == null || !CL_GameManager.IsInGame() || CL_GameManager.runHasEnded)
            {
                return;
            }
            Plugin.Log.LogInfo($"{who} reached the ending: the run is won for everyone");
            Revive.ReviveSystem.Instance?.OnRunWon();
            Applying = true;
            try
            {
                gMan.Win();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't end the run with the team's win: " + e.Message);
            }
            finally
            {
                Applying = false;
            }
        }
    }

    [HarmonyPatch(typeof(CL_GameManager), nameof(CL_GameManager.Win))]
    internal static class WinPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            if (WinSync.Applying || CL_GameManager.runHasEnded || NetSession.Instance == null || !NetSession.Instance.InLobby)
            {
                return;
            }
            Plugin.Log.LogInfo("Reached the ending: telling the team the run is won");
            NetSession.Instance.Send(PacketType.RunWon, null);
            Revive.ReviveSystem.Instance?.OnRunWon();
        }
    }
}
