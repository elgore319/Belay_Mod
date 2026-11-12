using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Interludes and breakrooms wait for the whole team, so nobody gets left behind by someone racing
    /// ahead and pressing on:
    ///  - doors and hatches never wait, so whoever is behind can always catch up;
    ///  - elevators and lifts wait until every living teammate is aboard (within reach of the button);
    ///  - any other button waits until every living teammate is inside the room.
    /// </summary>
    internal static class InterludeGate
    {
        /// <summary>How far outside a level's bounds still counts as "in the room" (meters).</summary>
        private const float BoundsSlack = 2f;

        private static float lastNoticeTime = -10f;

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

        private static bool InLobbyRun => NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready;

        public static bool IsGatedRoom(M_Level level)
        {
            return level != null && (level.safeArea || level.name.ToLowerInvariant().Contains("interlude"));
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
        /// Living teammates (not counting you) who aren't where <paramref name="there"/> says yet. Bots never
        /// hold the team up: once you're there, a bot that isn't is brought along.
        /// </summary>
        internal static List<string> Missing(System.Func<Vector3, bool> there)
        {
            var missing = new List<string>();
            foreach (RemotePlayer remote in PlayerSync.Instance.Remotes.Values)
            {
                if (remote == null || remote.Latest.Dead || Time.unscaledTime - remote.LastReceived > 3f)
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

        internal enum GateKind { Free, Room, Elevator }

        private static readonly Dictionary<int, GateKind> kinds = new Dictionary<int, GateKind>();

        /// <summary>How far from an elevator's button every teammate has to be before it goes (meters).</summary>
        private const float ElevatorReach = 8f;

        /// <summary>
        /// Doors and hatches never wait: anyone can open the way on, so a teammate who's behind can still
        /// catch up. Elevators (and lifts) are where the team waits: they leave only once everyone is aboard.
        /// Anything else in the room keeps waiting until the whole team is in the room.
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
            if (all.Contains("elevator") || all.Contains("lift"))
            {
                kind = GateKind.Elevator;
            }
            else if (all.Contains("door") || all.Contains("hatch") || all.Contains("shutter") || all.Contains("airlock"))
            {
                kind = GateKind.Free;
            }
            else
            {
                kind = GateKind.Room;
            }
            kinds[key] = kind;
            Plugin.Log.LogInfo($"Interlude control {ButtonSync.ObjectId(control)}: {kind} ({all})");
            return kind;
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

        /// <summary>True when this control is in an interlude that's still waiting for teammates.</summary>
        public static bool Blocked(Component control, bool notify)
        {
            if (!InLobbyRun || control == null || ButtonSync.Applying)
            {
                return false;
            }
            M_Level level = control.GetComponentInParent<M_Level>();
            if (!IsGatedRoom(level))
            {
                return false;
            }
            GateKind kind = Classify(control, level, EventsOf(control));
            if (kind == GateKind.Free)
            {
                return false;
            }
            Vector3 at = control.transform.position;
            List<string> missing = kind == GateKind.Elevator
                ? Missing(p => (p - at).sqrMagnitude <= ElevatorReach * ElevatorReach)
                : Missing(level);
            if (missing.Count == 0)
            {
                return false;
            }
            // The elevator waits quietly (Lee: don't say who it's waiting for).
            if (notify && kind != GateKind.Elevator && Time.unscaledTime - lastNoticeTime > 2f)
            {
                lastNoticeTime = Time.unscaledTime;
                string where = kind == GateKind.Elevator ? "the elevator" : "the room";
                Run.RunFlow.Instance?.ShowNotice("Wait for your team: " + string.Join(", ", missing) + (missing.Count == 1 ? " hasn't" : " haven't") + " reached " + where + " yet");
            }
            return true;
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
