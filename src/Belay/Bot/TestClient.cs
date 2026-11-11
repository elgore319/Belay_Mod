using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.Bot
{
    /// <summary>
    /// Runs in the second copy of the game (started with -belay-testclient). That copy is a real client:
    /// it builds its own world from the host's seed and run, so anything it shows differently from your main
    /// window is a desync. By default it flies along your path a couple of seconds behind you (autopilot);
    /// press the autopilot key in that window to play it yourself instead.
    ///
    /// It shares your Steam account and save folder, so it never writes saves, stats, settings,
    /// leaderboards or achievements, and it is muted.
    /// </summary>
    public class TestClient : MonoBehaviour
    {
        private const float TrailStep = 0.05f;
        private const float TrailSeconds = 120f;
        private const float KeepDistance = 1.6f;

        private struct Sample
        {
            public float Time;
            public Vector3 Position;
        }

        public static TestClient Instance { get; private set; }

        public bool Autopilot { get; private set; } = true;

        private readonly List<Sample> trail = new List<Sample>();
        private float trailTimer;
        private float playhead = -1f;
        private bool flying;
        private Traverse camY;
        private ENT_Player camYOwner;

        public static void Install(GameObject host, Harmony harmony)
        {
            host.AddComponent<TestClient>();
            BlockWrites(harmony);
        }

        private void Awake()
        {
            Instance = this;
            Application.runInBackground = true;
            AudioListener.volume = 0f;
            // Unity saves window size and other prefs when it quits normally, which would hand your main
            // game this window's small windowed resolution. Ending the process skips that.
            Application.quitting += () => Process.GetCurrentProcess().Kill();
        }

        // ---------------------------------------------------------------- Autopilot

        private void Update()
        {
            AudioListener.volume = 0f;
            // Keeps the game from recording stats or scores for this copy.
            CommandConsole.hasCheated = true;

            if (Input.GetKeyDown(Plugin.TestClientAutopilotKey.Value))
            {
                Autopilot = !Autopilot;
                if (!Autopilot)
                {
                    StopFlying();
                }
                Run.RunFlow.Instance?.ShowNotice("Test client autopilot " + (Autopilot ? "on" : "off"));
            }

            NetSession net = NetSession.Instance;
            if (!net.InLobby || !WorldSpace.Ready)
            {
                trail.Clear();
                playhead = -1f;
                StopFlying();
                return;
            }
            if (!PlayerSync.Instance.Remotes.TryGetValue(net.HostId, out RemotePlayer host) || host == null
                || Time.unscaledTime - host.LastReceived > 2f)
            {
                StopFlying();
                return;
            }

            Record(host);
            ENT_Player player = ENT_Player.playerObject;
            if (!Autopilot || player.dead || trail.Count == 0)
            {
                StopFlying();
                return;
            }
            Fly(player, host);
        }

        private void Record(RemotePlayer host)
        {
            trailTimer -= Time.deltaTime;
            if (trailTimer > 0f || host.Latest.Dead)
            {
                return;
            }
            trailTimer = TrailStep;
            trail.Add(new Sample { Time = Time.time, Position = host.Latest.Position });
            int drop = 0;
            while (drop < trail.Count && Time.time - trail[drop].Time > TrailSeconds)
            {
                drop++;
            }
            if (drop > 0)
            {
                trail.RemoveRange(0, drop);
            }
        }

        private void Fly(ENT_Player player, RemotePlayer host)
        {
            float delay = Mathf.Max(0.25f, Plugin.BotFollowDelay.Value);
            float oldest = trail[0].Time;
            float newest = trail[trail.Count - 1].Time;
            Vector3 hostShared = host.Latest.Position;
            Vector3 me = WorldSpace.ToShared(player.transform.position);
            if (playhead < oldest)
            {
                playhead = Mathf.Max(oldest, newest - delay);
            }
            float lag = Time.time - playhead;
            bool tooClose = Vector3.Distance(me, hostShared) < KeepDistance;
            if (!tooClose && lag > delay * 0.5f)
            {
                float speed = lag > delay * 1.5f ? 1.5f : 1f;
                playhead = Mathf.Min(newest, playhead + Time.deltaTime * speed);
            }
            Vector3 target = WorldSpace.ToLocal(SampleAt(playhead));

            // Face the host.
            Vector3 eye = target + Vector3.up * BotPov.EyeHeight;
            Vector3 toHost = host.transform.position - eye;
            Quaternion look = toHost.sqrMagnitude > 0.01f ? Quaternion.LookRotation(toHost) : player.transform.rotation;
            float yaw = look.eulerAngles.y;
            float pitch = Mathf.DeltaAngle(0f, look.eulerAngles.x);

            // Flying turns off gravity; teleporting every frame keeps the real player (and its colliders, so
            // triggers still fire) on the path.
            player.fly = true;
            flying = true;
            player.Teleport(target, Quaternion.Euler(0f, yaw, 0f));
            player.ResetFallingHeight();
            player.AddIgnoreFallDamageTime(1f);
            SetPitch(player, pitch);
            player.ResetCameraInterpolation();
        }

        private void SetPitch(ENT_Player player, float pitch)
        {
            if (camYOwner != player)
            {
                camYOwner = player;
                camY = Traverse.Create(player).Field("camY");
            }
            if (camY != null && camY.FieldExists())
            {
                camY.SetValue(pitch);
            }
            if (player.camTransform != null)
            {
                Vector3 e = player.camTransform.localEulerAngles;
                player.camTransform.localRotation = Quaternion.Euler(pitch, 0f, e.z);
            }
        }

        private void StopFlying()
        {
            if (flying && ENT_Player.playerObject != null)
            {
                ENT_Player.playerObject.fly = false;
            }
            flying = false;
        }

        private Vector3 SampleAt(float time)
        {
            int hi = trail.Count - 1;
            if (time >= trail[hi].Time)
            {
                return trail[hi].Position;
            }
            int lo = 0;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (trail[mid].Time <= time)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            Sample a = trail[lo];
            Sample b = trail[hi];
            if ((b.Position - a.Position).sqrMagnitude > 9f)
            {
                return time - a.Time < b.Time - time ? a.Position : b.Position;
            }
            float t = b.Time > a.Time ? Mathf.Clamp01((time - a.Time) / (b.Time - a.Time)) : 0f;
            return Vector3.Lerp(a.Position, b.Position, t);
        }

        private void OnGUI()
        {
            NetSession net = NetSession.Instance;
            string text = "BELAY TEST CLIENT   " + net.Status + "   Autopilot: " + (Autopilot ? "ON" : "OFF")
                + " [" + Plugin.TestClientAutopilotKey.Value + "]";
            var style = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            GUI.color = new Color(1f, 0.85f, 0.25f);
            GUI.Label(new Rect(10f, 8f, Screen.width - 20f, 26f), text, style);
            GUI.color = Color.white;
        }

        // ---------------------------------------------------------------- Never write to the shared save

        private static void BlockWrites(Harmony harmony)
        {
            var skip = new HarmonyMethod(typeof(TestClient).GetMethod(nameof(Skip), BindingFlags.NonPublic | BindingFlags.Static));
            Block(harmony, skip, typeof(StatManager), "SaveStats");
            Block(harmony, skip, typeof(StatManager), "SaveJsonDiffed");
            Block(harmony, skip, typeof(SettingsManager), "SaveSettings");
            Block(harmony, skip, typeof(CL_SaveManager), "SaveSessionToFile");
            Block(harmony, skip, typeof(CL_GameTracker), "SerializeData");
            Block(harmony, skip, typeof(SteamManager), "GiveSteamAchievement");
            Block(harmony, skip, AccessTools.Inner(typeof(CL_Leaderboard), "OfflineLeaderboard"), "UpdateOfflineLeaderboardJson");

            // Online leaderboard: answer "nothing changed" without uploading.
            Type api = AccessTools.Inner(typeof(SteamManager), "SteamLeaderboardApi");
            MethodInfo place = api != null ? AccessTools.Method(api, "PlaceScore") : null;
            if (place != null)
            {
                harmony.Patch(place, prefix: new HarmonyMethod(typeof(TestClient).GetMethod(nameof(SkipPlaceScore), BindingFlags.NonPublic | BindingFlags.Static)));
            }
            else
            {
                Plugin.Log.LogWarning("Test client: couldn't find the leaderboard upload to block");
            }
        }

        private static void Block(Harmony harmony, HarmonyMethod skip, Type type, string method)
        {
            if (type == null)
            {
                Plugin.Log.LogWarning("Test client: missing type for " + method);
                return;
            }
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
            {
                if (m.Name != method)
                {
                    continue;
                }
                if (m.IsGenericMethodDefinition)
                {
                    // Generic methods can't be patched in general; their callers above are blocked instead.
                    continue;
                }
                harmony.Patch(m, prefix: skip);
            }
        }

        private static bool Skip() => false;

        private static bool SkipPlaceScore(ref System.Threading.Tasks.Task<(bool, System.Threading.Tasks.Task)> __result)
        {
            __result = System.Threading.Tasks.Task.FromResult((false, (System.Threading.Tasks.Task)System.Threading.Tasks.Task.CompletedTask));
            return false;
        }
    }
}
