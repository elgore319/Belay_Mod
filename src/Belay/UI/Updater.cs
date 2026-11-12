using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace Belay.UI
{
    /// <summary>
    /// Keeps Belay current. Most updates are installed by the BelayUpdater preloader patcher before
    /// Belay even loads (so one launch is enough); this class installs that patcher, says when it
    /// updated, and keeps checking GitHub while the game runs. When a newer Belay comes out mid-session
    /// it downloads the release's Belay.dll and swaps it in next to the running one. Windows lets a
    /// loaded DLL be renamed, so the current file becomes Belay.dll.old (deleted on the next launch)
    /// and the new version is used after a restart.
    /// </summary>
    public class Updater : MonoBehaviour
    {
        public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

        public static Updater Instance { get; private set; }

        public UpdateState State { get; private set; } = UpdateState.Idle;
        public string LatestVersion { get; private set; } = "";
        public string Message { get; private set; } = "";

        /// <summary>Set when an update is downloaded; the menu shows a restart popup until dismissed.</summary>
        public bool PopupDismissed { get; set; }

        /// <summary>Minutes between checks while auto update is on.</summary>
        private const float RecheckMinutes = 10f;

        private string downloadUrl;
        private float nextCheck;

        private static readonly Regex DllUrl = new Regex(
            "\"browser_download_url\"\\s*:\\s*\"(https://github\\.com/[^\"]*?/releases/download/([^/\"]+)/Belay\\.dll)\"",
            RegexOptions.IgnoreCase);

        private static string PluginPath => typeof(Plugin).Assembly.Location;

        private void Awake()
        {
            Instance = this;
            CleanUpOldFile();
            InstallPatcher();
        }

        private void Start()
        {
            ReportLaunchUpdate();
            nextCheck = Time.unscaledTime + 5f;
        }

        private void Update()
        {
            if (!Plugin.AutoUpdate.Value || Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + RecheckMinutes * 60f;
            StartCoroutine(Check(autoInstall: true));
        }

        public void CheckNow() => StartCoroutine(Check(autoInstall: false));

        /// <summary>Checks right away and installs whatever is newer (used when the host runs a newer Belay).</summary>
        public void UpdateNow()
        {
            if (State == UpdateState.Available) InstallNow();
            else StartCoroutine(Check(autoInstall: true));
        }

        /// <summary>
        /// Writes the embedded BelayUpdater.dll to BepInEx\patchers\Belay so the next launch installs any
        /// newer release before Belay loads.
        /// </summary>
        private static void InstallPatcher()
        {
            try
            {
                byte[] bytes;
                using (Stream s = typeof(Updater).Assembly.GetManifestResourceStream("Belay.BelayUpdater.dll"))
                {
                    if (s == null) return;
                    bytes = new byte[s.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = s.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                string dir = Path.Combine(BepInEx.Paths.PatcherPluginPath, "Belay");
                string path = Path.Combine(dir, "BelayUpdater.dll");
                if (File.Exists(path) && SameBytes(File.ReadAllBytes(path), bytes)) return;
                Directory.CreateDirectory(dir);
                if (File.Exists(path))
                {
                    // The running game has the old patcher loaded; move it aside (Windows allows that).
                    string old = path + ".old";
                    try { if (File.Exists(old)) File.Delete(old); } catch { }
                    File.Move(path, old);
                }
                File.WriteAllBytes(path, bytes);
                Plugin.Log.LogInfo("Installed the launch updater at " + path);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't install the launch updater: " + e.Message);
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>The patcher leaves a note when it updated Belay this launch.</summary>
        private static void ReportLaunchUpdate()
        {
            try
            {
                string marker = Path.Combine(Path.Combine(BepInEx.Paths.CachePath, "Belay"), "updated.txt");
                if (!File.Exists(marker)) return;
                File.Delete(marker);
                Plugin.Log.LogInfo("Updated to Belay " + Plugin.Version + " at launch");
                Run.RunFlow.Instance?.ShowNotice("Updated to Belay " + Plugin.Version + ".", 8f);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't read the update note: " + e.Message);
            }
        }

        /// <summary>Compares two Belay versions ("0.6.21", "v0.6.21"); 0 when either can't be read.</summary>
        public static int Compare(string a, string b)
        {
            if (!TryParse(a, out var va) || !TryParse(b, out var vb)) return 0;
            return va.CompareTo(vb);
        }

        /// <summary>
        /// Quits and starts the game again through Steam, so the downloaded update loads.
        /// </summary>
        public static void RestartGame()
        {
            try
            {
                uint appId = Steamworks.SteamClient.IsValid ? Steamworks.SteamClient.AppId.Value : 0;
                if (appId != 0)
                {
                    // Steam ignores a launch while the game is still running, so wait for this copy to exit.
                    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                        "/c timeout /t 5 /nobreak >nul & start \"\" \"steam://rungameid/" + appId + "\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    System.Diagnostics.Process.Start(psi);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't schedule the relaunch: " + e.Message);
            }
            Application.Quit();
        }

        public void InstallNow()
        {
            if (State == UpdateState.Available && downloadUrl != null)
            {
                StartCoroutine(Download());
            }
        }

        private static void CleanUpOldFile()
        {
            try
            {
                string old = PluginPath + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't remove the previous Belay.dll.old: " + e.Message);
            }
        }

        private IEnumerator Check(bool autoInstall)
        {
            if (State == UpdateState.Checking || State == UpdateState.Downloading) yield break;
            // Once an update is downloaded, later checks only look for something newer still.
            UpdateState before = State;
            string installed = State == UpdateState.Ready ? LatestVersion : Plugin.Version;
            State = UpdateState.Checking;
            if (before != UpdateState.Ready) Message = "Checking for updates...";

            // The query string defeats any cached copy of an older release list.
            string url = "https://api.github.com/repos/" + Plugin.UpdateRepo.Value.Trim() + "/releases?per_page=20&t=" + DateTime.UtcNow.Ticks;
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("User-Agent", "Belay-Updater");
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.SetRequestHeader("Cache-Control", "no-cache");
                req.timeout = 15;
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    if (before == UpdateState.Ready) { State = UpdateState.Ready; yield break; }
                    Fail(req.responseCode == 404
                        ? "Update check failed: releases not found (is the repo public?)"
                        : "Update check failed: " + req.error);
                    yield break;
                }

                string body = req.downloadHandler.text ?? "";
                Version bestVer = null;
                string bestUrl = null;
                int seen = 0;
                // Every published release's Belay.dll URL has its tag in it:
                //   https://github.com/<owner>/<repo>/releases/download/<tag>/Belay.dll
                // Reading those directly avoids depending on JsonUtility with GitHub's large release
                // objects, and draft releases (untagged URLs) never parse as a version.
                foreach (Match m in DllUrl.Matches(body))
                {
                    seen++;
                    string assetUrl = m.Groups[1].Value.Replace("\\/", "/");
                    if (!TryParse(Uri.UnescapeDataString(m.Groups[2].Value), out var v)) continue;
                    if (bestVer == null || v > bestVer) { bestVer = v; bestUrl = assetUrl; }
                }
                Plugin.Log.LogInfo($"Update check: {seen} release DLLs listed, newest {(bestVer != null ? bestVer.ToString() : "none")}, you have {installed}");
                if (seen == 0 && body.IndexOf("tag_name", StringComparison.Ordinal) < 0)
                {
                    if (before == UpdateState.Ready) { State = UpdateState.Ready; yield break; }
                    Fail("Update check failed: GitHub sent an unexpected reply");
                    yield break;
                }

                TryParse(installed, out var current);
                if (bestVer == null || current == null || bestVer <= current)
                {
                    if (before == UpdateState.Ready) { State = UpdateState.Ready; yield break; }
                    State = UpdateState.UpToDate;
                    Message = "Belay " + Plugin.Version + " is up to date.";
                    yield break;
                }

                LatestVersion = bestVer.ToString();
                downloadUrl = bestUrl;
                State = UpdateState.Available;
                Message = "Belay " + LatestVersion + " is available (you have " + Plugin.Version + ").";
                Plugin.Log.LogInfo(Message);
            }

            if (autoInstall)
            {
                yield return Download();
            }
        }

        private IEnumerator Download()
        {
            State = UpdateState.Downloading;
            Message = "Downloading Belay " + LatestVersion + "...";

            using (var req = UnityWebRequest.Get(downloadUrl))
            {
                req.SetRequestHeader("User-Agent", "Belay-Updater");
                req.timeout = 60;
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Fail("Update download failed: " + req.error);
                    yield break;
                }

                byte[] data = req.downloadHandler.data;
                if (data == null || data.Length < 1024 || data[0] != 'M' || data[1] != 'Z')
                {
                    Fail("Update download failed: the file isn't a valid DLL.");
                    yield break;
                }

                try
                {
                    Install(data);
                }
                catch (Exception e)
                {
                    Fail("Couldn't install the update: " + e.Message);
                    yield break;
                }
            }

            State = UpdateState.Ready;
            PopupDismissed = false;
            Message = "Belay " + LatestVersion + " is installed. Restart the game to use it.";
            Plugin.Log.LogInfo(Message);
            // On the main menu the restart popup says this; in a run a notice is enough.
            if (CL_GameManager.IsInGame()) Run.RunFlow.Instance?.ShowNotice(Message, 10f);
        }

        private static void Install(byte[] data)
        {
            string path = PluginPath;
            string fresh = path + ".new";
            string old = path + ".old";

            File.WriteAllBytes(fresh, data);
            if (File.Exists(old))
            {
                try
                {
                    File.Delete(old);
                }
                catch
                {
                    // .old is the DLL this game is running (an update was already installed this
                    // session), so Belay.dll isn't loaded and can simply be replaced.
                    File.Copy(fresh, path, true);
                    File.Delete(fresh);
                    return;
                }
            }
            File.Move(path, old);
            try
            {
                File.Move(fresh, path);
            }
            catch
            {
                // Put the running version back so the mod still loads next time.
                File.Move(old, path);
                throw;
            }
        }

        private void Fail(string message)
        {
            State = UpdateState.Failed;
            Message = message;
            Plugin.Log.LogWarning(message);
        }

        private static bool TryParse(string tag, out Version v)
        {
            v = null;
            if (string.IsNullOrEmpty(tag)) return false;
            string s = tag.Trim().TrimStart('v', 'V');
            int dash = s.IndexOfAny(new[] { '-', '+' });
            if (dash >= 0) s = s.Substring(0, dash);
            if (!Version.TryParse(s, out var parsed)) return false;
            v = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return true;
        }
    }
}
