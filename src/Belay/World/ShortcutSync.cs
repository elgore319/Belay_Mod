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
    /// In a lobby, opening a shortcut now starts a team vote (run by the host). Everyone sees who
    /// wants to take it and votes Yes or No. When the vote passes, the shortcut opens on every PC at
    /// once and builds the same branch (seeded from the lobby). When it fails, nothing opens and the
    /// team carries on along the normal path.
    /// Rules: the opener counts as Yes. The vote ends when everyone has voted or after VoteSeconds.
    /// It passes with more Yes than No; anyone who doesn't vote doesn't count against it.
    /// </summary>
    public class ShortcutSync : MonoBehaviour
    {
        public static ShortcutSync Instance { get; private set; }

        private const float VoteSeconds = 20f;

        internal static bool Applying;

        /// <summary>Generators that have opened (or are opening) this run.</summary>
        internal static readonly HashSet<string> Opened = new HashSet<string>();

        // Host: the running vote.
        private string voteId;
        private SteamId voteStarter;
        private readonly HashSet<ulong> yes = new HashSet<ulong>();
        private readonly HashSet<ulong> no = new HashSet<ulong>();
        private float voteEnds;
        private float nextStateSend;

        // Everyone: what to draw.
        private bool showVote;
        private string shownId;
        private string shownStarter;
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
            net.On(PacketType.ShortcutVoteRequest, (origin, r) => HostStartVote(origin, r.ReadString()));
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
                bool passed = r.ReadBoolean();
                string starter = r.ReadString();
                Finish(id, passed, starter);
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
            Plugin.Log.LogInfo("Asking the team to take a shortcut: " + id);
            // Some shortcuts start when you walk into them. If the team picks the normal path, you're
            // put back where you were when you asked, on the right side of the barrier.
            myRequestId = id;
            myRequestPos = WorldSpace.ToShared(ENT_Player.playerObject.transform.position);
            NetSession net = NetSession.Instance;
            if (net.IsHost)
            {
                HostStartVote(NetSession.LocalId, id);
            }
            else
            {
                net.Send(PacketType.ShortcutVoteRequest, w => w.Write(id));
            }
        }

        private List<SteamId> Voters()
        {
            return NetSession.Instance.MemberIds();
        }

        private void HostStartVote(SteamId starter, string id)
        {
            if (!NetSession.Instance.IsHost || Opened.Contains(id))
            {
                return;
            }
            if (voteId != null)
            {
                if (voteId != id)
                {
                    Plugin.Log.LogInfo("Ignoring a second shortcut vote while one is running: " + id);
                }
                return;
            }
            voteId = id;
            voteStarter = starter;
            yes.Clear();
            no.Clear();
            yes.Add(starter.Value);
            voteEnds = Time.unscaledTime + VoteSeconds;
            nextStateSend = 0f;
            HostCheck();
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
                    string starterName = NetSession.NameOf(voteStarter);
                    int y = voters.Count(v => yes.Contains(v.Value));
                    int n = voters.Count(v => no.Contains(v.Value));
                    string id = voteId;
                    NetSession.Instance.Send(PacketType.ShortcutVoteState, w =>
                    {
                        w.Write(id);
                        w.Write(starterName);
                        w.Write(y);
                        w.Write(n);
                        w.Write(voters.Count);
                        w.Write(left);
                    });
                    ShowState(id, starterName, y, n, voters.Count, left);
                }
                return;
            }
            int yesCount = voters.Count(v => yes.Contains(v.Value));
            int noCount = voters.Count(v => no.Contains(v.Value));
            bool passed = yesCount > noCount;
            string finished = voteId;
            string starter = NetSession.NameOf(voteStarter);
            voteId = null;
            Plugin.Log.LogInfo($"Shortcut vote {(passed ? "passed" : "failed")} ({yesCount} yes, {noCount} no): {finished}");
            NetSession.Instance.Send(PacketType.ShortcutResult, w =>
            {
                w.Write(finished);
                w.Write(passed);
                w.Write(starter);
            });
            Finish(finished, passed, starter);
        }

        // ---------------------------------------------------------------- Everyone

        private void ReadState(System.IO.BinaryReader r)
        {
            string id = r.ReadString();
            string starter = r.ReadString();
            int y = r.ReadInt32();
            int n = r.ReadInt32();
            int voters = r.ReadInt32();
            float left = r.ReadSingle();
            ShowState(id, starter, y, n, voters, left);
        }

        private void ShowState(string id, string starter, int y, int n, int voters, float left)
        {
            if (!showVote || shownId != id)
            {
                myVoteSent = false;
            }
            showVote = true;
            shownId = id;
            shownStarter = starter;
            shownYes = y;
            shownNo = n;
            shownVoters = voters;
            shownEnds = Time.unscaledTime + left;
        }

        private void Finish(string id, bool passed, string starter)
        {
            showVote = false;
            shownId = null;
            if (!passed)
            {
                Opened.Add(id); // the vote is over: this shortcut stays shut for the run
                UT_BranchGenerator g = FindGenerator(id);
                if (g != null && g.startTransform != null)
                {
                    PlaceBarrier(g.startTransform, g.currentLevel, "shortcut");
                }
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
                Run.RunFlow.Instance?.ShowNotice($"Shortcut opened ({starter}'s vote passed). The normal path ahead is closed: follow it.", 8f);
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
                return;
            }
            if (runId != net.CurrentRun.RunId)
            {
                runId = net.CurrentRun.RunId;
                Opened.Clear();
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
            string line1 = $"{shownStarter} found a shortcut. Which way does the team go?";
            string line2 = myVoteSent
                ? $"Shortcut {shownYes}   Normal {shownNo}   of {shownVoters}   ({Mathf.CeilToInt(left)}s)"
                : $"[{Plugin.VoteYesKey.Value}] Shortcut   [{Plugin.VoteNoKey.Value}] Normal path      Shortcut {shownYes}   Normal {shownNo}   of {shownVoters}   ({Mathf.CeilToInt(left)}s)";
            var rect = new Rect(Screen.width / 2f - 330f, 110f, 660f, 58f);
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
}
