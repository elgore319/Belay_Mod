using Belay.Net;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// In co-op the pause menu doesn't stop the game: it only takes your hands off the controls while the world
    /// and teammates carry on.
    /// </summary>
    public class CoopPause : MonoBehaviour
    {
        public static CoopPause Instance { get; private set; }

        /// <summary>Our pause menu is open but the game keeps running.</summary>
        public static bool SoftPaused { get; private set; }

        /// <summary>The pause menu is up, whether or not the game is actually stopped.</summary>
        public static bool InPauseMenu => CL_GameManager.gMan != null && (CL_GameManager.gMan.isPaused || SoftPaused);

        internal static bool InCoop => NetSession.Instance != null && NetSession.Instance.InLobby && CL_GameManager.IsInGame();

        private static int unpausedFrame = -1;
        private static bool lockedCamera;
        /// <summary>Whether input was already locked (a terminal, a cutscene, Teeth) when the menu opened.</summary>
        private static bool inputWasLocked;
        private static readonly AccessTools.FieldRef<ENT_Player, bool> CamLocked = AccessTools.FieldRefAccess<ENT_Player, bool>("camLocked");

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            CL_GameManager gMan = CL_GameManager.gMan;
            if (SoftPaused && (gMan == null || gMan.pauseMenu == null || !gMan.pauseMenu.activeInHierarchy || !CL_GameManager.IsInGame()))
            {
                EndSoft();
            }
        }

        /// <summary>Right after the game opened its pause menu: keep the world running.</summary>
        internal static void Soften(CL_GameManager gMan)
        {
            gMan.isPaused = false;
            CL_GameManager.SetTimescale(1f);
            AudioListener.pause = false;
            if (!SoftPaused)
            {
                inputWasLocked = gMan.lockPlayerInput;
            }
            gMan.lockPlayerInput = true;
            SoftPaused = true;
            ENT_Player player = ENT_Player.playerObject;
            // Only lock (and later unlock) the camera if nothing else already had it locked: closing the
            // menu used to free the camera and controls while sat at a terminal or held by a cutscene.
            if (player != null && !lockedCamera && !CamLocked(player))
            {
                player.LockCamera();
                lockedCamera = true;
            }
        }

        internal static void EndSoft()
        {
            if (!SoftPaused)
            {
                return;
            }
            SoftPaused = false;
            CL_GameManager gMan = CL_GameManager.gMan;
            if (gMan != null)
            {
                gMan.lockPlayerInput = inputWasLocked || CommandConsole.IsConsoleVisible();
            }
            if (lockedCamera && ENT_Player.playerObject != null)
            {
                ENT_Player.playerObject.UnlockCamera();
            }
            lockedCamera = false;
            inputWasLocked = false;
        }

        internal static bool PausedThisFrame => unpausedFrame == Time.frameCount;

        internal static void MarkUnpaused()
        {
            unpausedFrame = Time.frameCount;
        }
    }

    [HarmonyPatch]
    internal static class CoopPausePatches
    {
        [HarmonyPatch(typeof(CL_GameManager), nameof(CL_GameManager.Pause))]
        [HarmonyPrefix]
        private static bool PausePrefix()
        {
            // Closing the menu with the pause key: the game's own "pause pressed while not paused" check
            // sees the same key press in the same frame and would open it again.
            return !(CoopPause.SoftPaused || CoopPause.InCoop && CoopPause.PausedThisFrame);
        }

        [HarmonyPatch(typeof(CL_GameManager), nameof(CL_GameManager.Pause))]
        [HarmonyPostfix]
        private static void PausePostfix(CL_GameManager __instance)
        {
            if (CoopPause.InCoop && __instance.isPaused)
            {
                CoopPause.Soften(__instance);
            }
        }

        [HarmonyPatch(typeof(CL_GameManager), nameof(CL_GameManager.UnPause))]
        [HarmonyPostfix]
        private static void UnPausePostfix()
        {
            if (CoopPause.SoftPaused)
            {
                CoopPause.MarkUnpaused();
            }
            CoopPause.EndSoft();
        }
    }
}
