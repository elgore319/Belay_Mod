using System.Collections.Generic;
using System.Linq;
using System.Text;
using Belay.Net;
using Belay.Patches;
using Belay.Players;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Checks that every player got the same tower. A few seconds after each level spawns (so the
    /// randomizers' Start has run), each player fingerprints it: which level it is, where it sits,
    /// whether it's mirrored, its subregion, which variant children are on, and the picked textures.
    /// Clients send their fingerprints to the host, which compares them with its own. Any difference
    /// shows on screen for both players and is written to LogOutput.log with both summaries.
    /// </summary>
    public class LevelCheck : MonoBehaviour
    {
        private const float SettleTime = 3f;

        public static LevelCheck Instance { get; private set; }

        /// <summary>Levels checked so far this run, and how many differed. A client counts the levels it sent.</summary>
        public int Checked { get; private set; }
        public int Mismatches { get; private set; }

        private class Fingerprint
        {
            public int Hash;
            public string Summary;
        }

        private readonly Dictionary<M_Level, float> firstSeen = new Dictionary<M_Level, float>();
        private readonly HashSet<M_Level> done = new HashSet<M_Level>();
        // Host: its own fingerprints, and client fingerprints that arrived before its own was ready.
        private readonly Dictionary<int, Fingerprint> own = new Dictionary<int, Fingerprint>();
        private readonly Dictionary<int, List<KeyValuePair<SteamId, Fingerprint>>> waiting = new Dictionary<int, List<KeyValuePair<SteamId, Fingerprint>>>();
        private float nextScan;
        private int runId;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.LevelHash, HandleLevelHash);
            NetSession.Instance.On(PacketType.LevelMismatch, HandleMismatch);
        }

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (!net.InLobby || !WorldSpace.Ready || WorldLoader.instance == null)
            {
                return;
            }
            if (runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                firstSeen.Clear();
                done.Clear();
                own.Clear();
                waiting.Clear();
                Checked = 0;
                Mismatches = 0;
            }
            if (Time.unscaledTime < nextScan)
            {
                return;
            }
            nextScan = Time.unscaledTime + 1f;

            var branch = WorldLoader.instance.GetCurrentBranch();
            if (branch?.levelTracker == null)
            {
                return;
            }
            foreach (var info in branch.levelTracker)
            {
                M_Level level = info?.GetLevel();
                if (level == null || done.Contains(level))
                {
                    continue;
                }
                if (!firstSeen.TryGetValue(level, out float seen))
                {
                    firstSeen[level] = Time.unscaledTime;
                    continue;
                }
                if (Time.unscaledTime - seen < SettleTime)
                {
                    continue;
                }
                done.Add(level);
                Fingerprint fp = Compute(level, out int levelId);
                if (net.IsHost)
                {
                    own[levelId] = fp;
                    if (waiting.TryGetValue(levelId, out var pending))
                    {
                        foreach (var p in pending)
                        {
                            Compare(levelId, p.Key, p.Value);
                        }
                        waiting.Remove(levelId);
                    }
                }
                else
                {
                    Checked++;
                    net.Send(PacketType.LevelHash, w =>
                    {
                        w.Write(levelId);
                        w.Write(fp.Hash);
                        w.Write(fp.Summary);
                    });
                }
            }
        }

        private static Fingerprint Compute(M_Level level, out int levelId)
        {
            levelId = LevelRandomPatches.LevelIdOf(level);
            var sb = new StringBuilder();
            sb.Append(level.name.Replace("(Clone)", ""));
            sb.Append(" y=").Append(Mathf.RoundToInt(WorldSpace.ToShared(level.transform.position).y));
            sb.Append(level.transform.localScale.x < 0f ? " mirrored" : " normal");
            if (level.subRegion != null)
            {
                sb.Append(" sub=").Append(level.subRegion.name);
            }
            foreach (UT_RandomChildActivator rca in level.GetComponentsInChildren<UT_RandomChildActivator>(true))
            {
                sb.Append(" v[");
                for (int i = 0; i < rca.transform.childCount; i++)
                {
                    sb.Append(rca.transform.GetChild(i).gameObject.activeSelf ? '1' : '0');
                }
                sb.Append(']');
            }
            foreach (UT_TextureRandomizer tr in level.GetComponentsInChildren<UT_TextureRandomizer>(true))
            {
                Texture tex = tr.targetRenderer != null && tr.targetRenderer.material != null ? tr.targetRenderer.material.mainTexture : null;
                sb.Append(" t=").Append(tex != null ? tex.name : "-");
            }
            string summary = sb.ToString();
            unchecked
            {
                int h = 17;
                foreach (char c in summary)
                {
                    h = h * 31 + c;
                }
                return new Fingerprint { Hash = h, Summary = summary };
            }
        }

        private void HandleLevelHash(SteamId origin, System.IO.BinaryReader r)
        {
            if (!NetSession.Instance.IsHost)
            {
                return;
            }
            int levelId = r.ReadInt32();
            var fp = new Fingerprint { Hash = r.ReadInt32(), Summary = r.ReadString() };
            if (own.ContainsKey(levelId))
            {
                Compare(levelId, origin, fp);
            }
            else
            {
                if (!waiting.TryGetValue(levelId, out var list))
                {
                    waiting[levelId] = list = new List<KeyValuePair<SteamId, Fingerprint>>();
                }
                list.Add(new KeyValuePair<SteamId, Fingerprint>(origin, fp));
            }
        }

        private void Compare(int levelId, SteamId client, Fingerprint theirs)
        {
            Fingerprint mine = own[levelId];
            Checked++;
            if (mine.Hash == theirs.Hash)
            {
                return;
            }
            Mismatches++;
            string who = new Friend(client).Name;
            Plugin.Log.LogWarning($"LEVEL MISMATCH #{levelId} with {who}\n  host:   {mine.Summary}\n  {who}: {theirs.Summary}");
            Run.RunFlow.Instance?.ShowNotice($"Level {levelId + 1} is different for {who} (details in LogOutput.log)", 8f);
            NetSession.Instance.Send(PacketType.LevelMismatch, w =>
            {
                w.Write(client.Value);
                w.Write(levelId);
                w.Write(mine.Summary);
            });
        }

        private void HandleMismatch(SteamId origin, System.IO.BinaryReader r)
        {
            if (origin != NetSession.Instance.HostId)
            {
                return;
            }
            ulong target = r.ReadUInt64();
            int levelId = r.ReadInt32();
            string hostSummary = r.ReadString();
            if (target != SteamClient.SteamId.Value)
            {
                return;
            }
            Mismatches++;
            Plugin.Log.LogWarning($"LEVEL MISMATCH #{levelId}: the host has {hostSummary}");
            Run.RunFlow.Instance?.ShowNotice($"Level {levelId + 1} is different from the host's (details in LogOutput.log)", 8f);
        }
    }
}
