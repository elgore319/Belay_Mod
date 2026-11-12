using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Belay.Run
{
    /// <summary>
    /// Keeps the whole lobby in the same run. The host's gamemode choice (with its settings) and a fresh
    /// seed go into lobby data; clients load into that run on their own, including people who join
    /// mid-climb, and then get carried up to the host.
    /// </summary>
    public class RunFlow : MonoBehaviour
    {
        /// <summary>How far below the host (meters) a freshly spawned player has to be to get carried up.</summary>
        private const float CatchUpDistance = 60f;
        /// <summary>Catch-up only happens this soon after spawning, so it can't be used as a teleport later.</summary>
        private const float CatchUpWindow = 45f;
        private const float CatchUpStepInterval = 1.5f;

        public static RunFlow Instance { get; private set; }

        /// <summary>Run id this client is playing (0 = none).</summary>
        public int LocalRunId { get; private set; }

        /// <summary>Short message for the HUD, e.g. why a button did nothing.</summary>
        public string Notice { get; private set; }

        private float noticeUntil;
        private int pendingRunId;
        private bool wasInGame;
        private float spawnTime;
        private bool catchingUp;
        private float nextCatchUpStep;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.OnLobbyChanged += CheckForNewRun;
            HostProfile.Register(NetSession.Instance);
        }

        public void ShowNotice(string text, float seconds = 4f)
        {
            Notice = text;
            noticeUntil = Time.unscaledTime + seconds;
        }

        private void Update()
        {
            if (Notice != null && Time.unscaledTime > noticeUntil)
            {
                Notice = null;
            }

            NetSession net = NetSession.Instance;
            HostProfile.Tick();
            if (!net.InLobby)
            {
                LocalRunId = 0;
                pendingRunId = 0;
                catchingUp = false;
                return;
            }

            // Follow once the host is actually in the run (sending states), so a stale run left in
            // lobby data after the host went back to the menu doesn't pull anyone in alone.
            if (pendingRunId != 0 && !net.IsHost && HostIsPlaying(net))
            {
                FollowRun(net.CurrentRun);
                pendingRunId = 0;
            }

            bool inGame = WorldSpace.Ready;
            if (inGame && !wasInGame)
            {
                spawnTime = Time.unscaledTime;
                catchingUp = !net.IsHost;
            }
            wasInGame = inGame;

            if (inGame && catchingUp)
            {
                CatchUpToHost();
            }
        }

        private static bool HostIsPlaying(NetSession net)
        {
            return PlayerSync.Instance.Remotes.TryGetValue(net.HostId, out RemotePlayer host)
                && host != null && Time.unscaledTime - host.LastReceived < 2f;
        }

        // ---------------------------------------------------------------- Host side

        /// <summary>Called when the host picks a gamemode from the menu. Publishes the run before the scene loads.</summary>
        public void HostStartingRun(M_Gamemode mode)
        {
            var settings = new List<string>();
            if (mode.gamemodeSettings != null)
            {
                foreach (GamemodeSetting setting in mode.gamemodeSettings)
                {
                    if (setting != null && mode.HasActiveSetting(setting.id))
                    {
                        settings.Add(setting.id);
                    }
                }
            }
            string profile = HostProfile.CaptureTeam(mode);
            RunInfo run = NetSession.Instance.StartRun(mode.gamemodeName, settings);
            NetSession.Instance.SetLobbyData(HostProfile.LobbyKey, profile);
            HostProfile.Load(profile, run.RunId);
            LocalRunId = run.RunId;
            Plugin.Log.LogInfo($"Starting run {run.RunId}: {run.Gamemode} [{string.Join(",", run.Settings)}] seed {run.Seed}");
        }

        // ---------------------------------------------------------------- Client side

        private void CheckForNewRun()
        {
            NetSession net = NetSession.Instance;
            if (!net.InLobby || net.IsHost)
            {
                return;
            }
            RunInfo run = net.CurrentRun;
            if (run.IsValid && run.RunId != LocalRunId)
            {
                // Defer to Update: this fires inside a Steam callback.
                pendingRunId = run.RunId;
            }
        }

        private void FollowRun(RunInfo run)
        {
            if (!run.IsValid)
            {
                return;
            }
            M_Gamemode mode = CL_AssetManager.GetGamemodeAsset(run.Gamemode);
            if (mode == null)
            {
                ShowNotice("Can't follow the host: gamemode '" + run.Gamemode + "' not found", 8f);
                Plugin.Log.LogWarning("Gamemode not found: " + run.Gamemode);
                LocalRunId = run.RunId;
                return;
            }

            ApplySettings(mode, run.Settings);
            CL_GameManager.gamemode = mode;
            if (CL_GameManager.gMan != null)
            {
                Traverse.Create(CL_GameManager.gMan).Field("baseGamemode").SetValue(mode);
            }
            CL_SaveManager.loadSessionSaveOnLoad = false;
            CL_SaveManager.isSavedRun = false;
            HostProfile.Load(NetSession.Instance.GetLobbyData(HostProfile.LobbyKey), run.RunId);
            LocalRunId = run.RunId;
            Plugin.Log.LogInfo($"Following host into run {run.RunId}: {run.Gamemode} seed {run.Seed}");
            SceneManager.LoadScene(mode.gamemodeScene);
        }

        /// <summary>Match the host's gamemode options (hard mode etc.), which the game keeps in the save file.</summary>
        private static void ApplySettings(M_Gamemode mode, List<string> active)
        {
            if (mode.gamemodeSettings == null || StatManager.saveData == null)
            {
                return;
            }
            StatManager.saveData.SetupGamemode(mode.gamemodeName);
            foreach (GamemodeSetting setting in mode.gamemodeSettings)
            {
                if (setting != null)
                {
                    StatManager.saveData.SetSetting(mode.gamemodeName, setting.id, active.Contains(setting.id));
                }
            }
        }

        /// <summary>
        /// A player who spawns far below the host is moved up level by level (so the tower gets generated
        /// on the way) and then placed next to the host.
        /// </summary>
        private void CatchUpToHost()
        {
            if (Time.unscaledTime - spawnTime > CatchUpWindow)
            {
                catchingUp = false;
                return;
            }
            if (Time.unscaledTime < nextCatchUpStep || !WorldLoader.isLoaded || WorldLoader.instance == null)
            {
                return;
            }
            NetSession net = NetSession.Instance;
            if (!PlayerSync.Instance.Remotes.TryGetValue(net.HostId, out RemotePlayer host) || host == null
                || Time.unscaledTime - host.LastReceived > 2f || host.Latest.Dead)
            {
                return;
            }

            ENT_Player player = ENT_Player.playerObject;
            Vector3 hostPos = host.LocalPosition;
            float below = hostPos.y - player.transform.position.y;
            nextCatchUpStep = Time.unscaledTime + CatchUpStepInterval;
            player.AddIgnoreFallDamageTime(CatchUpStepInterval + 1f);

            if (below > CatchUpDistance)
            {
                // Walking the tower one level at a time makes WorldLoader generate everything in between.
                WorldLoader.instance.TeleportPlayerToNextLevel(new string[0]);
                return;
            }
            if (below > 8f || Vector3.Distance(hostPos, player.transform.position) > CatchUpDistance)
            {
                player.Teleport(hostPos + Vector3.up * 0.5f);
                player.ResetFallingHeight();
                ShowNotice("Caught up to " + host.DisplayName);
            }
            catchingUp = false;
        }
    }

    [HarmonyPatch]
    internal static class RunFlowPatches
    {
        private static bool InLobby => NetSession.Instance != null && NetSession.Instance.InLobby;
        private static bool IsHost => InLobby && NetSession.Instance.IsHost;

        /// <summary>Host: publish the run. Client: only the host starts runs.</summary>
        [HarmonyPatch(typeof(UI_GamemodeScreen), nameof(UI_GamemodeScreen.LoadGamemode))]
        [HarmonyPrefix]
        private static bool LoadGamemode(UI_GamemodeScreen __instance)
        {
            if (!InLobby)
            {
                return true;
            }
            if (!IsHost)
            {
                RunFlow.Instance.ShowNotice("Only the host can start a run. You'll join automatically.");
                return false;
            }
            CL_SaveManager.loadSessionSaveOnLoad = false;
            var mode = Traverse.Create(__instance).Field("baseGamemode").GetValue<M_Gamemode>();
            if (mode != null)
            {
                RunFlow.Instance.HostStartingRun(mode);
            }
            return true;
        }

        /// <summary>Saved runs are single-player state; in a lobby "Continue" starts a fresh shared run.</summary>
        [HarmonyPatch(typeof(UI_GamemodeScreen), nameof(UI_GamemodeScreen.LoadGamemodeSave))]
        [HarmonyPrefix]
        private static bool LoadGamemodeSave(UI_GamemodeScreen __instance)
        {
            if (!InLobby)
            {
                return true;
            }
            if (IsHost)
            {
                RunFlow.Instance.ShowNotice("Saves can't be continued in a lobby. Starting a new run.");
                __instance.LoadGamemode();
            }
            else
            {
                RunFlow.Instance.ShowNotice("Only the host can start a run. You'll join automatically.");
            }
            return false;
        }

        /// <summary>Host restarting gives the whole lobby a new tower.</summary>
        [HarmonyPatch(typeof(CL_GameManager), nameof(CL_GameManager.RestartScene))]
        [HarmonyPrefix]
        private static void RestartScene()
        {
            if (IsHost && CL_GameManager.gamemode != null)
            {
                RunFlow.Instance.HostStartingRun(CL_GameManager.GetBaseGamemode() ?? CL_GameManager.gamemode);
            }
        }
    }
}
