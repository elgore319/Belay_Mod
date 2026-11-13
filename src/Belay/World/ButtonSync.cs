using System.Text;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// Level buttons are shared: when anyone presses one, it's pressed for everyone, so whatever it
    /// drives (hatches, lifts, lights, animated doors) changes on every PC at once. In interludes the
    /// press is only allowed once the whole team is inside (see InterludeGate).
    /// Buttons that buy things or act on the presser's own player are left alone, because currency
    /// is never shared.
    /// </summary>
    public class ButtonSync : MonoBehaviour
    {
        public static ButtonSync Instance { get; private set; }

        /// <summary>Set while replaying someone else's press, so it isn't sent back or gated.</summary>
        internal static bool Applying;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.ButtonPress, (origin, r) => ApplyPress(r.ReadString()));
        }

        internal static bool Shareable(CL_Button button)
        {
            return button != null && button.activeEvent != null && Shareable(button, button.activeEvent);
        }

        /// <summary>
        /// True when this level object's events can safely run on every PC: it sits inside a level and
        /// none of its events buy something or act on the local player.
        /// </summary>
        internal static bool Shareable(Component owner, params UnityEngine.Events.UnityEventBase[] events)
        {
            M_Level level = owner != null ? owner.GetComponentInParent<M_Level>() : null;
            if (level == null || owner.GetComponentInParent<VoteTerminalMarker>() != null)
            {
                return false; // Belay's vote terminals are each player's own
            }
            // Personal machines (upgrade consoles, shops, vending) are recognised by their objects' names too.
            for (Transform t = owner.transform; t != null && t != level.transform; t = t.parent)
            {
                if (IsPersonal(t.name))
                {
                    return false;
                }
            }
            foreach (var ev in events)
            {
                if (ev == null) continue;
                for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                {
                    Object target = ev.GetPersistentTarget(i);
                    string method = ev.GetPersistentMethodName(i) ?? "";
                    string type = target != null ? target.GetType().Name : "";
                    if (IsPersonal(type) || IsPersonal(method))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Listeners that act on whoever pressed: sitting down at a terminal (OS_Manager.Activate pulls
        /// the player to the screen and takes the camera), camera takeovers, teleports, cursor and UI.
        /// A teammate's press still does everything else, but these are left out on other PCs.
        /// </summary>
        internal static bool IsPresserOnly(string name)
        {
            string n = name.ToLowerInvariant();
            return n.StartsWith("os_") || n.StartsWith("app_") || n.Contains("computer") || n.Contains("terminal")
                || n.Contains("camera") || n.Contains("takeover") || n.Contains("teleport") || n.Contains("cursor")
                || n.Contains("player") || n.StartsWith("ui_") || n.Contains("uimanager") || n.Contains("dropHang".ToLowerInvariant());
        }

        /// <summary>Run a teammate's press with the presser-only listeners switched off for the moment.</summary>
        internal static void RunShared(System.Action press, params UnityEngine.Events.UnityEventBase[] events)
        {
            var muted = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<UnityEngine.Events.UnityEventBase, int>>();
            var states = new System.Collections.Generic.List<UnityEngine.Events.UnityEventCallState>();
            foreach (var ev in events)
            {
                if (ev == null) continue;
                for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                {
                    Object target = ev.GetPersistentTarget(i);
                    string type = target != null ? target.GetType().Name : "";
                    string method = ev.GetPersistentMethodName(i) ?? "";
                    if (IsPresserOnly(type) || IsPresserOnly(method))
                    {
                        muted.Add(new System.Collections.Generic.KeyValuePair<UnityEngine.Events.UnityEventBase, int>(ev, i));
                        states.Add(ev.GetPersistentListenerState(i));
                        ev.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
                    }
                }
            }
            try
            {
                press();
            }
            finally
            {
                for (int k = 0; k < muted.Count; k++)
                {
                    muted[k].Key.SetPersistentListenerState(muted[k].Value, states[k]);
                }
            }
        }

        private static bool IsPersonal(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("roach") || n.Contains("trader") || n.Contains("shop") || n.Contains("vend")
                || n.Contains("buy") || n.Contains("purchase") || n.Contains("currency") || n.Contains("inventory")
                || n.Contains("perk") || n.Contains("cosmetic") || n.Contains("upgrade")
                || n.Contains("_atm") || n.Contains("barter") || n.Contains("locker");
        }

        internal void Broadcast(CL_Button button)
        {
            string id = ObjectId(button);
            Plugin.Log.LogInfo("Button pressed: " + id);
            NetSession.Instance.Send(PacketType.ButtonPress, w => w.Write(id));
        }

        /// <summary>
        /// Presses on levels that weren't loaded here yet (you were further down, or dead and spectating):
        /// they're replayed as soon as that level loads, so the room is in the same state for you when you
        /// get there (an interlude's lockdown, for one, used to never happen on your PC).
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, float> missed = new System.Collections.Generic.Dictionary<string, float>();
        private float nextRetry;
        private const float MissedKeepSeconds = 900f;

        private void Update()
        {
            if (missed.Count == 0 || Time.unscaledTime < nextRetry)
            {
                return;
            }
            nextRetry = Time.unscaledTime + 1f;
            if (NetSession.Instance == null || !NetSession.Instance.InLobby || !CL_GameManager.IsInGame())
            {
                missed.Clear();
                return;
            }
            if (!WorldSpace.Ready)
            {
                return;
            }
            foreach (var pair in new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, float>>(missed))
            {
                if (Time.unscaledTime - pair.Value > MissedKeepSeconds)
                {
                    missed.Remove(pair.Key);
                }
                else if (ApplyPress(pair.Key, catchingUp: true))
                {
                    missed.Remove(pair.Key);
                    Plugin.Log.LogInfo("Caught up on a teammate's earlier press: " + pair.Key);
                }
            }
        }

        private static bool ApplyPress(string id, bool catchingUp = false)
        {
            foreach (CL_Button button in FindObjectsByType<CL_Button>(FindObjectsSortMode.None))
            {
                if (button == null || ObjectId(button) != id)
                {
                    continue;
                }
                Applying = true;
                try
                {
                    var presses = Traverse.Create(button).Field("presses");
                    int before = presses.GetValue<int>();
                    RunShared(() => button.Interact(), button.activeEvent, (button as CL_ToggleButton)?.onEvent, (button as CL_ToggleButton)?.offEvent);
                    if (presses.GetValue<int>() == before)
                    {
                        // Our copy wasn't ready (still inactive, cooling down, or switched on by
                        // something only the presser did). The press happened, so run it anyway.
                        Plugin.Log.LogInfo("Forcing teammate's press on a button that wasn't ready here: " + id);
                        presses.SetValue(before + 1);
                        button.hasBeenPressed = true;
                        RunShared(() => button.activeEvent?.Invoke(), button.activeEvent);
                        AudioManager.PlaySound(button.pressSound, button.transform.position);
                    }
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"Teammate's button press failed here ({id}): {e.Message}");
                }
                finally
                {
                    Applying = false;
                }
                return true;
            }
            if (!catchingUp)
            {
                Plugin.Log.LogInfo("Teammate pressed a button that isn't loaded here (will replay when it loads): " + id);
                missed[id] = Time.unscaledTime;
            }
            return false;
        }

        /// <summary>Same scheme as doors: level name, the level's shared height, then the sibling path inside it.</summary>
        internal static string ObjectId(Component c)
        {
            Transform t = c.transform;
            M_Level level = t.GetComponentInParent<M_Level>();
            Transform root = level != null ? level.transform : null;
            var path = new StringBuilder();
            for (Transform cursor = t; cursor != null && cursor != root; cursor = cursor.parent)
            {
                path.Insert(0, "/" + cursor.GetSiblingIndex() + cursor.name);
            }
            if (root == null)
            {
                return c.gameObject.scene.name + path;
            }
            return root.name + "@" + Mathf.RoundToInt(WorldSpace.ToShared(root.position).y) + path;
        }
    }

    [HarmonyPatch(typeof(CL_Button), nameof(CL_Button.Interact), new System.Type[0])]
    internal static class ButtonPressPatch
    {
        [HarmonyPrefix]
        private static void Prefix(CL_Button __instance, out int __state)
        {
            __state = Traverse.Create(__instance).Field("presses").GetValue<int>();
        }

        [HarmonyPostfix]
        private static void Postfix(CL_Button __instance, int __state)
        {
            if (ButtonSync.Applying || ButtonSync.Instance == null || NetSession.Instance == null || !NetSession.Instance.InLobby || !WorldSpace.Ready)
            {
                return;
            }
            int now = Traverse.Create(__instance).Field("presses").GetValue<int>();
            if (now <= __state)
            {
                return;
            }
            if (ButtonSync.Shareable(__instance))
            {
                ButtonSync.Instance.Broadcast(__instance);
            }
            else
            {
                Plugin.Log.LogInfo("Button kept local (personal or outside a level): " + ButtonSync.ObjectId(__instance));
            }
        }
    }
}
