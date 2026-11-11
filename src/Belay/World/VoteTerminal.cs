using System.Collections;
using System.Collections.Generic;
using Belay.Net;
using UnityEngine;

namespace Belay.World
{
    /// <summary>Marks a copy of a game terminal that Belay placed for shortcut voting.</summary>
    public class VoteTerminalMarker : MonoBehaviour
    {
        public M_Level Level;
    }

    /// <summary>
    /// A terminal used only for shortcut votes. Wherever a level has a game terminal and there are
    /// shortcuts between it and the next terminal, Belay places a copy of that terminal beside it, with
    /// an empty desktop and a "VOTE" sign. Sitting down at it (the game's normal terminal controls) shows
    /// one on-screen choice per shortcut ahead: Shortcut or Normal path. The first choice starts the team
    /// vote, everyone sees the banner, and anyone can vote at their own copy of the terminal (or with the
    /// vote keys). Results, barriers and seeded shortcut generation are unchanged (see ShortcutSync).
    /// The game's own terminals never start votes. If there's no clear spot beside the terminal, the
    /// copy goes in the least blocked one.
    /// </summary>
    public class VoteTerminal : MonoBehaviour
    {
        public static VoteTerminal Instance { get; private set; }

        private const float ScanInterval = 2f;
        private const float LabelDistance = 14f;

        private static readonly Dictionary<M_Level, VoteTerminalMarker> placed = new Dictionary<M_Level, VoteTerminalMarker>();
        private static OS_DiskData emptyDisk;
        private float nextScan;
        private int runId;

        private void Awake()
        {
            Instance = this;
        }

        internal static bool Is(OS_Manager os)
        {
            return os != null && os.worldInterface != null && os.worldInterface.GetComponentInParent<VoteTerminalMarker>() != null;
        }

        internal static bool HasFor(M_Level level)
        {
            return level != null && placed.TryGetValue(level, out var m) && m != null;
        }

        /// <summary>The level's own game terminal (never one of Belay's copies), or null.</summary>
        internal static OS_Computer_Interface GameTerminalIn(M_Level level)
        {
            foreach (OS_Computer_Interface t in level.GetComponentsInChildren<OS_Computer_Interface>(true))
            {
                if (t != null && t.GetComponentInParent<VoteTerminalMarker>() == null)
                {
                    return t;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- Placing

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
                placed.Clear();
            }
            if (!ShortcutSync.Live || Time.unscaledTime < nextScan || WorldLoader.instance == null)
            {
                return;
            }
            nextScan = Time.unscaledTime + ScanInterval;
            try
            {
                Scan();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Vote terminal scan failed: " + e.Message);
            }
        }

        private void Scan()
        {
            WorldLoader.BranchInfo branch = WorldLoader.instance.GetCurrentBranch();
            if (branch == null || branch.levelTracker == null)
            {
                return;
            }
            foreach (WorldLoader.LevelInfo info in branch.levelTracker)
            {
                M_Level level = info?.GetLevel();
                if (level == null || !level.gameObject.activeInHierarchy|| HasFor(level))
                {
                    continue; // placing needs the level's colliders, so only levels that are loaded
                }
                OS_Computer_Interface anchor = GameTerminalIn(level);
                if (anchor == null || ShortcutSync.ShortcutsAfter(level, true).Count == 0)
                {
                    continue;
                }
                Place(level, anchor);
            }
        }

        /// <summary>The whole terminal prop (desk, screen, keyboard), not just its computer part.</summary>
        private static Transform PropRoot(OS_Computer_Interface anchor, M_Level level)
        {
            Transform root = anchor.transform;
            for (Transform t = anchor.transform; t != null && t != level.transform; t = t.parent)
            {
                if (t.name.StartsWith("Prop_"))
                {
                    root = t;
                }
            }
            return root;
        }

        /// <summary>The prop's solid footprint (its non-trigger colliders), or its renderers if it has none.</summary>
        private static Bounds BoundsOf(Transform root)
        {
            bool any = false;
            var b = new Bounds(root.position, Vector3.zero);
            foreach (Collider c in root.GetComponentsInChildren<Collider>())
            {
                if (c.isTrigger || !c.enabled) continue;
                if (!any) { b = c.bounds; any = true; }
                else b.Encapsulate(c.bounds);
            }
            if (!any)
            {
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
                {
                    if (!any) { b = r.bounds; any = true; }
                    else b.Encapsulate(r.bounds);
                }
            }
            if (!any || b.size.sqrMagnitude < 0.01f) b = new Bounds(root.position + Vector3.up, new Vector3(1.2f, 2f, 1f));
            return b;
        }

        private struct Spot
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Shift; // how far the prop's footprint moves (rotation about the stand spot aside)
            public string What;
        }

