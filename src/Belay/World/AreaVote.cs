using System.Collections.Generic;
using System.Linq;
using Belay.Net;
using Belay.Players;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Moving to the next area takes a team vote at the room's vote terminal (see VoteTerminal): whatever carries
    /// the team on (elevator button, etc., not the lockdown) does nothing until more than half the living players
    /// vote yes. Doors stay free. No on-screen text, bots don't count, and the host counts votes; results hold for
    /// the run.
    /// </summary>
    public class AreaVote : MonoBehaviour
    {
        public static AreaVote Instance { get; private set; }

        /// <summary>Rooms the team voted to leave this run (by room id).</summary>
        private static readonly HashSet<string> passed = new HashSet<string>();

        // Host: who voted which way, per room.
        private readonly Dictionary<string, Dictionary<ulong, bool>> votes = new Dictionary<string, Dictionary<ulong, bool>>();
        // Everyone: our own votes, sent again if the host changes.
        private readonly Dictionary<string, bool> mine = new Dictionary<string, bool>();
        private int runId;
        private SteamId lastHost;
        private float nextCheck;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession net = NetSession.Instance;
            net.On(PacketType.MoveOnVote, (origin, r) => HostCount(origin, r.ReadString(), r.ReadBoolean()));
            net.On(PacketType.MoveOnPassed, (origin, r) =>
            {
                if (origin != NetSession.Instance.HostId) return;
                int n = r.ReadInt32();
                for (int i = 0; i < n && i < 256; i++) MarkPassed(r.ReadString());
            });
        }

        internal static string RoomId(M_Level level) => level != null ? ButtonSync.ObjectId(level) : null;

        /// <summary>The team has voted to leave this room (or it doesn't take a vote).</summary>
        internal static bool HasPassed(M_Level level)
        {
            return level == null || passed.Contains(RoomId(level));
        }

        /// <summary>This room holds the team until it votes to move on.</summary>
        internal static bool Holds(M_Level level)
        {
            return level != null && InterludeGate.IsGatedRoom(level) && VoteTerminal.HasFor(level) && !HasPassed(level);
        }

        internal void Cast(M_Level level, bool moveOn)
        {
            string id = RoomId(level);
            if (id == null)
            {
                return;
            }
            mine[id] = moveOn;
            Plugin.Log.LogInfo($"Voted to {(moveOn ? "move on" : "stay")}: {id}");
            Send(id, moveOn);
        }

        private void Send(string id, bool moveOn)
        {
            NetSession net = NetSession.Instance;
            if (net.IsHost)
            {
                HostCount(NetSession.LocalId, id, moveOn);
            }
            else
            {
                net.Send(PacketType.MoveOnVote, w =>
                {
                    w.Write(id);
                    w.Write(moveOn);
                });
            }
        }

        private void HostCount(SteamId voter, string id, bool moveOn)
        {
            if (!NetSession.Instance.IsHost || string.IsNullOrEmpty(id) || passed.Contains(id))
            {
                return;
            }
            if (!votes.TryGetValue(id, out var room))
            {
                votes[id] = room = new Dictionary<ulong, bool>();
            }
            room[voter.Value] = moveOn;
            HostCheck();
        }

        /// <summary>Living players, not bots. If everyone is dead, everyone counts.</summary>
        private static List<SteamId> Voters()
        {
            List<SteamId> all = NetSession.Instance.MemberIds()
                .Where(id => Bot.TestBot.Instance == null || !Bot.TestBot.Instance.IsBot(id)).ToList();
            List<SteamId> living = all.Where(id =>
            {
                if (id == NetSession.LocalId)
                {
                    return ENT_Player.playerObject == null || !ENT_Player.playerObject.dead;
                }
                return !PlayerSync.Instance.Remotes.TryGetValue(id, out RemotePlayer remote) || remote == null || !remote.Latest.Dead;
            }).ToList();
            return living.Count > 0 ? living : all;
        }

        private void HostCheck()
        {
            if (votes.Count == 0)
            {
                return;
            }
            List<SteamId> voters = Voters();
            var done = new List<string>();
            foreach (var pair in votes)
            {
                int yes = voters.Count(v => pair.Value.TryGetValue(v.Value, out bool y) && y);
                if (yes * 2 > voters.Count)
                {
                    Plugin.Log.LogInfo($"Team voted to move on ({yes} of {voters.Count}): {pair.Key}");
                    done.Add(pair.Key);
                }
            }
            if (done.Count == 0)
            {
                return;
            }
            foreach (string id in done)
            {
                votes.Remove(id);
                MarkPassed(id);
            }
            SendPassed();
        }

        private void SendPassed()
        {
            var all = passed.ToList();
            NetSession.Instance.Send(PacketType.MoveOnPassed, w =>
            {
                w.Write(all.Count);
                foreach (string id in all) w.Write(id);
            });
        }

        private static void MarkPassed(string id)
        {
            if (passed.Add(id))
            {
                Plugin.Log.LogInfo("Way on is open: " + id);
            }
        }

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (net == null || !net.InLobby)
            {
                return;
            }
            if (runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                passed.Clear();
                votes.Clear();
                mine.Clear();
            }
            if (lastHost.Value != 0 && lastHost != net.HostId)
            {
                // A new host doesn't have the old one's count: everyone sends their votes again.
                foreach (var pair in mine)
                {
                    if (!passed.Contains(pair.Key)) Send(pair.Key, pair.Value);
                }
                if (net.IsHost && passed.Count > 0) SendPassed();
            }
            lastHost = net.HostId;
            if (net.IsHost && Time.unscaledTime >= nextCheck)
            {
                // Someone dying or leaving can tip a vote.
                nextCheck = Time.unscaledTime + 1f;
                HostCheck();
            }
        }
    }
}
