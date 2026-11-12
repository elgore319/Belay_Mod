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
    ///
    /// The snapshot is the progress of the whole team put together: every player sends their own
    /// progress to the host while in the lobby, and anything any one of them has unlocked counts as
    /// unlocked for the run. So a door that opens by itself for the furthest-along player opens by
    /// itself for everyone.
    /// </summary>
    internal static class HostProfile
    {
        public const string LobbyKey = "belay_profile";
        private const int MaxLength = 7500;

        private static readonly HashSet<string> Flags = new HashSet<string>();
        private static readonly HashSet<string> Unlocks = new HashSet<string>();
        private static readonly HashSet<string> Upgrades = new HashSet<string>();
        private static readonly Dictionary<string, string> Codes = new Dictionary<string, string>();
        /// <summary>Host: the progress each teammate last sent, as their own Capture text.</summary>
        private static readonly Dictionary<ulong, string> MemberProfiles = new Dictionary<ulong, string>();
        /// <summary>Saved flags that hold a random code (keypad codes, radio settings); the host's values are used.</summary>
        private static readonly string[] CodeFlags = { "sinkcode", "chutecode", "diverfrequency", "diveramplitude" };
        private static bool won;
        private static bool unlockAll;
        private static int loadedRunId;
        /// <summary>True while reading this PC's own progress, so the patches below don't answer for it.</summary>
        internal static bool Capturing;
        private static string lastSent;
        private static float nextSendCheck;

        /// <summary>The snapshot applies only while we're in a lobby run it was taken for.</summary>
        public static bool Active
        {
            get
            {
                NetSession net = NetSession.Instance;
                return !Capturing && loadedRunId != 0 && net != null && net.InLobby && net.CurrentRun.RunId == loadedRunId
                    && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Main-Menu";
            }
        }

        // ---------------------------------------------------------------- Host: take the snapshot

        /// <summary>This PC's own progress, straight from its save.</summary>
        public static string Capture()
        {
            var flags = new List<string>();
            var unlocks = new List<string>();
            var upgrades = new List<string>();
            var wonModes = new List<string>();
            var codes = new List<string>();
            Capturing = true;
            try
            {
                if (StatManager.saveData?.flags != null)
                {
                    foreach (StatManager.SaveData.SaveFlags f in StatManager.saveData.flags)
                    {
                        if (f == null || string.IsNullOrEmpty(f.name) || f.name.IndexOf('|') >= 0) continue;
                        if (System.Array.IndexOf(CodeFlags, f.name) >= 0)
                        {
                            if (!string.IsNullOrEmpty(f.data) && f.data.IndexOf('|') < 0 && f.data.IndexOf('\n') < 0) codes.Add(f.name + "=" + f.data);
                        }
                        else if (f.value) flags.Add(f.name);
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
                if (StatManager.saveData?.gameModes != null)
                {
                    foreach (StatManager.SaveData.GameModeData gm in StatManager.saveData.gameModes)
                    {
                        if (gm?.stats == null || string.IsNullOrEmpty(gm.name) || gm.name.IndexOf('|') >= 0) continue;
                        if (StatManager.GetStatisticInt(gm.stats, "wins") > 0) wonModes.Add(gm.name);
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Reading progress failed: " + e.Message);
            }
            finally
            {
                Capturing = false;
            }
            bool all = SettingsManager.settings != null && SettingsManager.settings.unlockAll;

            var sb = new StringBuilder();
            sb.Append("A:").Append(all ? '1' : '0').Append('\n');
            sb.Append("M:").Append(string.Join("|", wonModes)).Append('\n');
            sb.Append("D:").Append(string.Join("|", codes)).Append('\n');
            sb.Append("U:").Append(string.Join("|", unlocks)).Append('\n');
            sb.Append("G:").Append(string.Join("|", upgrades)).Append('\n');
            sb.Append("F:").Append(string.Join("|", flags));
            return sb.ToString();
        }

        /// <summary>
        /// Host, at run start: its own progress plus everything any teammate in the lobby has unlocked.
        /// Random codes and the "unlock all" setting stay the host's.
        /// </summary>
        public static string CaptureTeam(M_Gamemode mode)
        {
            string own = Capture();
            var flags = new HashSet<string>();
            var unlocks = new HashSet<string>();
            var upgrades = new HashSet<string>();
            var wonModes = new HashSet<string>();
            string codes = "";
            bool all = false;
            int teammates = 0;
            NetSession net = NetSession.Instance;
            var texts = new List<string> { own };
            if (net != null)
            {
                foreach (Steamworks.SteamId id in net.MemberIds())
                {
                    if (id != NetSession.LocalId && MemberProfiles.TryGetValue(id.Value, out string theirs))
                    {
                        texts.Add(theirs);
                        teammates++;
                    }
                }
            }
            for (int i = 0; i < texts.Count; i++)
            {
                foreach (string line in texts[i].Split('\n'))
                {
                    if (line.Length < 2 || line[1] != ':') continue;
                    string body = line.Substring(2);
                    switch (line[0])
                    {
                        case 'A': if (i == 0) all = body == "1"; break;
                        case 'D': if (i == 0) codes = body; break;
                        case 'M': AddAll(wonModes, body); break;
                        case 'U': AddAll(unlocks, body); break;
                        case 'G': AddAll(upgrades, body); break;
                        case 'F': AddAll(flags, body); break;
                    }
                }
            }
            string modeName = null;
            try { modeName = mode != null ? mode.GetGamemodeName() : null; } catch { }
            bool hasWon = mode != null && ((modeName != null && wonModes.Contains(modeName)) || wonModes.Contains(mode.gamemodeName));
            if (!hasWon && mode != null)
            {
                Capturing = true;
                try { hasWon = mode.HasWonGamemode(); } catch { } finally { Capturing = false; }
            }

            var sb = new StringBuilder();
            sb.Append("W:").Append(hasWon ? '1' : '0').Append('\n');
            sb.Append("A:").Append(all ? '1' : '0').Append('\n');
            sb.Append("D:").Append(codes).Append('\n');
            sb.Append("U:").Append(string.Join("|", unlocks)).Append('\n');
            sb.Append("G:").Append(string.Join("|", upgrades)).Append('\n');
            sb.Append("F:").Append(string.Join("|", flags));
            string text = sb.ToString();
            Plugin.Log.LogInfo($"Team progress for this run: host + {teammates} teammate(s), {flags.Count} flags, {unlocks.Count} unlocks, {upgrades.Count} upgrades");
            if (text.Length > MaxLength)
            {
                Plugin.Log.LogWarning($"Team progress snapshot is {text.Length} chars; trimming flags to fit lobby data");
                text = text.Substring(0, MaxLength);
                text = text.Substring(0, text.LastIndexOf('|'));
            }
            return text;
        }

        // ---------------------------------------------------------------- Teammates: send own progress to the host

        public static void Register(NetSession net)
        {
            net.On(PacketType.ProgressProfile, (origin, r) =>
            {
                string text = r.ReadString();
                if (!net.IsHost || text.Length > 60000) return;
                MemberProfiles[origin.Value] = text;
                Plugin.Log.LogInfo($"Got progress from {NetSession.NameOf(origin)} ({text.Length} chars)");
            });
        }

        /// <summary>
        /// Called every frame. Outside a run, a non-host player keeps the host up to date with their own
        /// progress (it can change between runs), so it's there when the host starts the next one.
        /// </summary>
        public static void Tick()
        {
            NetSession net = NetSession.Instance;
            if (net == null || !net.InLobby)
            {
                lastSent = null;
                if (MemberProfiles.Count > 0) MemberProfiles.Clear();
                return;
            }
            if (net.IsHost || UnityEngine.Time.unscaledTime < nextSendCheck) return;
            nextSendCheck = UnityEngine.Time.unscaledTime + 3f;
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Main-Menu" || StatManager.saveData == null) return;
            string text = Capture();
            string key = net.HostId.Value + "\n" + text;
            if (key == lastSent) return;
            lastSent = key;
            net.Send(PacketType.ProgressProfile, w => w.Write(text));
        }

        // ---------------------------------------------------------------- Everyone: load it

        public static void Load(string text, int runId)
        {
            Flags.Clear();
            Unlocks.Clear();
            Upgrades.Clear();
            Codes.Clear();
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
                    case 'D':
                        foreach (string pair in body.Split('|'))
                        {
                            int eq = pair.IndexOf('=');
                            if (eq > 0) Codes[pair.Substring(0, eq)] = pair.Substring(eq + 1);
                        }
                        break;
                }
            }
            loadedRunId = runId;
            Plugin.Log.LogInfo($"Using the team's progress for this run: {Flags.Count} flags, {Unlocks.Count} unlocks, {Upgrades.Count} upgrades, beaten={won}");
        }

        /// <summary>
        /// The game copies every saved flag into the run's own flag list when the run scene starts, and
        /// doors, props and events read that list. Rewrite it from the team snapshot: on where anyone on
        /// the team has it, off otherwise, and the host's codes.
        /// </summary>
        public static void ApplyToRunFlags(CL_GameManager gMan)
        {
            if (!Active || gMan == null || gMan.sessionFlags == null) return;
            int turnedOn = 0, turnedOff = 0;
            var present = new HashSet<string>();
            foreach (CL_GameManager.SessionFlag f in gMan.sessionFlags)
            {
                if (f == null || string.IsNullOrEmpty(f.name)) continue;
                present.Add(f.name);
                if (System.Array.IndexOf(CodeFlags, f.name) >= 0) continue;
                bool want = Flags.Contains(f.name);
                if (f.state != want)
                {
                    f.state = want;
                    if (want) turnedOn++; else turnedOff++;
                }
            }
            foreach (string name in Flags)
            {
                if (present.Contains(name)) continue;
                CL_GameManager.SetGameFlag(name, true);
                turnedOn++;
            }
            foreach (KeyValuePair<string, string> code in Codes)
            {
                CL_GameManager.SetGameFlag(code.Key, true, code.Value);
            }
            Plugin.Log.LogInfo($"Run flags set from team progress: {turnedOn} turned on, {turnedOff} turned off, {Codes.Count} codes");
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
        [HarmonyPatch(typeof(CL_GameManager), "SetupFlags")]
        [HarmonyPostfix]
        private static void SetupFlags(CL_GameManager __instance)
        {
            try { HostProfile.ApplyToRunFlags(__instance); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Applying team progress to run flags failed: " + e.Message); }
        }

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
