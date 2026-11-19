using System.Collections.Generic;
using System.Linq;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Shortcuts build a new branch of the map from the level they start in (UT_BranchGenerator) and
    /// close the normal path ahead. That used to happen only on the PC of whoever opened it, with an
    /// unseeded pick of which branch to build, so the team ended up on different maps.
    ///
    /// In a lobby, the team decides each shortcut by vote (run by the host), at a terminal:
    ///  - When someone sits at a terminal, every undecided shortcut between that terminal and the next
    ///    one gets a vote, one after another. A shortcut with no terminal of its own is voted on at the
    ///    last terminal before it, so one terminal can carry several votes.
    ///  - Picking the shortcut marks it as approved: when anyone opens it, it opens on every PC at once
    ///    and builds the same branch (seeded from the lobby). Picking the normal path blocks it off.
    ///  - A shortcut nobody voted on yet (no terminal before it, or nobody sat down) still asks the team
    ///    when someone tries to open it, as before.
    /// Rules: whoever asked counts as a vote for the shortcut. A vote ends when everyone has voted or
    /// after VoteSeconds, and the shortcut wins with more votes than the normal path.
    /// </summary>
    public class ShortcutSync : MonoBehaviour
    {
        public static ShortcutSync Instance { get; private set; }

        private const float VoteSeconds = 20f;
        /// <summary>Longer, so teammates have time to walk over to the vote terminal.</summary>
        private const float VoteTerminalSeconds = 40f;

        internal static bool Applying;

        /// <summary>Generators that have opened (or are opening) this run, or were voted down.</summary>
        internal static readonly HashSet<string> Opened = new HashSet<string>();
        /// <summary>Shortcuts the team voted to take at a terminal, not opened yet.</summary>
        internal static readonly HashSet<string> Approved = new HashSet<string>();

        private const int TerminalLookAhead = 12;

        internal enum Outcome : byte { Normal = 0, Open = 1, Approved = 2, NoVotes = 3 }

        private struct Pending
        {
            public SteamId Starter;
            public string Id;
            public bool AtTerminal;
            public bool AtVoteTerminal;
            public int Index, Total;
        }

        // Host: the running vote and the ones waiting behind it.
        private string voteId;
        private SteamId voteStarter;
        private Pending voteInfo;
        private readonly List<Pending> queue = new List<Pending>();
        /// <summary>Votes cast on a vote terminal for a shortcut whose team vote hasn't started yet.</summary>
        private readonly Dictionary<string, Dictionary<ulong, bool>> earlyVotes = new Dictionary<string, Dictionary<ulong, bool>>();
        /// <summary>Someone tried to open these while their vote was still running or queued.</summary>
        private readonly HashSet<string> wantsOpen = new HashSet<string>();
        private readonly HashSet<ulong> yes = new HashSet<ulong>();
        private readonly HashSet<ulong> no = new HashSet<ulong>();
        private float voteEnds;
        private float nextStateSend;

        // Everyone: what to draw.
        private bool showVote;
        private string shownId;
        private float shownEnds;
        private int runId;

        // Everyone: the running vote as the host last described it, so a new host can carry it on.
        private bool shownAtTerminal, shownAtVoteTerminal;
        private ulong shownStarter;
        private readonly HashSet<ulong> shownYesIds = new HashSet<ulong>();
        private readonly HashSet<ulong> shownNoIds = new HashSet<ulong>();
        private SteamId lastHost;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession net = NetSession.Instance;
            net.On(PacketType.ShortcutVoteRequest, (origin, r) =>
            {
                bool atTerminal = r.ReadBoolean();
                int count = r.ReadInt32();
                var ids = new List<string>();
                for (int i = 0; i < count && i < 64; i++) ids.Add(r.ReadString());
                HostRequest(origin, ids, atTerminal);
            });
            net.On(PacketType.ShortcutVote, (origin, r) => HostCount(origin, r.ReadString(), r.ReadBoolean()));
            net.On(PacketType.ShortcutVoteState, (origin, r) =>
            {
                if (origin != NetSession.Instance.HostId) return;
                ReadState(r);
            });
            net.On(PacketType.ShortcutResult, (origin, r) =>
            {
                if (origin != NetSession.Instance.HostId) return;
                string id = r.ReadString();
                var outcome = (Outcome)r.ReadByte();
                string starter = r.ReadString();
                Finish(id, outcome, starter);
            });
        }

        internal static bool Live => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        // ---------------------------------------------------------------- Starting a vote

        /// <summary>A vote cast on a vote terminal: starts the team vote for that shortcut if none is running.</summary>
        internal void CastVote(string id, bool inFavour)
        {
            NetSession net = NetSession.Instance;
            if (net.IsHost)
            {
                HostCount(NetSession.LocalId, id, inFavour);
            }
            else
            {
                net.Send(PacketType.ShortcutVote, w =>
                {
                    w.Write(id);
                    w.Write(inFavour);
                });
            }
        }

        internal static bool IsDecided(string id, out bool shortcut)
        {
            shortcut = Approved.Contains(id);
            return shortcut || Opened.Contains(id);
        }

        private void Ask(List<string> ids, bool atTerminal)
        {
            NetSession net = NetSession.Instance;
            if (net.IsHost)
            {
                HostRequest(NetSession.LocalId, ids, atTerminal);
            }
            else
            {
                net.Send(PacketType.ShortcutVoteRequest, w =>
                {
                    w.Write(atTerminal);
                    w.Write(ids.Count);
                    foreach (string id in ids) w.Write(id);
                });
            }
        }

        internal static M_Level LevelOf(OS_Manager os)
        {
            M_Level level = null;
            if (os != null && os.worldInterface != null)
            {
                level = os.worldInterface.GetComponentInParent<M_Level>(true);
            }
            if (level == null && os != null)
            {
                level = os.GetComponentInParent<M_Level>(true);
            }
            if (level == null && ENT_Player.playerObject != null)
            {
                level = WorldLoader.GetClosestLevelToPosition(ENT_Player.playerObject.transform.position)?.GetLevel();
            }
            return level;
        }

        /// <summary>
        /// The shortcuts a terminal in this level decides: every one from this level up to (not including)
        /// the next level with a terminal of its own, along the same branch of the map.
        /// </summary>
        internal static List<string> ShortcutsAfter(M_Level level, bool includeDecided)
        {
            var ids = new List<string>();
            WorldLoader loader = WorldLoader.instance;
            if (loader == null || level == null)
            {
                return ids;
            }
            WorldLoader.BranchInfo branch = loader.GetBranchOfLevel(level) ?? loader.GetCurrentBranch();
            if (branch == null || branch.levelTracker == null)
            {
                return ids;
            }
            int start = branch.levelTracker.FindIndex(l => l != null && l.GetLevel() == level);
            if (start < 0)
            {
                return ids;
            }
            for (int i = start; i < branch.levelTracker.Count && i < start + TerminalLookAhead; i++)
            {
                M_Level l = branch.levelTracker[i]?.GetLevel();
                if (l == null)
                {
                    continue;
                }
                if (i > start && VoteTerminal.GameTerminalIn(l) != null)
                {
                    break; // the next terminal votes on its own shortcuts
                }
                foreach (UT_BranchGenerator g in l.GetComponentsInChildren<UT_BranchGenerator>(true))
                {
                    string id = ButtonSync.ObjectId(g);
                    if ((includeDecided || !IsDecided(id, out _)) && !ids.Contains(id))
                    {
                        ids.Add(id);
                    }
                }
            }
            return ids;
        }

        /// <summary>
        /// Who the vote waits for: living teammates. A dead player can't get to a terminal to vote, so
        /// counting them made every vote with someone down run out its whole timer. If everyone is dead,
        /// everyone counts (the run is ending anyway).
        /// </summary>
        private List<SteamId> Voters()
        {
            List<SteamId> all = NetSession.Instance.MemberIds();
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

        private bool Waiting(string id)
        {
            return voteId == id || queue.Any(p => p.Id == id);
        }

        private void HostRequest(SteamId starter, List<string> ids, bool atTerminal)
        {
            if (!NetSession.Instance.IsHost)
            {
                return;
            }
            var fresh = new List<string>();
            foreach (string id in ids)
            {
                if (Opened.Contains(id))
                {
                    continue;
                }
                if (!atTerminal)
                {
                    // Someone walked or fell into a shortcut the team hasn't turned down. It has to exist
                    // right now (some shortcuts are a drop: waiting on a vote meant falling to your death),
                    // so it opens for everyone at once and any vote on it is dropped.
                    CancelVote(id);
                    HostSend(id, Outcome.Open, NetSession.NameOf(starter));
                    continue;
                }
                else if (Approved.Contains(id) || Waiting(id))
                {
                    continue;
                }
                fresh.Add(id);
            }
            for (int i = 0; i < fresh.Count; i++)
            {
                queue.Add(new Pending { Starter = starter, Id = fresh[i], AtTerminal = atTerminal, Index = i + 1, Total = fresh.Count });
            }
            HostNext();
        }

        /// <summary>
        /// The host left mid-vote and this PC took over: carry on the vote everyone was looking at, with the
        /// votes already cast. Shortcuts still queued behind it get asked again at the next terminal visit.
        /// </summary>
        private void TakeOverVote()
        {
            if (voteId != null || !showVote || string.IsNullOrEmpty(shownId) || IsDecided(shownId, out _))
            {
                return;
            }
            voteInfo = new Pending
            {
                Starter = new SteamId { Value = shownStarter },
                Id = shownId,
                AtTerminal = shownAtTerminal,
                AtVoteTerminal = shownAtVoteTerminal,
                Index = 1,
                Total = 1,
            };
            voteId = shownId;
            voteStarter = voteInfo.Starter;
            yes.Clear();
            no.Clear();
            yes.UnionWith(shownYesIds);
            no.UnionWith(shownNoIds);
            voteEnds = Mathf.Max(shownEnds, Time.unscaledTime + 5f);
            nextStateSend = 0f;
            Plugin.Log.LogInfo($"Took over as host: carrying on the shortcut vote ({yes.Count} yes, {no.Count} no): {voteId}");
        }

        private void CancelVote(string id)
        {
            queue.RemoveAll(p => p.Id == id);
            wantsOpen.Remove(id);
            earlyVotes.Remove(id);
            if (voteId == id)
            {
                voteId = null;
                HostNext();
            }
        }

        /// <summary>
        /// The local player walked or fell into a shortcut: build it here straight away, then have the
        /// host open it for everyone (same seed, so it's the same shortcut on every PC).
        /// </summary>
        internal void OpenNow(UT_BranchGenerator generator)
        {
            string id = ButtonSync.ObjectId(generator);
            generatorCache[id] = generator;
            Plugin.Log.LogInfo("Walked into a shortcut, opening it for the team: " + id);
            string me = NetSession.NameOf(NetSession.LocalId);
            // Our player is right here, so build it now whatever happens with the team's copy.
            Open(id, me, force: true);
            if (NetSession.Instance.IsHost)
            {
                CancelVote(id);
                HostSend(id, Outcome.Open, me);
            }
            else
            {
                Ask(new List<string> { id }, false);
            }
        }

        internal void OpenHere(UT_BranchGenerator generator)
        {
            string id = ButtonSync.ObjectId(generator);
            generatorCache[id] = generator;
            Open(id, NetSession.NameOf(NetSession.LocalId), force: true);
        }

        private void HostNext()
        {
            if (voteId != null || queue.Count == 0)
            {
                return;
            }
            Pending p = queue[0];
            queue.RemoveAt(0);
            voteInfo = p;
            voteId = p.Id;
            voteStarter = p.Starter;
            yes.Clear();
            no.Clear();
            if (!p.AtTerminal)
            {
                yes.Add(p.Starter.Value);
            }
            if (earlyVotes.TryGetValue(p.Id, out var early))
            {
                foreach (var v in early) (v.Value ? yes : no).Add(v.Key);
                earlyVotes.Remove(p.Id);
            }
            voteEnds = Time.unscaledTime + (p.AtVoteTerminal ? VoteTerminalSeconds : VoteSeconds);
            nextStateSend = 0f;
            HostCheck();
        }

        private string Title(Pending p)
        {
            string who = NetSession.NameOf(p.Starter);
            if (!p.AtTerminal)
            {
                return $"{who} found a shortcut. Which way does the team go?";
            }
            if (p.AtVoteTerminal)
            {
                return $"{who} started a shortcut vote. Vote at the VOTE terminal: shortcut or normal path?";
            }
            string which = p.Total > 1 ? $"Shortcut {p.Index} of {p.Total} ahead" : "There's a shortcut ahead";
            return $"{who} is at a terminal. {which}: which way does the team go?";
        }

        private void HostSend(string id, Outcome outcome, string starter)
        {
            NetSession.Instance.Send(PacketType.ShortcutResult, w =>
            {
                w.Write(id);
                w.Write((byte)outcome);
                w.Write(starter);
            });
            Finish(id, outcome, starter);
        }

        private void HostCount(SteamId voter, string id, bool inFavour)
        {
            if (!NetSession.Instance.IsHost || IsDecided(id, out _))
            {
                return;
            }
            if (voteId != id)
            {
                // A vote from a vote terminal: hold it until this shortcut's team vote starts, and start
                // one if nobody has yet.
                if (!earlyVotes.TryGetValue(id, out var early))
                {
                    earlyVotes[id] = early = new Dictionary<ulong, bool>();
                }
                early[voter.Value] = inFavour;
                if (!Waiting(id))
                {
                    queue.Add(new Pending { Starter = voter, Id = id, AtTerminal = true, AtVoteTerminal = true, Index = 1, Total = 1 });
                }
                HostNext();
                return;
            }
            yes.Remove(voter.Value);
            no.Remove(voter.Value);
            (inFavour ? yes : no).Add(voter.Value);
            nextStateSend = 0f;
            HostCheck();
        }

        private void HostCheck()
        {
            if (voteId == null)
            {
                return;
            }
            List<SteamId> voters = Voters();
            int votes = voters.Count(v => yes.Contains(v.Value) || no.Contains(v.Value));
            bool done = votes >= voters.Count || Time.unscaledTime >= voteEnds;
            if (!done)
            {
                if (Time.unscaledTime >= nextStateSend)
                {
                    nextStateSend = Time.unscaledTime + 1f;
                    float left = voteEnds - Time.unscaledTime;
                    string title = Title(voteInfo);
                    int y = voters.Count(v => yes.Contains(v.Value));
                    int n = voters.Count(v => no.Contains(v.Value));
                    string id = voteId;
                    bool sendAtTerminal = voteInfo.AtTerminal;
                    bool atVoteTerminal = voteInfo.AtVoteTerminal;
                    ulong starterId = voteStarter.Value;
                    var yesIds = new List<ulong>(yes);
                    var noIds = new List<ulong>(no);
                    NetSession.Instance.Send(PacketType.ShortcutVoteState, w =>
                    {
                        w.Write(id);
                        w.Write(title);
                        w.Write(y);
                        w.Write(n);
                        w.Write(voters.Count);
                        w.Write(left);
                        w.Write(sendAtTerminal);
                        w.Write(atVoteTerminal);
                        w.Write(starterId);
                        w.Write(yesIds.Count);
                        foreach (ulong v in yesIds) w.Write(v);
                        w.Write(noIds.Count);
                        foreach (ulong v in noIds) w.Write(v);
                    });
                    ShowState(id, title, left);
                }
                return;
            }
            int yesCount = voters.Count(v => yes.Contains(v.Value));
            int noCount = voters.Count(v => no.Contains(v.Value));
            bool passed = yesCount > noCount;
            string finished = voteId;
            string starter = NetSession.NameOf(voteStarter);
            bool atTerminal = voteInfo.AtTerminal;
            voteId = null;
            Plugin.Log.LogInfo($"Shortcut vote {(passed ? "passed" : "failed")} ({yesCount} yes, {noCount} no): {finished}");
            // Nobody voted at all (everyone missed it): decide nothing. The shortcut stays open to a vote
            // later, at the next terminal visit or when someone tries to open it.
            Outcome outcome = yesCount == 0 && noCount == 0 ? Outcome.NoVotes
                : !passed ? Outcome.Normal
                : (atTerminal && !wantsOpen.Contains(finished)) ? Outcome.Approved
                : Outcome.Open;
            wantsOpen.Remove(finished);
            HostSend(finished, outcome, starter);
            HostNext();
        }

        // ---------------------------------------------------------------- Everyone

        private void ReadState(System.IO.BinaryReader r)
        {
            string id = r.ReadString();
            string title = r.ReadString();
            r.ReadInt32(); // yes, no and voter counts (unused here, kept for the packet format)
            r.ReadInt32();
            r.ReadInt32();
            float left = r.ReadSingle();
            shownAtTerminal = r.ReadBoolean();
            shownAtVoteTerminal = r.ReadBoolean();
            shownStarter = r.ReadUInt64();
            shownYesIds.Clear();
            shownNoIds.Clear();
            int yc = r.ReadInt32();
            for (int i = 0; i < yc && i < 64; i++) shownYesIds.Add(r.ReadUInt64());
            int nc = r.ReadInt32();
            for (int i = 0; i < nc && i < 64; i++) shownNoIds.Add(r.ReadUInt64());
            ShowState(id, title, left);
        }

        private void ShowState(string id, string title, float left)
        {
            if (!showVote || shownId != id)
            {
                Plugin.Log.LogInfo($"Showing shortcut vote: {title} ({id})");
            }
            showVote = true;
            shownId = id;
            shownEnds = Time.unscaledTime + left;
        }

        private void Finish(string id, Outcome outcome, string starter)
        {
            if (shownId == id)
            {
                showVote = false;
                shownId = null;
            }
            if (outcome == Outcome.NoVotes)
            {
                return;
            }
            if (outcome == Outcome.Approved)
            {
                Approved.Add(id);
                UT_BranchGenerator approved = FindGenerator(id);
                if (approved != null) BlockNormalPath(approved);
                else lockLater[id] = false;
                return;
            }
            if (outcome == Outcome.Normal)
            {
                Opened.Add(id); // the vote is over: this shortcut stays shut for the run
                UT_BranchGenerator g = FindGenerator(id);
                if (g != null) BlockShortcut(g);
                else lockLater[id] = true;
                Approved.Remove(id);
                return;
            }
            Open(id, starter);
        }

        private static readonly Dictionary<string, UT_BranchGenerator> generatorCache = new Dictionary<string, UT_BranchGenerator>();

        private static UT_BranchGenerator FindGenerator(string id)
        {
            if (generatorCache.TryGetValue(id, out UT_BranchGenerator cached) && cached != null)
            {
                return cached;
            }
            UT_BranchGenerator found = FindGeneratorSlow(id);
            if (found != null)
            {
                generatorCache[id] = found;
            }
            return found;
        }

        private static UT_BranchGenerator FindGeneratorSlow(string id)
        {
            foreach (UT_BranchGenerator g in FindObjectsByType<UT_BranchGenerator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (g != null && ButtonSync.ObjectId(g) == id)
                {
                    return g;
                }
            }
            return null;
        }

        /// <summary>
        /// Decided shortcuts whose level this PC hasn't built yet: locked once it loads (true = the
        /// shortcut side, false = the normal path).
        /// </summary>
        private static readonly Dictionary<string, bool> lockLater = new Dictionary<string, bool>();

        private static void BlockShortcut(UT_BranchGenerator g)
        {
            if (!ShortcutDoors.LockSide(g, shortcutSide: true) && g.startTransform != null)
            {
                PlaceBarrier(g.startTransform, g.currentLevel, "shortcut");
            }
        }

        private void RetryLocks()
        {
            if (lockLater.Count == 0)
            {
                return;
            }
            foreach (var pair in new List<KeyValuePair<string, bool>>(lockLater))
            {
                generatorCache.Remove(pair.Key);
                UT_BranchGenerator g = FindGenerator(pair.Key);
                if (g == null)
                {
                    continue;
                }
                lockLater.Remove(pair.Key);
                if (pair.Value) BlockShortcut(g);
                else if (Approved.Contains(pair.Key)) BlockNormalPath(g);
            }
        }

        /// <summary>Generators whose normal path (the rest of their level) is already blocked.</summary>
        private static readonly HashSet<string> normalBlocked = new HashSet<string>();

        private static void BlockNormalPath(UT_BranchGenerator g)
        {
            M_Level from = g != null ? g.currentLevel : null;
            if (from == null || from.GetLevelExit() == null || !normalBlocked.Add(ButtonSync.ObjectId(g)))
            {
                return;
            }
            if (!ShortcutDoors.LockSide(g, shortcutSide: false))
            {
                PlaceBarrier(from.GetLevelExit(), from, "normal");
            }
        }

        private float nextPathCheck;

        /// <summary>
        /// Once the team has picked a shortcut that hasn't opened yet, nobody may carry on up the normal path:
        /// anyone who gets past the block into a later level of that branch is put back below it.
        /// </summary>
        private void KeepOffNormalPath()
        {
            if (Approved.Count == 0 || Time.unscaledTime < nextPathCheck || WorldLoader.instance == null)
            {
                return;
            }
            nextPathCheck = Time.unscaledTime + 0.5f;
            ENT_Player player = ENT_Player.playerObject;
            if (player == null || player.dead)
            {
                return;
            }
            WorldLoader.LevelInfo here = WorldLoader.GetClosestLevelToPosition(player.transform.position);
            if (here == null || here.GetLevel() == null)
            {
                return;
            }
            foreach (string id in Approved)
            {
                UT_BranchGenerator g = FindGenerator(id);
                M_Level from = g != null ? g.currentLevel : null;
                if (from == null || from.GetLevelExit() == null)
                {
                    continue;
                }
                WorldLoader.BranchInfo branch = WorldLoader.instance.GetBranchOfLevel(from);
                if (branch == null || branch.levelTracker == null || here.branch != branch)
                {
                    continue;
                }
                int fromIndex = branch.levelTracker.FindIndex(l => l != null && l.GetLevel() == from);
                int hereIndex = branch.levelTracker.IndexOf(here);
                if (fromIndex >= 0 && hereIndex > fromIndex)
                {
                    Transform exit = from.GetLevelExit();
                    player.Teleport(exit.position - Vector3.up * 2f);
                    Plugin.Log.LogInfo("Kept off the normal path (team picked the shortcut): " + id);
                    return;
                }
            }
        }

        /// <summary>
        /// Only for a way with nothing the game could keep shut (no door, hatch or lift): seals it with a
        /// plate that looks like the level around it. It's a solid slab laid across the spot's "up" (paths
        /// climb upward from entrances and exits), dressed in the material of the nearest wall, floor or
        /// ceiling, and parented to the level so it follows the world and goes away with it. Same on
        /// every PC.
        /// </summary>
        private static void PlaceBarrier(Transform at, M_Level level, string what)
        {
            if (at == null)
            {
                return;
            }
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Belay_Seal_" + what;
            wall.transform.SetParent(level != null ? level.transform : at, true);
            wall.transform.position = at.position;
            wall.transform.rotation = at.rotation;
            wall.transform.localScale = Vector3.one;
            Vector3 lossy = wall.transform.lossyScale;
            wall.transform.localScale = new Vector3(6f / Mathf.Max(0.01f, Mathf.Abs(lossy.x)), 0.5f / Mathf.Max(0.01f, Mathf.Abs(lossy.y)), 6f / Mathf.Max(0.01f, Mathf.Abs(lossy.z)));
            Renderer r = wall.GetComponent<Renderer>();
            Material look = LevelMaterialNear(at.position, wall);
            if (r != null)
            {
                if (look != null)
                {
                    var m = new Material(look);
                    // A cube's faces are 0-1 in UV: tile the texture so it keeps roughly the level's scale.
                    if (m.HasProperty("_MainTex")) m.mainTextureScale = new Vector2(3f, 3f);
                    r.sharedMaterial = m;
                }
                else
                {
                    r.enabled = false; // nothing to dress it in: still blocks, just isn't drawn
                }
            }
            Plugin.Log.LogInfo($"Sealed the {what} path at {at.name}" + (look != null ? $" ({look.name})" : " (no material found)"));
        }

        /// <summary>The opaque material of the closest level surface around a spot.</summary>
        private static Material LevelMaterialNear(Vector3 at, GameObject self)
        {
            Vector3[] dirs = { Vector3.left, Vector3.right, Vector3.forward, Vector3.back, Vector3.down, Vector3.up };
            float best = float.MaxValue;
            Material found = null;
            foreach (Vector3 d in dirs)
            {
                foreach (RaycastHit hit in Physics.RaycastAll(at, d, 12f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider == null || hit.collider.gameObject == self || hit.distance >= best) continue;
                    if (hit.collider.GetComponentInParent<ENT_Player>() != null || hit.collider.GetComponentInParent<Rigidbody>() != null) continue;
                    Renderer rend = hit.collider.GetComponent<Renderer>() ?? hit.collider.GetComponentInParent<Renderer>();
                    Material mat = rend != null ? rend.sharedMaterial : null;
                    if (mat == null || mat.renderQueue >= 2450) continue; // skip glass, decals and see-through bits
                    best = hit.distance;
                    found = mat;
                }
            }
            return found;
        }

        /// <summary>Opened branches this PC has actually built.</summary>
        internal static readonly HashSet<string> BuiltHere = new HashSet<string>();
        /// <summary>Opened by the team, but this PC's player isn't there yet.</summary>
        private static readonly Dictionary<string, string> deferred = new Dictionary<string, string>();

        /// <summary>Set while a branch is built, so the build is seeded from the shortcut, not from this PC's history.</summary>
        internal static int? BuildSeed;

        /// <summary>
        /// Whether to build a branch here now. Building one switches the map over to it, which hides the
        /// levels behind and (for the main way on, like the Nest entry) deletes them. That used to happen
        /// on every PC the moment anyone walked in, so teammates still on their way had the map removed
        /// under them and fell into the void. Now a PC builds it once its own player gets there (or is
        /// dead and watching a teammate who is).
        /// </summary>
        private static bool ReadyHere(UT_BranchGenerator g)
        {
            ENT_Player player = ENT_Player.playerObject;
            if (player == null || player.dead)
            {
                return true;
            }
            M_Level level = g.currentLevel != null ? g.currentLevel : g.GetComponentInParent<M_Level>();
            WorldLoader loader = WorldLoader.instance;
            M_Level current = loader != null ? loader.GetCurrentLevel()?.GetLevel() : null;
            // In the branch's own level (the game counts it as current and you're inside it), or right at
            // its trigger. Walking into the trigger builds it anyway; this is the fallback.
            if (level != null && current == level && WorldCatchUp.LevelAt(player.transform.position) == level)
            {
                return true;
            }
            return Vector3.Distance(player.transform.position, g.transform.position) < 12f;
        }

        private static bool IsMainWayOn(UT_BranchGenerator g) => g.deleteRootBranch || g.deleteRootLevelAfterTeleporting || g.teleportPlayerIntoBranch;

        private void Open(string id, string starter, bool force = false)
        {
            if (BuiltHere.Contains(id))
            {
                return;
            }
            Opened.Add(id);
            Approved.Remove(id);
            UT_BranchGenerator g = FindGenerator(id);
            if (g != null)
            {
                if (!force && !ReadyHere(g))
                {
                    if (!deferred.ContainsKey(id))
                    {
                        Plugin.Log.LogInfo("The team opened a branch ahead of you; it builds here when you get there: " + id);
                    }
                    deferred[id] = starter;
                    return;
                }
                deferred.Remove(id);
                BuiltHere.Add(id);
                if (!IsMainWayOn(g))
                {
                    // Block the normal way on, so nobody climbs into the part of the map the shortcut replaces.
                    BlockNormalPath(g);
                }
                Applying = true;
                BuildSeed = Seed(id);
                try
                {
                    Random.InitState(Seed(id));
                    g.Generate();
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Couldn't open the shortcut here ({id}): {e.Message}");
                }
                finally
                {
                    Applying = false;
                    BuildSeed = null;
                }
                Plugin.Log.LogInfo("Shortcut opened for the team: " + id);
                return;
            }
            if (!notLoadedYet.ContainsKey(id))
            {
                Plugin.Log.LogWarning("The team opened a shortcut that isn't loaded here yet (opens when it loads): " + id);
            }
            notLoadedYet[id] = starter;
        }

        /// <summary>Shortcuts the team opened before this PC had built that part of the map.</summary>
        private static readonly Dictionary<string, string> notLoadedYet = new Dictionary<string, string>();
        private float nextNotLoadedTry;

        private void RetryNotLoaded()
        {
            if ((notLoadedYet.Count == 0 && lockLater.Count == 0 && deferred.Count == 0) || Time.unscaledTime < nextNotLoadedTry)
            {
                return;
            }
            nextNotLoadedTry = Time.unscaledTime + 1f;
            RetryLocks();
            foreach (var pair in new List<KeyValuePair<string, string>>(deferred))
            {
                if (BuiltHere.Contains(pair.Key))
                {
                    deferred.Remove(pair.Key);
                    continue;
                }
                UT_BranchGenerator g = FindGenerator(pair.Key);
                if (g != null && ReadyHere(g))
                {
                    Plugin.Log.LogInfo("Reached a branch the team opened, building it here: " + pair.Key);
                    Open(pair.Key, pair.Value, force: true);
                }
            }
            foreach (var pair in new List<KeyValuePair<string, string>>(notLoadedYet))
            {
                if (BuiltHere.Contains(pair.Key) || deferred.ContainsKey(pair.Key))
                {
                    notLoadedYet.Remove(pair.Key);
                    continue;
                }
                generatorCache.Remove(pair.Key);
                if (FindGenerator(pair.Key) != null)
                {
                    notLoadedYet.Remove(pair.Key);
                    Plugin.Log.LogInfo("Caught up on a shortcut the team opened: " + pair.Key);
                    Open(pair.Key, pair.Value);
                }
            }
        }

        private float nextReportTry;

        private void Update()
        {
            if (Time.unscaledTime >= nextReportTry)
            {
                nextReportTry = Time.unscaledTime + 5f;
                try
                {
                    ShortcutDoors.WriteReportOnce();
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning("Shortcut report failed: " + e.Message);
                    nextReportTry = float.MaxValue;
                }
            }
            NetSession net = NetSession.Instance;
            if (net == null || !net.InLobby)
            {
                showVote = false;
                voteId = null;
                queue.Clear();
                return;
            }
            if (runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                Opened.Clear();
                BuiltHere.Clear();
                deferred.Clear();
                ShortcutDoors.Clear();
                Approved.Clear();
                normalBlocked.Clear();
                notLoadedYet.Clear();
                lockLater.Clear();
                wantsOpen.Clear();
                queue.Clear();
                earlyVotes.Clear();
                showVote = false;
                voteId = null;
            }
            if (net.IsHost && lastHost.Value != 0 && lastHost != net.HostId)
            {
                TakeOverVote();
            }
            lastHost = net.HostId;
            if (net.IsHost)
            {
                HostCheck();
            }
            if (showVote && Time.unscaledTime > shownEnds + 5f)
            {
                showVote = false; // the host went quiet
            }
            ShortcutDoors.Tick();
            KeepOffNormalPath();
            RetryNotLoaded();
        }

        // No on-screen vote overlay: voting only happens at the SHORTCUT VOTE terminal.

        internal static int Seed(string id)
        {
            unchecked
            {
                int h = NetSession.Instance.Seed * 31 + 0x5C;
                foreach (char c in id)
                {
                    h = h * 31 + c;
                }
                return h;
            }
        }
    }

    [HarmonyPatch(typeof(UT_BranchGenerator), nameof(UT_BranchGenerator.Generate))]
    internal static class ShortcutSyncPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(UT_BranchGenerator __instance)
        {
            if (!ShortcutSync.Live || WorldLoader.instance == null || ShortcutSync.Instance == null)
            {
                return true;
            }
            if (ShortcutSync.Applying)
            {
                return true;
            }
            string id = ButtonSync.ObjectId(__instance);
            if (ShortcutSync.BuiltHere.Contains(id))
            {
                // Already built here: don't build a second branch.
                return false;
            }
            if (ShortcutSync.Opened.Contains(id))
            {
                // The team opened it and our player just got here: build our copy.
                ShortcutSync.Instance.OpenHere(__instance);
                return false;
            }
            ShortcutSync.Instance.OpenNow(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(OS_Manager), nameof(OS_Manager.Activate))]
    internal static class ShortcutTerminalPatch
    {
        [HarmonyPostfix]
        private static void Postfix(OS_Manager __instance)
        {
            if (!ShortcutSync.Live || ShortcutSync.Instance == null || WorldLoader.instance == null || ENT_Player.playerObject == null
                || TerminalGuard.ReplayingTeammate)
            {
                return;
            }
            try
            {
                if (VoteTerminal.Is(__instance))
                {
                    VoteTerminal.Instance?.OnSatDown(__instance);
                }

            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't check this terminal's shortcuts: " + e.Message);
            }
        }
    }
}
