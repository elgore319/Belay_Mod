using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace BelayUpdater
{
    /// <summary>
    /// BepInEx preloader patcher. It runs before any plugin is loaded, so it can replace Belay.dll on
    /// disk with the newest GitHub release and the game starts on that version straight away. It
    /// patches no game code. When anything goes wrong it does nothing and the in-game updater
    /// (Belay.UI.Updater) still works as before.
    /// </summary>
    public static class BelayUpdaterPatcher
    {
        // BepInEx calls Patch for each DLL listed here; Belay patches none.
        public static IEnumerable<string> TargetDLLs => new string[0];

        public static void Patch(AssemblyDefinition assembly) { }

        private const string DefaultRepo = "BrassWhuckle/Belay";
        private const string ConfigName = "brasswhuckle.belay.cfg";
        private const string TestClientFlag = "-belay-testclient";

        private static readonly Regex DllUrl = new Regex(
            "\"browser_download_url\"\\s*:\\s*\"(https://github\\.com/[^\"]*?/releases/download/([^/\"]+)/Belay\\.dll)\"",
            RegexOptions.IgnoreCase);

        private static ManualLogSource log;

        public static void Initialize()
        {
            log = Logger.CreateLogSource("Belay Updater");
            try
            {
                Run();
            }
            catch (Exception e)
            {
                log.LogWarning("Launch update skipped: " + e.Message);
            }
        }

        private static void Run()
        {
            if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, TestClientFlag, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            string[] found = Directory.Exists(Paths.PluginPath)
                ? Directory.GetFiles(Paths.PluginPath, "Belay.dll", SearchOption.AllDirectories)
                : new string[0];
            if (found.Length != 1)
            {
                log.LogInfo(found.Length == 0 ? "Belay isn't installed; nothing to update." : "Several Belay.dll files found; leaving them alone.");
                return;
            }
            string plugin = found[0];
            TryDelete(plugin + ".old");
            TryDelete(plugin + ".new");

            ReadConfig(out bool autoUpdate, out string repo);
            if (!autoUpdate)
            {
                log.LogInfo("Auto update is off.");
                return;
            }

            Version current = Normalize(AssemblyName.GetAssemblyName(plugin).Version);
            string url = "https://api.github.com/repos/" + repo + "/releases?per_page=20&t=" + DateTime.UtcNow.Ticks;
            string body = Encoding.UTF8.GetString(Fetch(url, 8));

            Version best = null;
            string bestUrl = null;
            foreach (Match m in DllUrl.Matches(body))
            {
                if (!TryParse(Uri.UnescapeDataString(m.Groups[2].Value), out Version v)) continue;
                if (best == null || v > best) { best = v; bestUrl = m.Groups[1].Value.Replace("\\/", "/"); }
            }
            log.LogInfo($"Newest release {(best != null ? best.ToString() : "none")}, installed {current}");
            if (best == null || best <= current)
            {
                return;
            }

            byte[] data = Fetch(bestUrl, 45);
            if (data.Length < 1024 || data[0] != 'M' || data[1] != 'Z')
            {
                throw new Exception("the downloaded file isn't a DLL");
            }

            // Nothing has loaded Belay.dll yet, so it can be replaced in place.
            string fresh = plugin + ".new";
            File.WriteAllBytes(fresh, data);
            File.Copy(fresh, plugin, true);
            TryDelete(fresh);
            log.LogInfo($"Updated Belay {current} -> {best}");

            // Belay reads this once and tells the player which version they're on now.
            string marker = Path.Combine(Path.Combine(Paths.CachePath, "Belay"), "updated.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(marker));
            File.WriteAllText(marker, current + " " + best);
        }

        private static void ReadConfig(out bool autoUpdate, out string repo)
        {
            autoUpdate = true;
            repo = DefaultRepo;
            string cfg = Path.Combine(Paths.ConfigPath, ConfigName);
            if (!File.Exists(cfg))
            {
                // Settings saved by versions before 0.6.24 (another plugin GUID); Belay migrates them on load.
                cfg = Directory.Exists(Paths.ConfigPath) ? Directory.GetFiles(Paths.ConfigPath, "*.belay.cfg").FirstOrDefault() : null;
                if (cfg == null) return;
            }
            foreach (string raw in File.ReadAllLines(cfg))
            {
                int eq = raw.IndexOf('=');
                if (raw.TrimStart().StartsWith("#") || eq < 0) continue;
                string key = raw.Substring(0, eq).Trim();
                string value = raw.Substring(eq + 1).Trim();
                if (key == "AutoUpdate") autoUpdate = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                else if (key == "UpdateRepo" && value.Length > 0 && !value.EndsWith("/Belay_Mod", StringComparison.OrdinalIgnoreCase)) repo = value;
            }
        }

        /// <summary>Mono's own HTTPS first; Windows' curl.exe if Mono's TLS isn't ready this early.</summary>
        private static byte[] Fetch(string url, int timeoutSeconds)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "Belay-Updater";
                req.Accept = "application/vnd.github+json";
                req.Headers["Cache-Control"] = "no-cache";
                req.Timeout = timeoutSeconds * 1000;
                req.ReadWriteTimeout = timeoutSeconds * 1000;
                using (var resp = req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var mem = new MemoryStream())
                {
                    stream.CopyTo(mem);
                    return mem.ToArray();
                }
            }
            catch (Exception e)
            {
                log.LogInfo("Built-in download failed (" + e.Message + "), trying curl");
                return Curl(url, timeoutSeconds);
            }
        }

        private static byte[] Curl(string url, int timeoutSeconds)
        {
            string curl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
            if (!File.Exists(curl)) throw new Exception("curl.exe not found");
            string tmp = Path.Combine(Path.GetTempPath(), "belay-update-" + Guid.NewGuid().ToString("N"));
            var psi = new ProcessStartInfo(curl,
                $"-sSfL --max-time {timeoutSeconds} -H \"User-Agent: Belay-Updater\" -H \"Accept: application/vnd.github+json\" -o \"{tmp}\" \"{url}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit((timeoutSeconds + 2) * 1000))
                    {
                        try { p.Kill(); } catch { }
                        throw new Exception("curl timed out");
                    }
                    if (p.ExitCode != 0) throw new Exception("curl exit code " + p.ExitCode);
                }
                return File.ReadAllBytes(tmp);
            }
            finally
            {
                TryDelete(tmp);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

        private static bool TryParse(string tag, out Version v)
        {
            v = null;
            if (string.IsNullOrEmpty(tag)) return false;
            string s = tag.Trim().TrimStart('v', 'V');
            int dash = s.IndexOfAny(new[] { '-', '+' });
            if (dash >= 0) s = s.Substring(0, dash);
            if (!Version.TryParse(s, out Version parsed)) return false;
            v = Normalize(parsed);
            return true;
        }
    }
}
