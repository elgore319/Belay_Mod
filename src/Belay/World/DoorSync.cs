using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Shared doors. Opening opens for everyone. A closing door is held open until every living player has passed
    /// (walked through its trigger or climbed well above), then shuts for everyone after the host's close delay
    /// (1 s by default). Doors closed by their own trigger zone (blast doors) are held the same way.
    /// </summary>
    public class DoorSync : MonoBehaviour
    {
        /// <summary>Seconds a door waits after the last living player passed before it shuts.</summary>
        public const float DefaultCloseDelay = 1f;
        public const float MinCloseDelay = 0f;
        public const float MaxCloseDelay = 10f;
        /// <summary>Marks a held door whose grace hasn't started yet (someone is still behind it).</summary>
        private const float Waiting = float.MaxValue;

        public static DoorSync Instance { get; private set; }

        /// <summary>Set while Belay itself drives a door, so the patches let the call through.</summary>
        internal static bool Applying;
        /// <summary>Set while a door runs its own Start/FixedUpdate logic, which every client does by itself.</summary>
        internal static bool InDoorSelfUpdate;

        /// <summary>
        /// Door id -> unscaled time it shuts, or <see cref="Waiting"/> while someone is behind. Host decides;
        /// clients mirror so a new host can carry on.
        /// </summary>
        private readonly Dictionary<string, float> countdowns = new Dictionary<string, float>();
        private readonly Dictionary<string, UT_Door> doorCache = new Dictionary<string, UT_Door>();
        private readonly Dictionary<string, UT_TriggerZone> zoneCache = new Dictionary<string, UT_TriggerZone>();
        private static readonly Dictionary<UT_TriggerZone, bool> closeZones = new Dictionary<UT_TriggerZone, bool>();
        /// <summary>Host: door id -> players who have passed through it.</summary>
        private readonly Dictionary<string, HashSet<SteamId>> passed = new Dictionary<string, HashSet<SteamId>>();
        /// <summary>Client: doors we've already told the host we passed.</summary>
        private readonly HashSet<string> sentPass = new HashSet<string>();
        private readonly Dictionary<string, string> lastHeldFor = new Dictionary<string, string>();
        /// <summary>A player this far above a door (meters) has clearly got past it, even without touching its trigger.</summary>
        private const float PastHeight = 4f;
        internal const string ZonePrefix = "zone:";

        public float CloseDelay
        {
            get
            {
                string raw = NetSession.Instance.GetLobbyData(NetSession.KeyDoorDelay);
                return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float delay)
                    ? Mathf.Clamp(delay, MinCloseDelay, MaxCloseDelay)
                    : DefaultCloseDelay;
            }
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession net = NetSession.Instance;
            net.On(PacketType.DoorOpen, (origin, r) => ApplyOpen(r.ReadString()));
            net.On(PacketType.DoorRequestClose, (origin, r) => HostPass(r.ReadString(), origin));
            net.OnLobbyChanged += NoteHostChange;
            net.On(PacketType.DoorCountdown, HandleCountdown);
            net.On(PacketType.DoorClose, HandleClose);
            net.OnLobbyChanged += () =>
            {
                if (!net.InLobby)
                {
                    ClearAll();
                }
            };
        }

        private void Update()
        {
            NetSession net = NetSession.Instance;
            if (!net.InLobby || countdowns.Count == 0)
            {
                return;
            }
            if (!WorldSpace.Ready)
            {
                ClearAll();
                return;
            }
            if (!net.IsHost)
            {
                return;
            }
            List<string> due = null;
            List<string> graceStarts = null;
            foreach (KeyValuePair<string, float> entry in countdowns)
            {
                if (!EveryonePassed(entry.Key))
                {
                    continue;
                }
                if (entry.Value == Waiting)
                {
                    (graceStarts ?? (graceStarts = new List<string>())).Add(entry.Key);
                }
                else if (Time.unscaledTime >= entry.Value)
                {
                    (due ?? (due = new List<string>())).Add(entry.Key);
                }
            }
            if (graceStarts != null)
            {
                foreach (string id in graceStarts)
                {
                    HostStartGrace(id);
                }
            }
            if (due == null)
            {
                return;
            }
            foreach (string id in due)
            {
                countdowns.Remove(id);
                net.Send(PacketType.DoorClose, w => w.Write(id));
                ApplyClose(id);
            }
        }

        private void ClearAll()
        {
            extrasRun.Clear();
            countdowns.Clear();
            passed.Clear();
            sentPass.Clear();
            lastHeldFor.Clear();
        }

        /// <summary>
        /// Host: true once every living player has walked through this door's trigger or is well above it. Bots
        /// don't hold doors.
        /// </summary>
        private bool EveryonePassed(string id)
        {
            passed.TryGetValue(id, out HashSet<SteamId> through);
            Component door = IsZone(id) ? (Component)FindZone(id, allowScan: false) : Find(id, allowScan: false);
            float pastY = door != null ? door.transform.position.y + PastHeight : float.MaxValue;
            var waiting = new List<string>();
            ENT_Player me = ENT_Player.playerObject;
            if (me != null && !me.dead && (through == null || !through.Contains(NetSession.LocalId)) && me.transform.position.y < pastY)
            {
                waiting.Add("you");
            }
            foreach (RemotePlayer remote in PlayerSync.Instance.Remotes.Values)
            {
                if (remote == null || remote.Latest.Dead || !remote.Present)
                {
                    continue;
                }
                if (Bot.TestBot.Instance != null && Bot.TestBot.Instance.IsBot(remote.SteamId))
                {
                    continue;
                }
                if ((through == null || !through.Contains(remote.SteamId)) && remote.LocalPosition.y < pastY)
                {
                    waiting.Add(remote.DisplayName);
                }
            }
            string list = string.Join(", ", waiting);
            if (!lastHeldFor.TryGetValue(id, out string before) || before != list)
            {
                lastHeldFor[id] = list;
                if (waiting.Count > 0)
                {
                    Plugin.Log.LogInfo($"Door held open for {list}: {id}");
                }
            }
            return waiting.Count == 0;
        }

        // ---------------------------------------------------------------- Local door events

        /// <summary>Returns false when the game's own close must be held back.</summary>
        internal bool OnLocalClose(UT_Door door)
        {
            if (!door.IsOpen())
            {
                return true;
            }
            string id = DoorId(door);
            if (ByPlayerControl(door))
            {
                // A lever, button or crank shut this door (an airlock). Every PC replays the control, so shut now;
                // waiting for everyone would stop airlocks cycling.
                countdowns.Remove(id);
                passed.Remove(id);
                return true;
            }
            LocalPass(id);
            return false;
        }

        // ---------------------------------------------------------------- Doors worked by a control

        /// <summary>Depth of game control code (buttons, holds, levers, hand panels) running right now.</summary>
        internal static int ControlDepth;
        private static float lastControlTime = float.MinValue;
        private static Vector3 lastControlPos;
        /// <summary>A door shut this soon after a control was worked this close to it counts as shut by that control (timed airlocks).</summary>
        private const float ControlWindow = 12f;
        private const float ControlRange = 20f;

        internal static void NoteControl(Component control)
        {
            if (control == null)
            {
                return;
            }
            lastControlTime = Time.time;
            lastControlPos = control.transform.position;
        }

        private static bool ByPlayerControl(UT_Door door)
        {
            if (ZoneSync.Applying)
            {
                return false; // a teammate's room trigger, not a control
            }
            if (ControlDepth > 0 || ButtonSync.Applying)
            {
                return true;
            }
            return Time.time - lastControlTime < ControlWindow
                && Vector3.Distance(((Component)door).transform.position, lastControlPos) < ControlRange;
        }

        /// <summary>The local player walked into a door's close zone. Returns false to hold the zone back.</summary>
        internal bool OnLocalCloseZone(UT_TriggerZone zone, Transform who)
        {
            string id = ZoneId(zone);
            zoneCache[id] = zone;
            if (extrasRun.Add(zone) && !IsGooZone(zone))
            {
                RunPart(zone, who, entering: true);
            }
            LocalPass(id);
            return false;
        }

        // ---------------------------------------------------------------- Zones that move the mass

        private static readonly HashSet<UT_TriggerZone> gooZones = new HashSet<UT_TriggerZone>();

        /// <summary>
        /// A zone that moves or wakes the mass (e.g. the hallway before the Mother). It waits until every living
        /// player has walked through, then fires for everyone at once.
        /// </summary>
        internal static bool IsGooZone(UT_TriggerZone zone) => zone != null && IsCloseZone(zone) && gooZones.Contains(zone);

        private static bool IsGooTarget(Object target)
        {
            if (target is UT_GooController || target is DEN_DeathFloor || target is UT_GooTakeover || target is UT_GooSpeedZone)
            {
                return true;
            }
            if (target is GameObject go)
            {
                string n = go.name.ToLowerInvariant();
                return n.Contains("goo") || n.Contains("mass") || go.GetComponentInChildren<UT_GooTakeover>(true) != null
                    || go.GetComponentInChildren<UT_GooController>(true) != null;
            }
            return false;
        }

        // ---------------------------------------------------------------- Splitting close zones

        /// <summary>Close zones whose non-door work already ran on this PC.</summary>
        private static readonly HashSet<UT_TriggerZone> extrasRun = new HashSet<UT_TriggerZone>();

        private static readonly string[] DoorWords = { "door", "bulkhead", "blast", "hatch", "vent", "gate", "shutter", "airlock" };

        /// <summary>
        /// Whether a zone listener works the door (held until everyone is through) or is the rest of walking in
        /// (loading, unloading, branches, achievements), which shouldn't wait for the door.
        /// </summary>
        private static bool IsDoorListener(Object target, string method)
        {
            if (target == null)
            {
                return true;
            }
            if (target is UT_Door || target is Animator)
            {
                return true;
            }
            if (target is M_Level || target is UT_WorldLoaderController || target is UT_BranchGenerator)
            {
                return false;
            }
            string m = (method ?? "").ToLowerInvariant();
            if (m.Contains("close") || m.Contains("shut") || m.Contains("lock"))
            {
                return true;
            }
            Transform t = target is Component c ? c.transform : target is GameObject g ? g.transform : null;
            for (int depth = 0; t != null && depth < 3; t = t.parent, depth++)
            {
                string n = t.name.ToLowerInvariant();
                foreach (string w in DoorWords)
                {
                    if (n.Contains(w)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Listeners that change the map (current level, unloading behind, building a branch). Only run when this
        /// PC's own player walks in, or they'd remove the floor under someone behind.
        /// </summary>
        private static bool IsWorldListener(Object target)
        {
            return target is M_Level || target is UT_WorldLoaderController || target is UT_BranchGenerator;
        }

        private enum Part { Door, Unload, World, Other }

        private static Part PartOf(Object target, string method)
        {
            if (IsDoorListener(target, method))
            {
                return Part.Door;
            }
            if (IsWorldListener(target))
            {
                string m = (method ?? "").ToLowerInvariant();
                return m.Contains("unload") || m.Contains("delete") || m.Contains("clear") || m.Contains("restart") || m.Contains("sendtoworldloader")
                    ? Part.Unload : Part.World;
            }
            return Part.Other;
        }

        /// <summary>
        /// Runs part of a close zone's listeners: on walking in, everything but the door and unloading; on
        /// shutting after we walked in, the door then unloading; on shutting if we never walked in, the door and
        /// harmless rest, never map changes.
        /// </summary>
        private static void RunPart(UT_TriggerZone zone, Transform who, bool entering, bool restDone = false, bool whole = false)
        {
            UnityEngine.Events.UnityEventBase ev = zone.onEnter;
            if (ev == null)
            {
                return;
            }
            var muted = new List<int>();
            var states = new List<UnityEngine.Events.UnityEventCallState>();
            int kept = 0;
            for (int i = 0; i < ev.GetPersistentEventCount(); i++)
            {
                Part part = PartOf(ev.GetPersistentTarget(i), ev.GetPersistentMethodName(i));
                bool run = whole ? (restDone || part == Part.Door || part == Part.Other)
                    : entering ? (part == Part.World || part == Part.Other)
                    : restDone ? (part == Part.Door || part == Part.Unload)
                    : (part == Part.Door || part == Part.Other);
                if (run)
                {
                    kept++;
                    continue;
                }
                muted.Add(i);
                states.Add(ev.GetPersistentListenerState(i));
                ev.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
            }
            if (kept == 0)
            {
                Restore(ev, muted, states);
                return;
            }
            if (entering)
            {
                Plugin.Log.LogInfo($"Running the rest of a door trigger now ({kept} of {ev.GetPersistentEventCount()}): {ButtonSync.ObjectId(zone)}");
            }
            try
            {
                if (!entering)
                {
                    ButtonSync.RunShared(() => zone.onEnter.Invoke(who), zone.onEnter);
                }
                else
                {
                    zone.onEnter.Invoke(who);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"A door trigger failed here ({ButtonSync.ObjectId(zone)}): {e.Message}");
            }
            finally
            {
                Restore(ev, muted, states);
            }
        }

        private static void Restore(UnityEngine.Events.UnityEventBase ev, List<int> muted, List<UnityEngine.Events.UnityEventCallState> states)
        {
            for (int k = 0; k < muted.Count; k++)
            {
                ev.SetPersistentListenerState(muted[k], states[k]);
            }
        }

        /// <summary>We went through a door's close trigger: tell the host (once) that we're past it.</summary>
        private void LocalPass(string id)
        {
            NetSession net = NetSession.Instance;
            // Everyone hears who got through (the host passes clients' word on), so if the host leaves,
            // whoever takes over still knows who the door is waiting for.
            if (sentPass.Add(id))
            {
                net.Send(PacketType.DoorRequestClose, w => w.Write(id));
            }
            HostPass(id, NetSession.LocalId);
        }

        private SteamId lastHost;

        /// <summary>
        /// This PC became host: it already has every countdown and who passed, so held doors carry on.
        /// </summary>
        private void NoteHostChange()
        {
            NetSession net = NetSession.Instance;
            SteamId host = net.InLobby ? net.HostId : default;
            if (host != lastHost && lastHost.Value != 0 && net.IsHost && countdowns.Count > 0)
            {
                Plugin.Log.LogInfo($"Took over as host: carrying on {countdowns.Count} held door(s)");
            }
            lastHost = host;
        }

        internal void OnLocalOpen(UT_Door door)
        {
            string id = DoorId(door);
            // Someone reopened it (lever, button): cancel a pending shut.
            countdowns.Remove(id);
            NetSession.Instance.Send(PacketType.DoorOpen, w => w.Write(id));
        }

        // ---------------------------------------------------------------- Host

        /// <summary>Every PC records who passed; only the host starts the countdown.</summary>
        private void HostPass(string id, SteamId who)
        {
            if (!passed.TryGetValue(id, out HashSet<SteamId> through))
            {
                passed[id] = through = new HashSet<SteamId>();
            }
            through.Add(who);
            if (!countdowns.ContainsKey(id))
            {
                // Held open until the last living player is through; Update starts the short grace then.
                // Every PC tracks it, so whoever takes over as host carries on.
                countdowns[id] = Waiting;
                if (IsZone(id)) FindZone(id); else Find(id);
            }
        }

        /// <summary>Host: everyone is through this door, so it shuts for everyone after the short grace.</summary>
        private void HostStartGrace(string id)
        {
            NetSession net = NetSession.Instance;
            if (!net.IsHost)
            {
                return;
            }
            float delay = IsZone(id) && IsGooZone(FindZone(id)) ? 0f : CloseDelay;
            countdowns[id] = Time.unscaledTime + delay;
            Plugin.Log.LogInfo($"Everyone is through, door closing in {delay:0.#} s: {id}");
            net.Send(PacketType.DoorCountdown, w =>
            {
                w.Write(id);
                w.Write(delay);
            });
        }

        // ---------------------------------------------------------------- Clients

        private void HandleCountdown(SteamId origin, System.IO.BinaryReader reader)
        {
            string id = reader.ReadString();
            float delay = reader.ReadSingle();
            if (origin == NetSession.Instance.HostId)
            {
                countdowns[id] = Time.unscaledTime + delay;
                if (IsZone(id)) FindZone(id); else Find(id);
                Plugin.Log.LogInfo($"Everyone is through, door closing in {delay:0.#} s: {id}");
            }
        }

        private void HandleClose(SteamId origin, System.IO.BinaryReader reader)
        {
            string id = reader.ReadString();
            if (origin == NetSession.Instance.HostId)
            {
                countdowns.Remove(id);
                ApplyClose(id);
            }
        }

        private void ApplyOpen(string id)
        {
            countdowns.Remove(id);
            UT_Door door = Find(id);
            if (door == null)
            {
                return;
            }
            Applying = true;
            try
            {
                door.Open();
            }
            finally
            {
                Applying = false;
            }
        }

        private void ApplyClose(string id)
        {
            passed.Remove(id);
            sentPass.Remove(id);
            lastHeldFor.Remove(id);
            if (IsZone(id))
            {
                FireZone(id);
                return;
            }
            UT_Door door = Find(id);
            if (door == null)
            {
                // Not loaded here (far away); it will be closed already by the time this player gets there.
                return;
            }
            Applying = true;
            try
            {
                door.Close();
            }
            finally
            {
                Applying = false;
            }
        }

        private void FireZone(string id)
        {
            UT_TriggerZone zone = FindZone(id);
            if (zone == null)
            {
                return; // not loaded here
            }
            var hasRun = Traverse.Create(zone).Field("hasRun");
            if (zone.runOnce && hasRun.GetValue<bool>())
            {
                return;
            }
            Collider c = zone.GetComponent<Collider>();
            ENT_Player player = ENT_Player.playerObject;
            bool inside = false;
            if (c != null && player != null)
            {
                Bounds b = c.bounds;
                b.Expand(1f);
                inside = b.Contains(player.transform.position);
            }
            Transform who = inside ? player.transform : zone.transform;
            Plugin.Log.LogInfo("Door shutting now: " + id);
            Applying = true;
            ZoneSync.Applying = true;
            ButtonSync.Applying = true;
            try
            {
                hasRun.SetValue(true);
                // The rest of the zone already ran here when this PC's player walked in; only the door is left.
                bool restDone = extrasRun.Contains(zone);
                RunPart(zone, who, entering: false, restDone: restDone, whole: IsGooZone(zone));
                if (!zone.runOnce)
                {
                    extrasRun.Remove(zone);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't shut the door here ({id}): {e.Message}");
            }
            finally
            {
                Applying = false;
                ZoneSync.Applying = false;
                ButtonSync.Applying = false;
            }
        }

        // ---------------------------------------------------------------- Close zones

        private static bool IsZone(string id) => id.StartsWith(ZonePrefix, System.StringComparison.Ordinal);

        internal static string ZoneId(UT_TriggerZone zone) => ZonePrefix + ButtonSync.ObjectId(zone);

        private UT_TriggerZone FindZone(string id, bool allowScan = true)
        {
            if (zoneCache.TryGetValue(id, out UT_TriggerZone cached) && cached != null)
            {
                return cached;
            }
            if (!allowScan)
            {
                return null;
            }
            foreach (UT_TriggerZone zone in Object.FindObjectsByType<UT_TriggerZone>(FindObjectsSortMode.None))
            {
                if (zone != null && IsCloseZone(zone))
                {
                    string zoneId = ZoneId(zone);
                    zoneCache[zoneId] = zone;
                    if (zoneId == id)
                    {
                        return zone;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// A trigger zone that shuts a door: named "close" on a door, or wired straight to a door's Close/Toggle.
        /// </summary>
        internal static bool IsCloseZone(UT_TriggerZone zone)
        {
            if (closeZones.TryGetValue(zone, out bool known))
            {
                return known;
            }
            bool result = false;
            string name = zone.name.ToLowerInvariant();
            if (name.Contains("close") || name.Contains("shut"))
            {
                M_Level level = zone.GetComponentInParent<M_Level>();
                Transform stop = level != null ? level.transform : null;
                int depth = 0;
                for (Transform t = zone.transform; t != null && t != stop && depth < 4; t = t.parent, depth++)
                {
                    string n = t.name.ToLowerInvariant();
                    if (n.Contains("door") || n.Contains("bulkhead") || n.Contains("blast") || n.Contains("hatch"))
                    {
                        result = true;
                        break;
                    }
                }
            }
            var listeners = new StringBuilder();
            if (zone.onEnter != null)
            {
                for (int i = 0; i < zone.onEnter.GetPersistentEventCount(); i++)
                {
                    Object target = zone.onEnter.GetPersistentTarget(i);
                    string method = zone.onEnter.GetPersistentMethodName(i) ?? "";
                    listeners.Append(' ').Append(target != null ? target.GetType().Name + "(" + target.name + ")" : "null").Append('.').Append(method);
                    if (target is UT_Door && (method == nameof(UT_Door.Close) || method == nameof(UT_Door.Toggle)))
                    {
                        result = true;
                    }
                    if (IsGooTarget(target))
                    {
                        result = true;
                        gooZones.Add(zone);
                    }
                }
            }
            closeZones[zone] = result;
            if (result)
            {
                Plugin.Log.LogInfo($"Door close zone found: {ButtonSync.ObjectId(zone)} ->{listeners}");
            }
            return result;
        }

        // ---------------------------------------------------------------- Ids

        private UT_Door Find(string id, bool allowScan = true)
        {
            if (doorCache.TryGetValue(id, out UT_Door cached) && cached != null)
            {
                return cached;
            }
            if (!allowScan)
            {
                return null;
            }
            foreach (UT_Door door in Object.FindObjectsByType<UT_Door>(FindObjectsSortMode.None))
            {
                string doorId = DoorId(door);
                doorCache[doorId] = door;
                if (doorId == id)
                {
                    return door;
                }
            }
            return null;
        }

        /// <summary>
        /// Same on every client: owning level name, offset-corrected rounded height, then the sibling path inside
        /// it (indices above the level differ per client).
        /// </summary>
        public static string DoorId(UT_Door door)
        {
            Transform t = ((Component)door).transform;
            M_Level level = t.GetComponentInParent<M_Level>();
            Transform root = level != null ? ((Component)level).transform : null;
            var path = new StringBuilder();
            Transform cursor = t;
            while (cursor != null && cursor != root)
            {
                path.Insert(0, "/" + cursor.GetSiblingIndex() + cursor.name);
                cursor = cursor.parent;
            }
            if (root == null)
            {
                return ((Component)door).gameObject.scene.name + path;
            }
            int height = Mathf.RoundToInt(WorldSpace.ToShared(root.position).y);
            return root.name + "@" + height + path;
        }
    }

    [HarmonyPatch(typeof(UT_Door))]
    internal static class DoorPatches
    {
        private static bool Synced =>
            !DoorSync.Applying && !DoorSync.InDoorSelfUpdate
            && NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        [HarmonyPatch("Start")]
        [HarmonyPrefix]
        private static void StartPrefix() => DoorSync.InDoorSelfUpdate = true;

        [HarmonyPatch("Start")]
        [HarmonyFinalizer]
        private static void StartFinalizer() => DoorSync.InDoorSelfUpdate = false;

        [HarmonyPatch("FixedUpdate")]
        [HarmonyPrefix]
        private static void FixedUpdatePrefix() => DoorSync.InDoorSelfUpdate = true;

        [HarmonyPatch("FixedUpdate")]
        [HarmonyFinalizer]
        private static void FixedUpdateFinalizer() => DoorSync.InDoorSelfUpdate = false;

        [HarmonyPatch(nameof(UT_Door.Close))]
        [HarmonyPrefix]
        private static bool Close(UT_Door __instance)
        {
            return !Synced || DoorSync.Instance.OnLocalClose(__instance);
        }

        [HarmonyPatch(nameof(UT_Door.Open))]
        [HarmonyPrefix]
        private static void Open(UT_Door __instance, bool ___open)
        {
            if (Synced && !___open)
            {
                DoorSync.Instance.OnLocalOpen(__instance);
            }
        }
    }

    /// <summary>Marks when game control code runs, so a door it shuts closes at once (see DoorSync.ByPlayerControl).</summary>
    [HarmonyPatch]
    internal static class DoorControlPatches
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CL_Button), nameof(CL_Button.Interact), new System.Type[0]);
            yield return AccessTools.Method(typeof(CL_HoldButton), nameof(CL_HoldButton.Interact), new System.Type[0]);
            yield return AccessTools.Method(typeof(CL_HoldButton), "Stop");
            yield return AccessTools.Method(typeof(CL_HoldButton), nameof(CL_HoldButton.StopInteract));
            yield return AccessTools.Method(typeof(CL_HoldButton), nameof(CL_HoldButton.Update));
            yield return AccessTools.Method(typeof(CL_HoldLever), "ResolveSnapping");
            yield return AccessTools.Method(typeof(UT_HandTrigger), "OnTriggerEnter");
        }

        [HarmonyPrefix]
        private static void Prefix(Component __instance, System.Reflection.MethodBase __originalMethod)
        {
            DoorSync.ControlDepth++;
            // Update and ResolveSnapping run every frame: only a real press counts for the timed window.
            string name = __originalMethod.Name;
            if (name != nameof(CL_HoldButton.Update) && name != "ResolveSnapping")
            {
                DoorSync.NoteControl(__instance);
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            DoorSync.ControlDepth = Mathf.Max(0, DoorSync.ControlDepth - 1);
        }
    }
}
