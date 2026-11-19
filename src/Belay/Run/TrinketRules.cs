using System.Collections.Generic;
using System.Linq;
using Belay.Net;
using HarmonyLib;

namespace Belay.Run
{
    /// <summary>
    /// The host can ban trinkets and bindings for the lobby. The banned asset names live on the lobby
    /// (NetSession.KeyBannedTrinkets, '|'-separated), so every member reads the same list. Banned ones
    /// don't show up in the game's trinket picker while in a lobby, and are taken off a player's pick
    /// before the run's trinkets activate (TrinketBudget then trims anything that is now over budget).
    /// </summary>
    internal static class TrinketRules
    {
        private static readonly AccessTools.FieldRef<List<CL_AssetManager.WKDatabaseHolder>> Databases =
            AccessTools.StaticFieldRefAccess<List<CL_AssetManager.WKDatabaseHolder>>(AccessTools.Field(typeof(CL_AssetManager), "databases"));

        private static string cachedRaw;
        private static HashSet<string> cachedBans = new HashSet<string>();

        public static HashSet<string> Banned
        {
            get
            {
                NetSession net = NetSession.Instance;
                string raw = net != null && net.InLobby ? net.GetLobbyData(NetSession.KeyBannedTrinkets) ?? "" : "";
                if (raw != cachedRaw)
                {
                    cachedRaw = raw;
                    cachedBans = new HashSet<string>(raw.Split(new[] { '|' }, System.StringSplitOptions.RemoveEmptyEntries));
                }
                return cachedBans;
            }
        }

        public static bool IsBanned(Trinket t) => t != null && Banned.Contains(t.name);

        public static void SetBanned(Trinket t, bool banned)
        {
            NetSession net = NetSession.Instance;
            if (net == null || !net.IsHost || t == null)
            {
                return;
            }
            var bans = new HashSet<string>(Banned);
            if (banned) bans.Add(t.name);
            else bans.Remove(t.name);
            net.SetLobbyData(NetSession.KeyBannedTrinkets, string.Join("|", bans.OrderBy(n => n)));
        }

        /// <summary>Every trinket and binding the game ships (not the "coming soon" ones), trinkets first.</summary>
        public static List<Trinket> All()
        {
            var result = new List<Trinket>();
            List<CL_AssetManager.WKDatabaseHolder> dbs = Databases();
            if (dbs == null)
            {
                return result;
            }
            var seen = new HashSet<string>();
            foreach (CL_AssetManager.WKDatabaseHolder holder in dbs)
            {
                if (holder?.database?.trinketAssets == null) continue;
                foreach (Trinket t in holder.database.trinketAssets)
                {
                    if (t != null && !t.comingSoon && seen.Add(t.name))
                    {
                        result.Add(t);
                    }
                }
            }
            return result.OrderBy(t => t.isBinding).ThenBy(t => t.title).ToList();
        }

        /// <summary>Takes banned trinkets off the saved pick for this gamemode. Called before the budget check.</summary>
        public static void StripBanned(string mode)
        {
            if (Banned.Count == 0)
            {
                return;
            }
            List<string> picked = StatManager.saveData.GetGamemodeTrinkets(mode);
            if (picked == null || picked.Count == 0)
            {
                return;
            }
            var kept = new List<string>();
            foreach (string name in picked)
            {
                Trinket t = CL_AssetManager.GetTrinketAsset(name);
                if (!IsBanned(t))
                {
                    kept.Add(name);
                }
            }
            if (kept.Count != picked.Count)
            {
                StatManager.saveData.SetGamemodeTrinkets(mode, kept);
                Plugin.Log.LogInfo($"Left banned trinkets behind ({picked.Count - kept.Count}).");
            }
        }

        /// <summary>Hides banned trinkets and bindings from the game's picker while in a lobby.</summary>
        [HarmonyPatch(typeof(UI_TrinketPicker), nameof(UI_TrinketPicker.PopulateTrinkets))]
        private static class PickerPatch
        {
            [HarmonyPrefix]
            private static void Prefix(ref List<Trinket> trinketsToPopulate)
            {
                if (trinketsToPopulate != null && Banned.Count > 0)
                {
                    trinketsToPopulate = trinketsToPopulate.Where(t => !IsBanned(t)).ToList();
                }
            }
        }
    }
}
