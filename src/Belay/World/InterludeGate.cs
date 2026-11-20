using System.Collections.Generic;
using System.Linq;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Interludes and breakrooms wait for the team: doors and hatches never wait, elevators wait until every
    /// living teammate is aboard, and other buttons wait until everyone is in the room.
    /// </summary>
    internal static class InterludeGate
    {
        /// <summary>How far outside a level's bounds still counts as "in the room" (meters).</summary>
        private const float BoundsSlack = 2f;

        /// <summary>For the HUD: teammates still missing from the room you're in, or 0.</summary>
        public static int MissingInCurrentRoom
        {
            get
            {
                if (!InLobbyRun || WorldLoader.instance == null || WorldLoader.instance.GetCurrentLevel() == null)
                {
                    return 0;
                }
                M_Level level = WorldLoader.instance.GetCurrentLevel().GetLevel();
                return IsGatedRoom(level) ? Missing(level).Count : 0;
            }
        }

        private static bool InLobbyRun => NetSession.InLobbyRun;

        public static bool IsGatedRoom(M_Level level)
        {
            return level != null && (level.safeArea || level.name.ToLowerInvariant().Contains("interlude"));
        }

        /// <summary>This spot is inside an interlude or breakroom (a room that waits for the team).</summary>
        internal static bool InGatedRoom(Vector3 worldPos)
        {
            M_Level level = WorldCatchUp.LevelAt(worldPos);
            return IsGatedRoom(level) && Inside(level, worldPos);
        }

        private static bool Inside(M_Level level, Vector3 worldPos)
        {
            Vector3 local = level.transform.worldToLocalMatrix.MultiplyPoint(worldPos);
            Bounds b = level.GetLocalBounds();
            b.Expand(BoundsSlack * 2f);
            return b.Contains(local);
        }

        /// <summary>Living teammates (not counting you) who aren't inside this level yet.</summary>
        internal static List<string> Missing(M_Level level)
        {
            return Missing(p => Inside(level, p));
        }

        /// <summary>
        /// Living teammates (not you) who aren't where <paramref name="there"/> says. Bots never hold things up;
        /// they're brought along.
        /// </summary>
        internal static List<string> Missing(System.Func<Vector3, bool> there)
        {
            var missing = new List<string>();
            foreach (RemotePlayer remote in PlayerSync.Instance.Remotes.Values)
            {
                if (remote == null || remote.Latest.Dead || !remote.Present)
                {
                    continue;
                }
                if (Bot.TestBot.Instance != null && Bot.TestBot.Instance.IsBot(remote.SteamId))
                {
                    if (ENT_Player.playerObject != null && there(ENT_Player.playerObject.transform.position))
                    {
                        Bot.TestBot.Instance.BringAlong(remote.SteamId, there);
                    }
                    continue;
                }
                if (!there(remote.LocalPosition))
                {
                    missing.Add(remote.DisplayName);
                }
            }
            return missing;
        }

        // ---------------------------------------------------------------- What a control does

        internal enum GateKind { Free, Room, Elevator, Gathered }

        private static readonly Dictionary<int, GateKind> kinds = new Dictionary<int, GateKind>();
        /// <summary>Controls that take the team on to the next area (see AreaVote).</summary>
        private static readonly HashSet<int> wayOn = new HashSet<int>();

        /// <summary>How far from an elevator's button every teammate has to be before it goes (meters).</summary>
        private const float ElevatorReach = 8f;

        /// <summary>How far from a Hadal diver button every living teammate has to be before it works (meters).</summary>
        private const float GatherReach = 15f;

        /// <summary>
        /// Doors and hatches never wait. Elevators leave only once everyone is aboard. Anything that seals the way
        /// behind or moves the mass (a lockdown button) waits until everyone is in the room.
        /// </summary>
        internal static GateKind Classify(Component control, M_Level level, params UnityEngine.Events.UnityEventBase[] events)
        {
            int key = control.GetInstanceID();
            if (kinds.TryGetValue(key, out GateKind kind))
            {
                return kind;
            }
            var words = new List<string>();
            AddPath(words, control.transform, level, 4);
            foreach (var ev in events)
            {
                if (ev == null) continue;
                for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                {
                    Object target = ev.GetPersistentTarget(i);
                    words.Add(ev.GetPersistentMethodName(i) ?? "");
                    if (target == null) continue;
                    words.Add(target.GetType().Name);
                    if (target is Component c) AddPath(words, c.transform, level, 3);
                    else if (target is GameObject g) AddPath(words, g.transform, level, 3);
                }
            }
            string all = string.Join(" ", words).ToLowerInvariant();
            if (GathersTeam(all, level))
            {
                // The Hadal diver's buttons carry whoever is there off to the next area and strand anyone
                // left behind, so they wait until every living teammate is gathered at them.
                kind = GateKind.Gathered;
            }
            else if (all.Contains("elevator") || all.Contains("lift"))
            {
                kind = GateKind.Elevator;
            }
            else if (SealsOrMovesMass(all))
            {
                // A lockdown sealed the hatch and raised the mass on a teammate still climbing below.
                kind = GateKind.Room;
            }
            else if (all.Contains("door") || all.Contains("hatch") || all.Contains("shutter") || all.Contains("airlock"))
            {
                kind = GateKind.Free;
            }
            else if (IsPersonalOrTerminal(control, all, events))
            {
                // Terminals, vendors, upgrade consoles: each player's own. Using one never strands anybody,
                // so nobody waits for the rest of the team to use them.
                kind = GateKind.Free;
            }
            else
            {
                kind = GateKind.Room;
            }
            kinds[key] = kind;
            // Only controls that carry the team on wait for the area vote. A lockdown sealing the hatch behind
            // (Silo to Pipeworks) just waits for everyone in the room.
            if (kind == GateKind.Elevator || kind == GateKind.Gathered)
            {
                wayOn.Add(key);
            }
            Plugin.Log.LogInfo($"Interlude control {ButtonSync.ObjectId(control)}: {kind} ({all})");
            return kind;
        }

        /// <summary>
        /// Buttons that carry everyone on and strand the rest (the pair after the Teeth climb, the Hadal diver's).
        /// These wait for the team even outside interludes.
        /// </summary>
        private static bool GathersTeam(string all, M_Level level)
        {
            string levelName = level != null ? level.name.ToLowerInvariant() : "";
            return levelName.Contains("pier_entrance") || all.Contains("hadal") || all.Contains("diver") || all.Contains("tram_interfacility");
        }

        /// <summary>Gathering buttons count everywhere, not only in interludes.</summary>
        private static bool IsGatheringControl(Component control, M_Level level)
        {
            return Classify(control, level, EventsOf(control)) == GateKind.Gathered;
        }

        private static bool IsPersonalOrTerminal(Component control, string all, UnityEngine.Events.UnityEventBase[] events)
        {
            return !ButtonSync.Shareable(control, events)
                || all.Contains("os_computer") || all.Contains("activatecomputer") || all.Contains("keyboard")
                || all.Contains("terminal");
        }

        /// <summary>
        /// This spot is above the room: a teammate there has already been through and gone on up, so nothing done
        /// in the room can leave them behind.
        /// </summary>
        private static bool Above(M_Level level, Vector3 worldPos)
        {
            Vector3 local = level.transform.worldToLocalMatrix.MultiplyPoint(worldPos);
            Bounds b = level.GetLocalBounds();
            return local.y > b.max.y + BoundsSlack;
        }

        private static bool SealsOrMovesMass(string all)
        {
            return all.Contains("close") || all.Contains("shut") || all.Contains("seal") || all.Contains("lockdown")
                || all.Contains("goo") || all.Contains("deathfloor") || all.Contains("death floor");
        }

        private static void AddPath(List<string> words, Transform t, M_Level level, int depth)
        {
            for (int i = 0; t != null && i < depth && (level == null || t != level.transform); i++, t = t.parent)
            {
                words.Add(t.name);
            }
        }

        private static UnityEngine.Events.UnityEventBase[] EventsOf(Component control)
        {
            if (control is CL_Button b) return new UnityEngine.Events.UnityEventBase[] { b.activeEvent };
            if (control is CL_HoldButton h) return new UnityEngine.Events.UnityEventBase[] { h.activeEvent, h.stopEvent, h.finishEvent, h.failEvent };
            return new UnityEngine.Events.UnityEventBase[0];
        }

        /// <summary>This room has a control that takes the team on to the next area (an elevator's button, say).</summary>
        internal static bool HasWayOn(M_Level level)
        {
            if (level == null)
            {
                return false;
            }
            foreach (Component c in level.GetComponentsInChildren<CL_Button>(true).Cast<Component>().Concat(level.GetComponentsInChildren<CL_HoldButton>(true)))
            {
                if (c == null || c.GetComponentInParent<VoteTerminalMarker>() != null)
                {
                    continue;
                }
                Classify(c, level, EventsOf(c));
                if (wayOn.Contains(c.GetInstanceID()))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>True when this control is in an interlude that's still waiting for teammates.</summary>
        public static bool Blocked(Component control, bool notify)
        {
            if (!InLobbyRun || control == null || ButtonSync.Applying)
            {
                return false;
            }
            M_Level level = control.GetComponentInParent<M_Level>();
            if (!IsGatedRoom(level) && !(control is CL_Button || control is CL_HoldButton ? IsGatheringControl(control, level) : false))
            {
                return false;
            }
            GateKind kind = Classify(control, level, EventsOf(control));
            if (kind == GateKind.Free)
            {
                return false;
            }
            if (wayOn.Contains(control.GetInstanceID()) && AreaVote.Holds(level))
            {
                return true; // the team hasn't voted to move on yet
            }
            Vector3 at = control.transform.position;
            float reach = kind == GateKind.Elevator ? ElevatorReach : GatherReach;
            // A teammate who went on further up isn't held up by the room, and doesn't hold it up either.
            List<string> missing = kind == GateKind.Room
                ? Missing(p => Inside(level, p) || Above(level, p))
                : Missing(p => (p - at).sqrMagnitude <= reach * reach);
            if (missing.Count == 0)
            {
                return false;
            }
            return true; // waits quietly, no on-screen text
        }
    }

    [HarmonyPatch]
    internal static class InterludeGatePatches
    {
        // Hide the interact prompt while the room is waiting.
        [HarmonyPatch(typeof(CL_Button), nameof(CL_Button.CanInteract), new[] { typeof(Interaction) })]
        [HarmonyPostfix]
        private static void ButtonCanInteract(CL_Button __instance, ref bool __result)
        {
            if (__result && InterludeGate.Blocked(__instance, notify: false)) __result = false;
        }

        [HarmonyPatch(typeof(CL_Button), nameof(CL_Button.CanInteract), new System.Type[0])]
        [HarmonyPostfix]
        private static void ButtonCanInteractNoArgs(CL_Button __instance, ref bool __result)
        {
            if (__result && InterludeGate.Blocked(__instance, notify: false)) __result = false;
        }

        [HarmonyPatch(typeof(CL_HoldButton), nameof(CL_HoldButton.CanInteract), new[] { typeof(Interaction) })]
        [HarmonyPostfix]
        private static void HoldCanInteract(CL_HoldButton __instance, ref bool __result)
        {
            if (__result && InterludeGate.Blocked(__instance, notify: false)) __result = false;
        }

        // And refuse the press itself, in case something calls Interact directly.
        [HarmonyPatch(typeof(CL_Button), nameof(CL_Button.Interact), new System.Type[0])]
        [HarmonyPrefix]
        private static bool ButtonInteract(CL_Button __instance) => !InterludeGate.Blocked(__instance, notify: true);

        [HarmonyPatch(typeof(CL_Button), nameof(CL_Button.Interact), new[] { typeof(Clickable.InteractionInfo) })]
        [HarmonyPrefix]
        private static bool ButtonInteractInfo(CL_Button __instance) => !InterludeGate.Blocked(__instance, notify: true);

        [HarmonyPatch(typeof(CL_HoldButton), nameof(CL_HoldButton.Interact), new System.Type[0])]
        [HarmonyPrefix]
        private static bool HoldInteract(CL_HoldButton __instance) => !InterludeGate.Blocked(__instance, notify: true);

        [HarmonyPatch(typeof(CL_HoldButton), nameof(CL_HoldButton.Interact), new[] { typeof(Clickable.InteractionInfo) })]
        [HarmonyPrefix]
        private static bool HoldInteractInfo(CL_HoldButton __instance) => !InterludeGate.Blocked(__instance, notify: true);
    }
}
