using System.Collections.Generic;
using System.IO;
using Belay.Net;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Each PC picks the next stretch of levels itself when its player gets near. The pick reads more than the
    /// seed: per-player stats and lifetime totals, run flags set by things only one player did, difficulty,
    /// best climb distance, and each region's own leftover level pool. So areas like the Nest could come out as
    /// different tilesets on different PCs. Now the first PC to pick a stretch shares its pick (keyed by the
    /// build's seed and its place in its map branch, which every PC shares), and every other PC builds that same stretch.
    /// </summary>
    public class LevelLists : MonoBehaviour
    {
        public static LevelLists Instance { get; private set; }

        internal class Entry
        {
            public string Level;
            public string Region;
            public string Subregion;
            public bool Pausing;
        }

        private readonly Dictionary<string, List<Entry>> picks = new Dictionary<string, List<Entry>>();
        private int runId;

        /// <summary>Set when a build starts; the generator's next list is the one for this key.</summary>
        internal static string ArmedKey;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.LevelList, HandleList);
            NetSession.Instance.OnPeerHello += CatchUp;
        }

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (net != null && net.InLobby && runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                picks.Clear();
            }
        }

        /// <summary>
        /// Host: someone joined (or rejoined) mid-run with a fresh map. Send them every stretch picked so far, so
        /// they build the same levels as the team instead of picking their own.
        /// </summary>
        private void CatchUp(SteamId peer)
        {
            if (picks.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<string, List<Entry>> pair in picks)
            {
                SendList(peer, pair.Key, pair.Value);
            }
            Plugin.Log.LogInfo($"Sent {picks.Count} level pick(s) to a teammate who joined");
        }

        private static void SendList(SteamId? to, string key, List<Entry> mine)
        {
            System.Action<BinaryWriter> payload = w =>
            {
                w.Write(key);
                w.Write(mine.Count);
                foreach (Entry e in mine)
                {
                    w.Write(e.Level);
                    w.Write(e.Region);
                    w.Write(e.Subregion);
                    w.Write(e.Pausing);
                }
            };
            if (to.HasValue) NetSession.Instance.SendTo(to.Value, PacketType.LevelList, payload);
            else NetSession.Instance.Send(PacketType.LevelList, payload);
        }

        internal static bool Active
        {
            get
            {
                NetSession net = NetSession.Instance;
                return Instance != null && net != null && net.InLobby && net.Seed != 0;
            }
        }

        private void HandleList(SteamId origin, BinaryReader r)
        {
            string key = r.ReadString();
            int n = r.ReadInt32();
            var list = new List<Entry>(n);
            for (int i = 0; i < n; i++)
            {
                list.Add(new Entry { Level = r.ReadString(), Region = r.ReadString(), Subregion = r.ReadString(), Pausing = r.ReadBoolean() });
            }
            if (origin == NetSession.LocalId)
            {
                return;
            }
            if (picks.ContainsKey(key))
            {
                return; // already built (or picked) here; the first pick stands
            }
            picks[key] = list;
            Plugin.Log.LogInfo($"Teammate picked {n} levels for build {key}; building the same here when we get there");
        }

        /// <summary>The generator just made its list for the armed build: share it, or swap in the team's.</summary>
        internal void OnList(string key, ref List<M_Level.LevelAssetHolder> list)
        {
            if (list == null)
            {
                return;
            }
            if (picks.TryGetValue(key, out List<Entry> team))
            {
                List<M_Level.LevelAssetHolder> built = Build(team, list);
                if (built == null)
                {
                    Plugin.Log.LogWarning($"Couldn't build the team's levels for build {key}; keeping this PC's own");
                    return;
                }
                if (!Same(team, list))
                {
                    Plugin.Log.LogInfo($"Build {key}: this PC picked {Names(list)}; using the team's {Names(built)}");
                }
                list = built;
                return;
            }
            var mine = new List<Entry>(list.Count);
            foreach (M_Level.LevelAssetHolder h in list)
            {
                if (h == null) continue;
                mine.Add(new Entry
                {
                    Level = h.id ?? "",
                    Region = h.region != null ? h.region.name : "",
                    Subregion = h.subregion != null ? h.subregion.name : "",
                    Pausing = h.isPausingGeneration,
                });
            }
            picks[key] = mine;
            SendList(null, key, mine);
        }

        private static bool Same(List<Entry> team, List<M_Level.LevelAssetHolder> list)
        {
            if (team.Count != list.Count) return false;
            for (int i = 0; i < team.Count; i++)
            {
                if (list[i] == null || !string.Equals(list[i].id, team[i].Level, System.StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private static string Names(List<M_Level.LevelAssetHolder> list)
        {
            var names = new List<string>();
            foreach (M_Level.LevelAssetHolder h in list) names.Add(h?.id ?? "?");
            return string.Join(", ", names);
        }

        /// <summary>
        /// The team's list as this PC's level holders: the same holder objects the generator uses where it can
        /// (this PC's own pick, then the subregion's level lists), else the game's level database.
        /// </summary>
        private static List<M_Level.LevelAssetHolder> Build(List<Entry> team, List<M_Level.LevelAssetHolder> own)
        {
            var spare = new List<M_Level.LevelAssetHolder>(own);
            var result = new List<M_Level.LevelAssetHolder>(team.Count);
            foreach (Entry e in team)
            {
                M_Region region = string.IsNullOrEmpty(e.Region) ? null : CL_AssetManager.GetRegionAsset(e.Region);
                M_Subregion sub = string.IsNullOrEmpty(e.Subregion) ? null : CL_AssetManager.GetSubregionAsset(e.Subregion);
                M_Level.LevelAssetHolder h = TakeMatch(spare, e.Level) ?? FromSubregion(sub, e.Level) ?? CL_AssetManager.GetLevelAsset(e.Level);
                if (h == null)
                {
                    Plugin.Log.LogWarning("Level " + e.Level + " isn't in this game's level list");
                    return null;
                }
                if (region != null) h.region = region;
                if (sub != null) h.subregion = sub;
                h.isPausingGeneration = e.Pausing;
                result.Add(h);
            }
            return result;
        }

        private static M_Level.LevelAssetHolder TakeMatch(List<M_Level.LevelAssetHolder> list, string id)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && string.Equals(list[i].id, id, System.StringComparison.OrdinalIgnoreCase))
                {
                    M_Level.LevelAssetHolder h = list[i];
                    list.RemoveAt(i);
                    return h;
                }
            }
            return null;
        }

        private static M_Level.LevelAssetHolder FromSubregion(M_Subregion sub, string id)
        {
            if (sub == null) return null;
            M_Level.LevelAssetHolder h = Match(sub.startLevelReferences, id) ?? Match(sub.endLevelReferences, id); // the game moved a subregion's levels into its level groups
            if (h != null || sub.levelGroups == null) return h;
            foreach (M_Subregion.LevelGroup g in sub.levelGroups)
            {
                h = Match(g?.levelReferences, id);
                if (h != null) return h;
            }
            return null;
        }

        private static M_Level.LevelAssetHolder Match(List<M_Level.LevelAssetHolder> list, string id)
        {
            if (list == null) return null;
            foreach (M_Level.LevelAssetHolder h in list)
            {
                if (h != null && string.Equals(h.id, id, System.StringComparison.OrdinalIgnoreCase)) return h;
            }
            return null;
        }
    }

    [HarmonyPatch]
    internal static class LevelListPatches
    {
        /// <summary>
        /// Runs once the build's coroutine is made (after the seed patch), before its first step: the next list
        /// the generator makes belongs to this build. Builds from a paused list, a save or a preload are left
        /// alone (they don't pick anything new).
        /// </summary>
        [HarmonyPatch(typeof(WorldLoader), "GenerateLevels")]
        [HarmonyPostfix]
        private static void Arm(WorldLoader.BranchInfo branch, WorldLoader.GenerationParameters genParams, int ___seed)
        {
            LevelLists.ArmedKey = null;
            if (!LevelLists.Active || ___seed == -1 || branch == null)
            {
                return;
            }
            if (PausedIds(branch) is System.Collections.ICollection paused && paused.Count > 0)
            {
                return;
            }
            if (genParams != null && (genParams.preloadLevels != null || genParams.saveLevels != null))
            {
                return;
            }
            int offset = 0; // the game no longer offsets the build seed
            LevelLists.ArmedKey = unchecked(___seed + offset) + "|" + Patches.BranchKeys.BuildKey(branch);
        }

        private static readonly System.Reflection.FieldInfo PausedField = AccessTools.Field(typeof(WorldLoader.BranchInfo), "pausedLevelIDs");

        private static object PausedIds(WorldLoader.BranchInfo branch) => PausedField?.GetValue(branch);

        [HarmonyPatch(typeof(M_Gamemode), nameof(M_Gamemode.GetGenerationList))]
        [HarmonyPostfix]
        private static void GamemodeList(ref List<M_Level.LevelAssetHolder> __result) => Take(ref __result);

        [HarmonyPatch(typeof(M_GenerationBranch), nameof(M_GenerationBranch.GetGenerationList))]
        [HarmonyPostfix]
        private static void BranchList(ref List<M_Level.LevelAssetHolder> __result) => Take(ref __result);

        private static void Take(ref List<M_Level.LevelAssetHolder> list)
        {
            string key = LevelLists.ArmedKey;
            LevelLists.ArmedKey = null;
            if (key == null || !LevelLists.Active)
            {
                return;
            }
            try
            {
                LevelLists.Instance.OnList(key, ref list);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Sharing the level pick failed: " + e.Message);
            }
        }
    }
}