        private void Place(M_Level level, OS_Computer_Interface anchor)
        {
            Transform root = PropRoot(anchor, level);
            Bounds bounds = BoundsOf(root);
            // "Right" as seen by someone standing at the terminal and looking at it.
            Vector3 right = Vector3.ProjectOnPlane(root.right, Vector3.up).normalized;
            Transform stand = anchor.playerPosition;
            if (stand != null)
            {
                Vector3 facing = Vector3.ProjectOnPlane(bounds.center - stand.position, Vector3.up);
                if (facing.sqrMagnitude > 0.01f)
                {
                    right = Vector3.Cross(Vector3.up, facing.normalized);
                }
            }
            if (right.sqrMagnitude < 0.01f) right = Vector3.right;
            float width = Mathf.Clamp(Mathf.Abs(bounds.size.x * right.x) + Mathf.Abs(bounds.size.z * right.z), 0.8f, 3f);
            var spots = new List<Spot>();
            foreach (float k in new[] { 1f, 1.3f, 1.6f, -1f, -1.3f, -1.6f, 2.2f, -2.2f })
            {
                Vector3 shift = right * (width + 0.25f) * k;
                spots.Add(new Spot { Position = root.position + shift, Rotation = root.rotation, Shift = shift, What = k > 0 ? "right of" : "left of" });
            }
            var own = new HashSet<Collider>(root.GetComponentsInChildren<Collider>());
            Spot best = spots[0];
            int bestHits = int.MaxValue;
            foreach (Spot spot in spots)
            {
                int hits = Blockers(bounds, spot.Shift, own);
                if (hits < bestHits)
                {
                    best = spot;
                    bestHits = hits;
                }
                if (hits == 0)
                {
                    break;
                }
            }
            GameObject copy = Instantiate(root.gameObject, best.Position, best.Rotation, root.parent);
            copy.name = "Belay_VoteTerminal";
            var marker = copy.AddComponent<VoteTerminalMarker>();
            marker.Level = level;
            foreach (OS_Computer_Interface t in copy.GetComponentsInChildren<OS_Computer_Interface>(true))
            {
                t.computerID = "";
                t.desktopData = EmptyDisk();
            }
            placed[level] = marker;
            Plugin.Log.LogInfo($"Placed a vote terminal {best.What} {root.name} in {level.name} at {best.Position}" + (bestHits > 0 ? $" (touching {bestHits} things: no clear spot)" : ""));
        }

        /// <summary>How many solid things a copy of the prop moved by shift would overlap, plus one if it has no floor.</summary>
        private static int Blockers(Bounds bounds, Vector3 shift, HashSet<Collider> own)
        {
            int hits = 0;
            Vector3 center = bounds.center + shift + Vector3.up * 0.15f;
            Vector3 half = new Vector3(bounds.extents.x * 0.8f, Mathf.Max(0.1f, bounds.extents.y - 0.25f), bounds.extents.z * 0.8f);
            foreach (Collider c in Physics.OverlapBox(center, half, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c == null || own.Contains(c) || c.attachedRigidbody != null)
                {
                    continue; // loose props get pushed aside
                }
                if (ENT_Player.playerObject != null && c.transform.IsChildOf(ENT_Player.playerObject.transform))
                {
                    continue;
                }
                if (c.GetComponentInParent<Players.RemoteBody>() != null || c.GetComponentInParent<Players.RemotePlayer>() != null)
                {
                    continue; // teammates and bots move
                }
                hits++;
            }
            Vector3 foot = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z) + shift;
            if (!Physics.Raycast(foot + Vector3.up * 0.5f, Vector3.down, 1.5f, ~0, QueryTriggerInteraction.Ignore))
            {
                hits++;
            }
            return hits;
        }

