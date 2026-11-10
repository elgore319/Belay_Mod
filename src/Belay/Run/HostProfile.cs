using System.Collections.Generic;
using System.Text;
using Belay.Net;
using HarmonyLib;

namespace Belay.Run
{
    /// <summary>
    /// What each player has unlocked changes the tower: subregions and spawns check save flags,
    /// progression unlocks, facility upgrades and "has beaten this mode". With mixed saves (one player
    /// has beaten the game) each PC generated different areas. For a lobby run, the host's progress is
    /// snapshotted into lobby data when the run starts, and every player, the host included, answers
    /// those checks from the snapshot.
    /// </summary>
    internal static class HostProfile
    {
        public const string LobbyKey = "belay_profile";
        private const int MaxLength = 7500;

        private static readonly HashSet<string> Flags = new HashSet<string>();
        private static readonly HashSet<string> Unlocks = new HashSet<string>();
        private static readonly HashSet<string> Upgrades = new HashSet<string>();
        private static bool won;
        private static bool unlockAll;
        private static int loadedRunId;

        /// <summary>The snapshot applies only while we're in a lobby run it was taken for.</summary>
        public static bool Active
        {
            get
            {
                NetSession net = NetSession.Instance;
                return loadedRunId != 0 && net != null && net.InLobby && net.CurrentRun.RunId == loadedRunId
                    && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Main-Menu";
            }
        }

        // ---------------------------------------------------------------- Host: take the snapshot

        public static string Capture(M_Gamemode mode)
        {
            var flags = new List<string>();
            var unlocks = new List<string>();
            var upgrades = new List<string>();
            // Read the real values: the snapshot patches must not answer for themselves here.
            loadedRunId = 0;

            if (StatManager.saveData?.flags != null)
            {
                foreach (StatManager.SaveData.SaveFlags f in StatManager.saveData.flags)
                {
                    if (f != null && f.value && !string.IsNullOrEmpty(f.name)) flags.Add(f.name);
                }
            }
            var dict = Traverse.Create(typeof(CL_ProgressionManager)).Field("progressionDict").GetValue<Dictionary<string, ProgressionUnlock>>();
            if (dict != null)
            {
                foreach (KeyValuePair<string, ProgressionUnlock> kv in dict)
                {
                    if (kv.Value != null && kv.Value.state) unlocks.Add(kv.Key);
                }
            }
            if (StatManager.saveData?.facilities != null)
            {
                foreach (StatManager.SaveData.Facility fac in StatManager.saveData.facilities)
                {
                    if (fac?.upgrades == null) continue;
                    foreach (StatManager.SaveData.Facility.Upgrade up in fac.upgrades)
                    {
                        if (up != null && up.owned) upgrades.Add(fac.id + "/" + up.id);
                    }
                }
            }
            bool hasWon = mode != null && mode.HasWonGamemode();
            bool all = SettingsManager.settings != null && SettingsManager.settings.unlockAll;

            var sb = new StringBuilder();
            sb.Append("W:").Append(hasWon ? '1' : '0').Append('\n');
            sb.Append("A:").Append(all ? '1' : '0').Append('\n');
            sb.Append("U:").Append(string.Join("|", unlocks)).Append('\n');
            sb.Append("G:").Append(string.Join("|", upgrades)).Append('\n');
            sb.Append("F:").Append(string.Join("|", flags));
            string text = sb.ToString();
            if (text.Length > MaxLength)
            {
                Plugin.Log.LogWarning($"Host progress snapshot is {text.Length} chars; trimming flags to fit lobby data");
                text = text.Substring(0, MaxLength);
                text = text.Substring(0, text.LastIndexOf('|'));
            }
            return text;
        }

        // ---------------------------------------------------------------- Everyone: load it

        public static void Load(string text, int runId)
        {
            Flags.Clear();
            Unlocks.Clear();
            Upgrades.Clear();
            won = false;
            unlockAll = false;
            loadedRunId = 0;
            if (string.IsNullOrEmpty(text))
            {
                Plugin.Log.LogWarning("No host progress in lobby data; using your own save for this run");
                return;
            }
            foreach (string line in text.Split('\n'))
            {
                if (line.Length < 2 || line[1] != ':') continue;
                string body = line.Substring(2);
                switch (line[0])
                {
                    case 'W': won = body == "1"; break;
                    case 'A': unlockAll = body == "1"; break;
                    case 'U': AddAll(Unlocks, body); break;
                    case 'G': AddAll(Upgrades, body); break;
                    case 'F': AddAll(Flags, body); break;
                }
            }
            loadedRunId = runId;
            Plugin.Log.LogInfo($"Using the host's progress for this run: {Flags.Count} flags, {Unlocks.Count} unlocks, {Upgrades.Count} upgrades, beaten={won}");
        }

