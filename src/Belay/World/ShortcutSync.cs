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

        internal static bool Applying;

        /// <summary>Generators that have opened (or are opening) this run, or were voted down.</summary>
        internal static readonly HashSet<string> Opened = new HashSet<string>();
        /// <summary>Shortcuts the team voted to take at a terminal, not opened yet.</summary>
        internal static readonly HashSet<string> Approved = new HashSet<string>();

        private const int TerminalLookAhead = 12;

        internal enum Outcome : byte { Normal = 0, Open = 1, Approved = 2 }

        private struct Pending
        {
            public SteamId Starter;
            public string Id;
            public bool AtTerminal;
            public int Index, Total;
        }

        // Host: the running vote and the ones waiting behind it.
        private string voteId;
        private SteamId voteStarter;
        private Pending voteInfo;
        private readonly List<Pending> queue = new List<Pending>();
        /// <summary>Someone tried to open these while their vote was still running or queued.</summary>
        private readonly HashSet<string> wantsOpen = new HashSet<string>();
        private readonly HashSet<ulong> yes = new HashSet<ulong>();
        private readonly HashSet<ulong> no = new HashSet<ulong>();
        private float voteEnds;
        private float nextStateSend;

        // Everyone: what to draw.
        private bool showVote;
        private string shownId;
        private string shownTitle;
        private int shownYes, shownNo, shownVoters;
        private float shownEnds;
        private bool myVoteSent;
        private int runId;
        private string myRequestId;
        private Vector3 myRequestPos;

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

        /// <summary>The local player opened a shortcut: ask the team instead of opening it.</summary>
        internal void RequestVote(UT_BranchGenerator generator)
        {
            string id = ButtonSync.ObjectId(generator);
            if (showVote && shownId == id)
            {
                return;
            }
            Plugin.Log.LogInfo((Approved.Contains(id) ? "Opening a shortcut the team picked: " : "Asking the team to take a shortcut: ") + id);
            // Some shortcuts start when you walk into them. If the team picks the normal path, you're
            // put back where you were when you asked, on the right side of the barrier.
            myRequestId = id;
            myRequestPos = WorldSpace.ToShared(ENT_Player.playerObject.transform.position);
            Ask(new List<string> { id }, false);
        }

        /// <summary>The local player sat at a terminal: vote on the shortcuts it covers.</summary>
        internal void OnTerminal(OS_Manager os)
        {
            List<string> ids = ShortcutsAfter(os);
            if (ids.Count == 0)
            {
                return;
            }
            Plugin.Log.LogInfo($"Terminal covers {ids.Count} shortcut(s): {string.Join(", ", ids)}");
            Ask(ids, true);
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

        /// <summary>
        /// The shortcuts a terminal decides: every one from the terminal's level up to (not including)
        /// the next level with a terminal of its own, along the same branch of the map.
        /// </summary>
        private static List<string> ShortcutsAfter(OS_Manager os)
        {
            var ids = new List<string>();
            WorldLoader loader = WorldLoader.instance;
            if (loader == null)
            {
                return ids;
            }
            M_Level level = null;
            if (os.worldInterface != null)
            {
                level = os.worldInterface.GetComponentInParent<M_Level>(true);
            }
            if (level == null)
            {
                level = os.GetComponentInParent<M_Level>(true);
            }
            if (level == null && ENT_Player.playerObject != null)
            {
                level = WorldLoader.GetClosestLevelToPosition(ENT_Player.playerObject.transform.position)?.GetLevel();
            }
            if (level == null)
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
                if (i > start && l.GetComponentsInChildren<OS_Computer_Interface>(true).Length > 0)
                {
                    break; // the next terminal votes on its own shortcuts
                }
                foreach (UT_BranchGenerator g in l.GetComponentsInChildren<UT_BranchGenerator>(true))
                {
                    string id = ButtonSync.ObjectId(g);
                    if (!Opened.Contains(id) && !Approved.Contains(id) && !ids.Contains(id))
                    {
                        ids.Add(id);
                    }
                }
            }
            return ids;
        }

        private List<SteamId> Voters()
        {
            return NetSession.Instance.MemberIds();
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
                    if (Approved.Contains(id))
                    {
                        HostSend(id, Outcome.Open, NetSession.NameOf(starter));
                        continue;
                    }
                    if (Waiting(id))
                    {
                        wantsOpen.Add(id); // open it as soon as its vote picks the shortcut
                        continue;
                    }
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
            voteEnds = Time.unscaledTime + VoteSeconds;
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
            if (!NetSession.Instance.IsHost || voteId != id)
            {
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
                    NetSession.Instance.Send(PacketType.ShortcutVoteState, w =>
                    {
                        w.Write(id);
                        w.Write(title);
                        w.Write(y);
                        w.Write(n);
                        w.Write(voters.Count);
                        w.Write(left);
                    });
                    ShowState(id, title, y, n, voters.Count, left);
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
            Outcome outcome = !passed ? Outcome.Normal
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
            int y = r.ReadInt32();
            int n = r.ReadInt32();
            int voters = r.ReadInt32();
            float left = r.ReadSingle();
            ShowState(id, title, y, n, voters, left);
        }

        private void ShowState(string id, string title, int y, int n, int voters, float left)
        {
            if (!showVote || shownId != id)
            {
                myVoteSent = false;
            }
            showVote = true;
            shownId = id;
            shownTitle = title;
            shownYes = y;
            shownNo = n;
            shownVoters = voters;
            shownEnds = Time.unscaledTime + left;
        }

        private void Finish(string id, Outcome outcome, string starter)
        {
            if (shownId == id)
            {
                showVote = false;
                shownId = null;
            }
            if (outcome == Outcome.Approved)
            {
                Approved.Add(id);
                Run.RunFlow.Instance?.ShowNotice("The team picked the shortcut. It opens for everyone when someone reaches it.", 6f);
                return;
            }
            if (outcome == Outcome.Normal)
            {
                Opened.Add(id); // the vote is over: this shortcut stays shut for the run
                UT_BranchGenerator g = FindGenerator(id);
                if (g != null && g.startTransform != null)
                {
                    PlaceBarrier(g.startTransform, g.currentLevel, "shortcut");
                }
                Approved.Remove(id);
                if (myRequestId == id && ENT_Player.playerObject != null && !ENT_Player.playerObject.dead)
                {
                    ENT_Player.playerObject.Teleport(WorldSpace.ToLocal(myRequestPos));
                }
                myRequestId = null;
                Run.RunFlow.Instance?.ShowNotice("The team picked the normal path. The shortcut is blocked off: head back and carry on the normal way.", 6f);
                return;
            }
            Open(id, starter);
        }

        private static UT_BranchGenerator FindGenerator(string id)
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

        private static Material barrierMaterial;

        /// <summary>
        /// A solid, see-through red slab across a path, the same on every PC. It's laid across the
        /// spot's "up" (paths climb upward from entrances and exits) and parented to the level so it
        /// follows the world and goes away with it.
        /// </summary>
        private static void PlaceBarrier(Transform at, M_Level level, string what)
        {
            if (at == null)
            {
                return;
            }
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Belay_Barrier_" + what;
            wall.transform.SetParent(level != null ? level.transform : at, true);
            wall.transform.position = at.position;
            wall.transform.rotation = at.rotation;
            wall.transform.localScale = Vector3.one;
            Vector3 lossy = wall.transform.lossyScale;
            wall.transform.localScale = new Vector3(6f / Mathf.Max(0.01f, Mathf.Abs(lossy.x)), 0.4f / Mathf.Max(0.01f, Mathf.Abs(lossy.y)), 6f / Mathf.Max(0.01f, Mathf.Abs(lossy.z)));
            if (barrierMaterial == null)
            {
                barrierMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.9f, 0.15f, 0.1f, 0.45f) };
            }
            Renderer r = wall.GetComponent<Renderer>();
            if (r != null)
            {
                r.sharedMaterial = barrierMaterial;
            }
            Plugin.Log.LogInfo($"Blocked the {what} path at {at.name}");
        }

        private void Open(string id, string starter)
        {
            if (Opened.Contains(id))
            {
                return;
            }
            UT_BranchGenerator g = FindGenerator(id);
            if (g != null)
            {
                Opened.Add(id);
                Approved.Remove(id);
                // Block the normal way on, so nobody climbs into the part of the map the shortcut replaces.
                M_Level from = g.currentLevel;
                if (from != null && from.GetLevelExit() != null)
                {
                    PlaceBarrier(from.GetLevelExit(), from, "normal");
                }
                Applying = true;
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
                }
                Plugin.Log.LogInfo("Shortcut opened for the team: " + id);
                Run.RunFlow.Instance?.ShowNotice($"Shortcut opened by {starter}. The normal path ahead is closed: follow it.", 8f);
                return;
            }
            Plugin.Log.LogWarning("The team opened a shortcut that isn't loaded here: " + id);
        }

        private void SendVote(bool inFavour)
        {
            if (!showVote || myVoteSent)
            {
                return;
            }
            myVoteSent = true;
            string id = shownId;
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
            Run.RunFlow.Instance?.ShowNotice(inFavour ? "You voted for the shortcut." : "You voted for the normal path.", 3f);
        }

        private void Update()
        {
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
                Approved.Clear();
                wantsOpen.Clear();
                queue.Clear();
                showVote = false;
                voteId = null;
            }
            if (net.IsHost)
            {
                HostCheck();
            }
            if (showVote && !myVoteSent)
            {
                if (Input.GetKeyDown(Plugin.VoteYesKey.Value)) SendVote(true);
                else if (Input.GetKeyDown(Plugin.VoteNoKey.Value)) SendVote(false);
            }
            if (showVote && Time.unscaledTime > shownEnds + 5f)
            {
                showVote = false; // the host went quiet
            }
        }

        private void OnGUI()
        {
            if (!showVote)
            {
                return;
            }
            float left = Mathf.Max(0f, shownEnds - Time.unscaledTime);
            string line1 = shownTitle;
            string line2 = myVoteSent
                ? $"Shortcut {shownYes}   Normal {shownNo}   of {shownVoters}   ({Mathf.CeilToInt(left)}s)"
                : $"[{Plugin.VoteYesKey.Value}] Shortcut   [{Plugin.VoteNoKey.Value}] Normal path      Shortcut {shownYes}   Normal {shownNo}   of {shownVoters}   ({Mathf.CeilToInt(left)}s)";
            var rect = new Rect(Screen.width / 2f - 360f, 110f, 720f, 58f);
            GUI.Box(rect, "");
            var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
            GUI.Label(new Rect(rect.x, rect.y + 4f, rect.width, 24f), line1, style);
            GUI.Label(new Rect(rect.x, rect.y + 28f, rect.width, 24f), line2, style);
        }

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
            if (ShortcutSync.Opened.Contains(ButtonSync.ObjectId(__instance)))
            {
                // Already opened for the team: don't build a second branch.
                return false;
            }
            ShortcutSync.Instance.RequestVote(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(OS_Manager), nameof(OS_Manager.Activate))]
    internal static class ShortcutTerminalPatch
    {
        [HarmonyPostfix]
        private static void Postfix(OS_Manager __instance)
        {
            if (!ShortcutSync.Live || ShortcutSync.Instance == null || WorldLoader.instance == null || ENT_Player.playerObject == null)
            {
                return;
            }
            try
            {
                ShortcutSync.Instance.OnTerminal(__instance);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't check this terminal's shortcuts: " + e.Message);
            }
        }
    }
}
