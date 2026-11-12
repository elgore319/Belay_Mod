using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace Belay.UI
{
    /// <summary>
    /// Checks the repo's GitHub releases on launch. When a newer Belay is out, it downloads the
    /// release's Belay.dll and swaps it in next to the running one. Windows lets a loaded DLL be
    /// renamed, so the current file becomes Belay.dll.old (deleted on the next launch) and the
    /// new version is used after a restart.
    /// </summary>
    public class Updater : MonoBehaviour
    {
        public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

        public static Updater Instance { get; private set; }

        public UpdateState State { get; private set; } = UpdateState.Idle;
        public string LatestVersion { get; private set; } = "";
        public string Message { get; private set; } = "";

        private string downloadUrl;

        private static readonly Regex DllUrl = new Regex(
            "\"browser_download_url\"\\s*:\\s*\"(https://github\\.com/[^\"]*?/releases/download/([^/\"]+)/Belay\\.dll)\"",
            RegexOptions.IgnoreCase);

        private static string PluginPath => typeof(Plugin).Assembly.Location;

        private void Awake()
        {
            Instance = this;
            CleanUpOldFile();
        }

        private void Start()
        {
            if (Plugin.AutoUpdate.Value)
            {
                StartCoroutine(Check(autoInstall: true));
            }
        }

        public void CheckNow() => StartCoroutine(Check(autoInstall: false));

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
            if (State == UpdateState.Checking || State == UpdateState.Downloading || State == UpdateState.Ready) yield break;
            State = UpdateState.Checking;
            Message = "Checking for updates...";

            // Try the configured repo first. Until the public releases repo has its first release,
            // fall back to the old source repo, so updates keep working through the move.
            var repos = new System.Collections.Generic.List<string> { Plugin.UpdateRepo.Value.Trim() };
            if (!repos.Exists(r => string.Equals(r, Plugin.LegacyUpdateRepo, StringComparison.OrdinalIgnoreCase)))
            {
                repos.Add(Plugin.LegacyUpdateRepo);
            }

            string body = null;
            string emptyBody = null;
            string error = null;
            foreach (string repo in repos)
            {
                // The query string defeats any cached copy of an older release list.
                string url = "https://api.github.com/repos/" + repo + "/releases?per_page=20&t=" + DateTime.UtcNow.Ticks;
                using (var req = UnityWebRequest.Get(url))
                {
                    req.SetRequestHeader("User-Agent", "Belay-Updater");
                    req.SetRequestHeader("Accept", "application/vnd.github+json");
                    req.SetRequestHeader("Cache-Control", "no-cache");
                    req.timeout = 15;
                    yield return req.SendWebRequest();

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        if (error == null)
                        {
                            error = req.responseCode == 404
                                ? "Update check failed: releases not found (is the repo public?)"
                                : "Update check failed: " + req.error;
                        }
                        Plugin.Log.LogInfo("Update check: " + repo + " gave " + (req.responseCode == 404 ? "404" : req.error));
                        continue;
                    }

                    string text = req.downloadHandler.text ?? "";
                    // An existing repo with no releases yet answers "[]": keep looking.
                    if (!DllUrl.IsMatch(text) && repo != repos[repos.Count - 1])
                    {
                        emptyBody = emptyBody ?? text;
                        continue;
                    }
                    body = text;
                    break;
                }
            }

            body = body ?? emptyBody;
            if (body == null)
            {
                Fail(error ?? "Update check failed");
                yield break;
            }

            {
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
                Plugin.Log.LogInfo($"Update check: {seen} release DLLs listed, newest {(bestVer != null ? bestVer.ToString() : "none")}, you have {Plugin.Version}");
                if (seen == 0 && body.IndexOf("tag_name", StringComparison.Ordinal) < 0 && body.Trim() != "[]")
                {
                    Fail("Update check failed: GitHub sent an unexpected reply");
                    yield break;
                }

                TryParse(Plugin.Version, out var current);
                if (bestVer == null || current == null || bestVer <= current)
                {
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
            Message = "Belay " + LatestVersion + " is installed. Restart the game to use it.";
            Plugin.Log.LogInfo(Message);
            Run.RunFlow.Instance?.ShowNotice(Message, 10f);
        }

        private static void Install(byte[] data)
        {
            string path = PluginPath;
            string fresh = path + ".new";
            string old = path + ".old";

            File.WriteAllBytes(fresh, data);
            if (File.Exists(old)) File.Delete(old);
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
            return Version.TryParse(s, out v);
        }
    }
}