        private static void AddAll(HashSet<string> set, string body)
        {
            foreach (string s in body.Split('|'))
            {
                if (s.Length > 0) set.Add(s);
            }
        }

        // ---------------------------------------------------------------- Answers

        public static bool HasFlag(string name) => Flags.Contains(name);
        public static bool HasUnlock(string name) => Unlocks.Contains(name);
        public static bool OwnsUpgrade(string facility, string upgrade) => Upgrades.Contains(facility + "/" + upgrade);
        public static bool HasWon => won;
        public static bool UnlockAll => unlockAll;
    }

    [HarmonyPatch]
    internal static class HostProfilePatches
    {
        /// <summary>Saved flags come from the host. Flags set during this run (session flags) stay local.</summary>
        [HarmonyPatch(typeof(CL_GameManager), nameof(CL_GameManager.HasActiveFlag))]
        [HarmonyPrefix]
        private static bool HasActiveFlag(string name, ref bool __result)
        {
            if (!HostProfile.Active || string.IsNullOrEmpty(name) || CL_GameManager.gMan == null)
            {
                return true;
            }
            var session = Traverse.Create(CL_GameManager.gMan).Field("sessionFlagDict").GetValue() as System.Collections.IDictionary;
            if (session != null && session.Contains(name))
            {
                return true;
            }
            __result = HostProfile.HasFlag(name);
            return false;
        }

        [HarmonyPatch(typeof(CL_ProgressionManager), nameof(CL_ProgressionManager.HasProgressionUnlock))]
        [HarmonyPrefix]
        private static bool HasProgressionUnlock(string unlockName, ref bool __result)
        {
            if (!HostProfile.Active) return true;
            __result = HostProfile.HasUnlock(unlockName);
            return false;
        }

        [HarmonyPatch(typeof(FacilityUpgrade), nameof(FacilityUpgrade.IsOwned))]
        [HarmonyPrefix]
        private static bool IsOwned(FacilityUpgrade __instance, string facilityID, ref bool __result)
        {
            if (!HostProfile.Active || CL_GameManager.IsIronKnuckle()) return true;
            __result = HostProfile.OwnsUpgrade(facilityID, __instance.id);
            return false;
        }

        [HarmonyPatch(typeof(M_Gamemode), nameof(M_Gamemode.HasWonGamemode))]
        [HarmonyPrefix]
        private static bool HasWonGamemode(M_Gamemode __instance, ref bool __result)
        {
            if (!HostProfile.Active || CL_GameManager.gamemode == null || __instance.gamemodeName != CL_GameManager.gamemode.gamemodeName) return true;
            __result = HostProfile.HasWon;
            return false;
        }

        [HarmonyPatch(typeof(M_Gamemode), nameof(M_Gamemode.HasBeatGamemode))]
        [HarmonyPrefix]
        private static bool HasBeatGamemode(M_Gamemode __instance, ref bool __result)
        {
            return HasWonGamemode(__instance, ref __result);
        }

        /// <summary>"Unlock all" in settings forces some spawns on; use the host's setting.</summary>
        [HarmonyPatch(typeof(SpawnTable.SpawnSettings), nameof(SpawnTable.SpawnSettings.GetEffectiveSpawnChance))]
        [HarmonyPrefix]
        private static void SpawnChancePrefix(out bool __state)
        {
            __state = SettingsManager.settings != null && SettingsManager.settings.unlockAll;
            if (HostProfile.Active && SettingsManager.settings != null)
            {
                SettingsManager.settings.unlockAll = HostProfile.UnlockAll;
            }
        }

        [HarmonyPatch(typeof(SpawnTable.SpawnSettings), nameof(SpawnTable.SpawnSettings.GetEffectiveSpawnChance))]
        [HarmonyPostfix]
        private static void SpawnChancePostfix(bool __state)
        {
            if (SettingsManager.settings != null)
            {
                SettingsManager.settings.unlockAll = __state;
            }
        }
    }
}
