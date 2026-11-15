using Belay.Net;
using Belay.UI;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// In a co-op run the pause menu no longer stops the game. Opening it only takes your hands off the
    /// controls (the way the game's console does) while the world, the mass and your teammates carry on,
    /// so one player pausing can't freeze things for the team. The pause menu has a "Pause for everyone"
    /// button instead: that one stops the game on every PC (a real pause, menu open for all) until anyone
    /// presses "Resume for everyone".
    /// </summary>
    public class CoopPause : MonoBehaviour
    {
        public static CoopPause Instance { get; private set; }

        /// <summary>Our pause menu is open but the game keeps running.</summary>
        public static bool SoftPaused { get; private set; }

        /// <summary>Someone paused the game for the whole team.</summary>
        public static bool TeamPaused { get; private set; }

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

        private void Start()
        {
            NetSession.Instance.On(PacketType.TeamPause, (origin, r) =>
            {
                bool on = r.ReadBoolean();
                if (origin != NetSession.LocalId)
                {
                    Plugin.Log.LogInfo((on ? "Paused for everyone by " : "Resumed for everyone by ") + NetSession.NameOf(origin));
                    Apply(on);
                }
            });
        }

        private void Update()
        {
            CL_GameManager gMan = CL_GameManager.gMan;
            if (SoftPaused && (gMan == null || gMan.pauseMenu == null || !gMan.pauseMenu.activeInHierarchy || !CL_GameManager.IsInGame()))
            {
                EndSoft();
            }
            if (TeamPaused && (NetSession.Instance == null || !NetSession.Instance.InLobby || !CL_GameManager.IsInGame()))
            {
                TeamPaused = false;
            }
        }

        /// <summary>The button in the pause menu.</summary>
        public static void SetTeamPause(bool on)
        {
            if (!InCoop)
            {
                return;
            }
            Plugin.Log.LogInfo(on ? "Paused for everyone" : "Resumed for everyone");
            Apply(on);
            NetSession.Instance.Send(PacketType.TeamPause, w => w.Write(on));
        }

        private static void Apply(bool on)
        {
            CL_GameManager gMan = CL_GameManager.gMan;
            TeamPaused = on && InCoop;
            if (gMan == null || !CL_GameManager.IsInGame())
            {
                return;
            }
            if (TeamPaused)
            {
                if (SoftPaused)
                {
                    EndSoft();
                    HardPause(gMan);
                }
                else if (!gMan.isPaused)
                {
                    gMan.Pause();
                }
            }
            else if (gMan.isPaused || SoftPaused)
            {
                gMan.UnPause();
            }
        }

        private static void HardPause(CL_GameManager gMan)
        {
            gMan.isPaused = true;
            CL_GameManager.SetTimescale(0f);
            AudioListener.pause = true;
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

        private void OnGUI()
        {
            if (!InCoop || !InPauseMenu)
            {
                return;
            }
            GUISkin previous = GUI.skin;
            if (BelayTheme.Skin != null)
            {
                GUI.skin = BelayTheme.Skin;
            }
            try
            {
                var rect = new Rect(Screen.width / 2f - 150f, Screen.height - 70f, 300f, 44f);
                if (GUI.Button(rect, TeamPaused ? "RESUME FOR EVERYONE" : "PAUSE FOR EVERYONE"))
                {
                    SetTeamPause(!TeamPaused);
                }
            }
            finally
            {
                GUI.skin = previous;
            }
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
            if (CoopPause.InCoop && !CoopPause.TeamPaused && __instance.isPaused)
            {
                CoopPause.Soften(__instance);
            }
        }

        // While the team is paused, closing your own menu would start your game again alone.
        [HarmonyPatch(typeof(CL_GameManager), nameof(CL_GameManager.UnPause))]
        [HarmonyPrefix]
        private static bool UnPausePrefix()
        {
            return !CoopPause.TeamPaused;
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
