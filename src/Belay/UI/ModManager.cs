using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;

namespace Belay.UI
{
    /// <summary>
    /// Turns other BepInEx mods on and off. BepInEx can't unload a plugin (or undo its Harmony patches)
    /// while the game runs, so a switch renames the mod's DLL to/from ".dll.disabled" and takes effect on
    /// the next launch. Nothing else about other mods is touched.
    /// </summary>
    public static class ModManager
    {
        public const string DisabledSuffix = ".disabled";

        public class ModEntry
        {
            /// <summary>Current path on disk (ends in .dll or .dll.disabled).</summary>
            public string Path;
            public string FileName;
            public string DisplayName;
            public string Version;
            public List<string> Guids = new List<string>();
            /// <summary>Loaded in this session.</summary>
            public bool LoadedNow;
            /// <summary>Will load next launch.</summary>
            public bool EnabledOnDisk;
            public bool Protected;
            public string Error;
        }

        private static List<ModEntry> cached;

        public static bool RestartRequired => cached != null && cached.Any(m => m.LoadedNow != m.EnabledOnDisk);

        public static List<ModEntry> Mods
        {
            get
            {
                if (cached == null)
                {
                    Refresh();
                }
                return cached;
            }
        }

        public static void Refresh()
        {
            var byFile = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            string ownPath = System.IO.Path.GetFullPath(typeof(Plugin).Assembly.Location);

            foreach (PluginInfo info in Chainloader.PluginInfos.Values)
            {
                if (string.IsNullOrEmpty(info.Location))
                {
                    continue;
                }
                string path = System.IO.Path.GetFullPath(info.Location);
                if (!byFile.TryGetValue(path, out ModEntry entry))
                {
                    entry = new ModEntry
                    {
                        Path = path,
                        FileName = System.IO.Path.GetFileName(path),
                        DisplayName = info.Metadata.Name,
                        Version = info.Metadata.Version.ToString(),
                        LoadedNow = true,
                        EnabledOnDisk = File.Exists(path),
                        Protected = string.Equals(path, ownPath, StringComparison.OrdinalIgnoreCase),
                    };
                    byFile[path] = entry;
                }
                else
                {
                    entry.DisplayName += ", " + info.Metadata.Name;
                }
                entry.Guids.Add(info.Metadata.GUID);
                if (!entry.EnabledOnDisk && File.Exists(path + DisabledSuffix))
                {
                    entry.Path = path + DisabledSuffix;
                }
            }

            // Mods switched off earlier (not loaded this session).
            try
            {
                foreach (string file in Directory.GetFiles(Paths.PluginPath, "*.dll" + DisabledSuffix, SearchOption.AllDirectories))
                {
                    string enabledPath = file.Substring(0, file.Length - DisabledSuffix.Length);
                    if (byFile.ContainsKey(System.IO.Path.GetFullPath(enabledPath)))
                    {
                        continue;
                    }
                    byFile[file] = new ModEntry
                    {
                        Path = file,
                        FileName = System.IO.Path.GetFileName(enabledPath),
                        DisplayName = System.IO.Path.GetFileNameWithoutExtension(enabledPath),
                        Version = "",
                        LoadedNow = false,
                        EnabledOnDisk = false,
                    };
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't scan plugins folder: " + e.Message);
            }

            cached = byFile.Values.OrderBy(m => m.Protected ? 0 : 1).ThenBy(m => m.DisplayName).ToList();
        }

        /// <summary>Switch a mod on or off for the next launch.</summary>
        public static void SetEnabled(ModEntry mod, bool enabled)
        {
            if (mod.Protected || mod.EnabledOnDisk == enabled)
            {
                return;
            }
            string enabledPath = mod.Path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                ? mod.Path.Substring(0, mod.Path.Length - DisabledSuffix.Length)
                : mod.Path;
            string disabledPath = enabledPath + DisabledSuffix;
            try
            {
                if (enabled)
                {
                    File.Move(disabledPath, enabledPath);
                    mod.Path = enabledPath;
                }
                else
                {
                    File.Move(enabledPath, disabledPath);
                    mod.Path = disabledPath;
                }
                mod.EnabledOnDisk = enabled;
                mod.Error = null;
                Plugin.Log.LogInfo($"{(enabled ? "Enabled" : "Disabled")} {mod.FileName} (applies next launch)");
            }
            catch (Exception e)
            {
                mod.Error = "Couldn't rename the file (" + e.Message + "). Close the game and rename it by hand.";
                Plugin.Log.LogWarning(mod.Error);
            }
        }

        /// <summary>GUIDs of every loaded plugin, for spotting players running different mods.</summary>
        public static string LoadedModList()
        {
            return string.Join(",", Chainloader.PluginInfos.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }
    }
}