        private static OS_DiskData EmptyDisk()
        {
            if (emptyDisk == null)
            {
                emptyDisk = ScriptableObject.CreateInstance<OS_DiskData>();
                emptyDisk.name = "Belay Vote";
                emptyDisk.diskName = "VOTE";
                emptyDisk.id = "belay-vote";
                emptyDisk.type = OS_DiskData.DiskDataType.standard;
                emptyDisk.files = new List<OS_Filesystem.FileInfo>();
                emptyDisk.diskDataSelectors = new List<OS_DiskData.DiskDataSelector>();
                emptyDisk.randomDisks = new List<OS_DiskData>();
            }
            return emptyDisk;
        }

        // ---------------------------------------------------------------- Voting

        internal void OnSatDown(OS_Manager os)
        {
            StartCoroutine(ShowChoices(os));
        }

        private IEnumerator ShowChoices(OS_Manager os)
        {
            yield return new WaitForSecondsRealtime(0.6f);
            if (os == null || os.messageManager == null || !os.canControl)
            {
                yield break;
            }
            VoteTerminalMarker marker = os.worldInterface.GetComponentInParent<VoteTerminalMarker>();
            List<string> all = ShortcutSync.ShortcutsAfter(marker != null ? marker.Level : ShortcutSync.LevelOf(os), true);
            var open = all.FindAll(id => !ShortcutSync.IsDecided(id, out _));
            if (open.Count == 0)
            {
                string text = all.Count == 0
                    ? "SHORTCUT VOTE\n\nThere's no shortcut ahead to vote on yet."
                    : "SHORTCUT VOTE\n\nEvery shortcut ahead is decided:\n" + Decisions(all);
                Show(os, text, "OK", null, null, null);
                yield break;
            }
            Ask(os, open, 0);
        }

        private static string Decisions(List<string> ids)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                ShortcutSync.IsDecided(ids[i], out bool shortcut);
                sb.Append($"Shortcut {i + 1}: {(shortcut ? "team takes the shortcut" : "team takes the normal path")}\n");
            }
            return sb.ToString();
        }

        private void Ask(OS_Manager os, List<string> ids, int index)
        {
            if (os == null || !os.canControl)
            {
                return;
            }
            if (index >= ids.Count)
            {
                Show(os, "SHORTCUT VOTE\n\nVote sent. The result shows at the top of everyone's screen.", "OK", null, null, null);
                return;
            }
            string id = ids[index];
            string which = ids.Count > 1 ? $"Shortcut {index + 1} of {ids.Count} ahead." : "There's a shortcut ahead.";
            Show(os, $"SHORTCUT VOTE\n\n{which}\nWhich way should the team go?",
                "Shortcut", () => { ShortcutSync.Instance?.CastVote(id, true); Ask(os, ids, index + 1); },
                "Normal path", () => { ShortcutSync.Instance?.CastVote(id, false); Ask(os, ids, index + 1); });
        }

        private static void Show(OS_Manager os, string text, string main, System.Action onMain, string other, System.Action onOther)
        {
            try
            {
                var packet = new Message_Manager.Message_Packet
                {
                    type = "default",
                    message = text,
                    closeText = main,
                    closeFunction = onMain,
                    screenPos = Vector2.zero,
                };
                if (onOther != null)
                {
                    packet.aText = other;
                    packet.optionAFunction = onOther;
                }
                os.messageManager.CreateMessage(packet);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't show the vote on the terminal: " + e.Message);
                Run.RunFlow.Instance?.ShowNotice("Couldn't show the vote on this terminal. Use the vote keys instead.", 5f);
            }
        }

        // ---------------------------------------------------------------- Sign

        private void OnGUI()
        {
            ENT_Player player = ENT_Player.playerObject;
            Camera cam = Camera.main;
            if (player == null || cam == null || OS_Manager.inUse || placed.Count == 0)
            {
                return;
            }
            var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold };
            style.normal.textColor = new Color(1f, 0.85f, 0.3f);
            foreach (VoteTerminalMarker m in placed.Values)
            {
                if (m == null || !m.gameObject.activeInHierarchy)
                {
                    continue;
                }
                Vector3 top = m.transform.position + Vector3.up * 2.2f;
                if (Vector3.Distance(player.transform.position, top) > LabelDistance)
                {
                    continue;
                }
                Vector3 sp = cam.WorldToScreenPoint(top);
                if (sp.z <= 0f)
                {
                    continue;
                }
                GUI.Label(new Rect(sp.x - 120f, Screen.height - sp.y - 15f, 240f, 30f), "SHORTCUT VOTE", style);
            }
        }
    }
}
