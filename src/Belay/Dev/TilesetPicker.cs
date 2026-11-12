using System.Collections.Generic;
using System.Linq;
using Belay.Net;
using HarmonyLib;
using UnityEngine;

namespace Belay.Dev
{
    /// <summary>
    /// Dev window "Tilesets" page: choose which subregions (the game's tilesets, like the different
    /// versions of each area) the generator may pick. Unticked ones are left out of every level generated
    /// from then on. In a lobby the host's choice is shared through lobby data and applies to everyone,
    /// so all players keep generating the same levels.
    /// </summary>
    internal static class TilesetPicker
    {
        public const string LobbyKey = "belay_dev_tiles_off";

        private static readonly HashSet<string> localOff = new HashSet<string>();
        private static Vector2 scroll;
        private static float nextPublish;

        /// <summary>Set while M_Region.GetLevels runs for a region the filter would empty: the filter is skipped.</summary>
        internal static bool Bypass;

        /// <summary>The names that are off right now: the host's in a lobby, your own otherwise.</summary>
        internal static HashSet<string> Off
        {
            get
            {
                NetSession net = NetSession.Instance;
                if (net != null && net.InLobby && !net.IsHost)
                {
                    return Parse(net.GetLobbyData(LobbyKey));
                }
                return localOff;
            }
        }

        private static string cachedRaw;
        private static HashSet<string> cachedSet = new HashSet<string>();

        private static HashSet<string> Parse(string raw)
        {
            raw = raw ?? "";
            if (raw != cachedRaw)
            {
                cachedRaw = raw;
                cachedSet = new HashSet<string>(raw.Split('|').Where(s => s.Length > 0));
            }
            return cachedSet;
        }

        internal static bool Allowed(M_Subregion sub)
        {
            return Bypass || sub == null || !Off.Contains(sub.subregionName);
        }

        /// <summary>The host keeps the lobby's copy of its choice current.</summary>
        internal static void Tick()
        {
            NetSession net = NetSession.Instance;
            if (net == null || !net.IsHost || Time.unscaledTime < nextPublish)
            {
                return;
            }
            nextPublish = Time.unscaledTime + 1f;
            string raw = string.Join("|", localOff.OrderBy(s => s));
            if ((net.GetLobbyData(LobbyKey) ?? "") != raw)
            {
                net.SetLobbyData(LobbyKey, raw);
            }
        }

        private static List<M_Region> Regions()
        {
            var found = new List<M_Region>();
            M_Gamemode mode = CL_GameManager.gamemode;
            if (mode != null && mode.regions != null)
            {
                found.AddRange(mode.regions.Where(r => r != null));
            }
            foreach (M_Region r in Resources.FindObjectsOfTypeAll<M_Region>())
            {
                if (r != null && !found.Contains(r))
                {
                    found.Add(r);
                }
            }
            return found.Where(r => r.subregionGroups != null && r.subregionGroups.Any(g => g?.subregions != null && g.subregions.Count > 0)).ToList();
        }

        public static void Draw()
        {
            NetSession net = NetSession.Instance;
            bool client = net != null && net.InLobby && !net.IsHost;
            GUILayout.Label("Untick a tileset to leave it out of the run. It applies to levels that haven't been generated yet, "
                + "so set it before starting a run (or before reaching that area).");
            if (client)
            {
                GUILayout.Label("You're not the host, so the host's choice is shown and used.");
            }
            else if (net != null && net.InLobby)
            {
                GUILayout.Label("You're the host: everyone in the lobby uses this choice.");
            }

            HashSet<string> off = Off;
            GUILayout.BeginHorizontal();
            GUI.enabled = !client;
            if (GUILayout.Button("Tick all"))
            {
                localOff.Clear();
            }
            GUI.enabled = true;
            GUILayout.Label($"{off.Count} off");
            GUILayout.EndHorizontal();

            List<M_Region> regions = Regions();
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(330));
            if (regions.Count == 0)
            {
                GUILayout.Label("No tilesets loaded yet. Open this again on the main menu after the game has finished loading, or during a run.");
            }
            foreach (M_Region region in regions)
            {
                GUILayout.Space(4);
                GUILayout.Label((string.IsNullOrEmpty(region.regionName) ? region.name : region.regionName).ToUpperInvariant());
                foreach (M_Region.SubregionGroup group in region.subregionGroups)
                {
                    if (group?.subregions == null)
                    {
                        continue;
                    }
                    foreach (M_Subregion sub in group.subregions)
                    {
                        if (sub == null)
                        {
                            continue;
                        }
                        string name = sub.subregionName;
                        bool on = !off.Contains(name);
                        GUI.enabled = !client;
                        string label = "  " + name + (group.subregions.Count > 1 ? $"   ({group.name}: 1 of {group.subregions.Count})" : "");
                        bool want = GUILayout.Toggle(on, label);
                        GUI.enabled = true;
                        if (want != on && !client)
                        {
                            if (want)
                            {
                                localOff.Remove(name);
                            }
                            else
                            {
                                localOff.Add(name);
                            }
                            nextPublish = 0f;
                            Plugin.Log.LogInfo($"[Dev] Tileset {name} {(want ? "on" : "off")}");
                        }
                    }
                }
            }
            GUILayout.EndScrollView();
        }
    }

    [HarmonyPatch]
    internal static class TilesetPickerPatches
    {
        /// <summary>
        /// If the filter would leave a region with no tileset at all, generating it would fail, so that
        /// region ignores the filter.
        /// </summary>
        [HarmonyPatch(typeof(M_Region), nameof(M_Region.GetLevels))]
        [HarmonyPrefix]
        private static void GetLevelsPrefix(M_Region __instance)
        {
            TilesetPicker.Bypass = false;
            if (TilesetPicker.Off.Count == 0 || __instance.subregionGroups == null)
            {
                return;
            }
            bool any = __instance.subregionGroups.Any(g => g?.subregions != null && g.subregions.Any(s => s != null && TilesetPicker.Allowed(s)));
            if (!any)
            {
                TilesetPicker.Bypass = true;
                Plugin.Log.LogWarning($"[Dev] Every tileset of {__instance.regionName} is off; using them all for this region.");
            }
        }

        [HarmonyPatch(typeof(M_Region), nameof(M_Region.GetLevels))]
        [HarmonyFinalizer]
        private static void GetLevelsFinalizer()
        {
            TilesetPicker.Bypass = false;
        }

        /// <summary>The game's own pick, with the unticked tilesets taken out first.</summary>
        [HarmonyPatch(typeof(M_Region.SubregionGroup), nameof(M_Region.SubregionGroup.GetSubregion))]
        [HarmonyPrefix]
        private static bool GetSubregionPrefix(M_Region.SubregionGroup __instance, bool allowForced, ref M_Subregion __result)
        {
            if (TilesetPicker.Bypass || TilesetPicker.Off.Count == 0 || __instance.subregions == null)
            {
                return true;
            }
            var list = new List<M_Subregion>();
            foreach (M_Subregion sub in __instance.subregions)
            {
                if (sub == null || !TilesetPicker.Allowed(sub))
                {
                    continue;
                }
                if (allowForced && sub.ForceSpawn())
                {
                    __result = sub;
                    return false;
                }
                if (sub.CanSpawn() && sub.spawnSettings.RandomCheck())
                {
                    list.Add(sub);
                }
            }
            __result = list.Count == 0 ? null : list[Random.Range(0, list.Count)];
            return false;
        }
    }
}
