using System;
using System.Collections;
using System.IO;
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

#pragma warning disable 0649 // filled in by JsonUtility
        [Serializable] private class Asset { public string name; public string browser_download_url; }
        [Serializable] private class Release { public string tag_name; public bool draft; public Asset[] assets; }
        [Serializable] private class ReleaseList { public Release[] items; }
#pragma warning restore 0649

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

            string url = "https://api.github.com/repos/" + Plugin.UpdateRepo.Value.Trim() + "/releases?per_page=10";
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("User-Agent", "Belay-Updater");
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.timeout = 15;
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Fail(req.responseCode == 404
                        ? "Update check failed: releases not found (is the repo public?)"
                        : "Update check failed: " + req.error);
                    yield break;
                }

                Release best = null;
                Version bestVer = null;
                try
                {
                    var list = JsonUtility.FromJson<ReleaseList>("{\"items\":" + req.downloadHandler.text + "}");
                    foreach (var r in list.items ?? new Release[0])
                    {
                        if (r == null || r.draft || !TryParse(r.tag_name, out var v)) continue;
                        if (FindDll(r) == null) continue;
                        if (bestVer == null || v > bestVer) { best = r; bestVer = v; }
                    }
                }
                catch (Exception e)
                {
                    Fail("Update check failed: couldn't read the release list (" + e.Message + ")");
                    yield break;
                }

                TryParse(Plugin.Version, out var current);
                if (best == null || current == null || bestVer <= current)
                {
                    State = UpdateState.UpToDate;
                    Message = "Belay " + Plugin.Version + " is up to date.";
                    yield break;
                }

                LatestVersion = bestVer.ToString();
                downloadUrl = FindDll(best).browser_download_url;
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

        private static Asset FindDll(Release r)
        {
            if (r.assets == null) return null;
            foreach (var a in r.assets)
            {
                if (a != null && string.Equals(a.name, "Belay.dll", StringComparison.OrdinalIgnoreCase)) return a;
            }
            return null;
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
