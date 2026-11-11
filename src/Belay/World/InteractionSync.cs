using System.Collections.Generic;
using Belay.Net;
using Belay.Players;
using HarmonyLib;
using UnityEngine;

namespace Belay.World
{
    /// <summary>
    /// The rest of the level's controls, shared like buttons (see ButtonSync):
    ///  - Hold buttons and cranks: starting, letting go, failing and finishing a hold run their events
    ///    on every PC, so a "hold to open" door or a winch finishes for everyone.
    ///  - Levers: while you hold one, its angle streams to everyone, and their copy swings to match.
    ///    Each PC then hits the same snap points itself, so whatever a snap drives fires everywhere.
    ///  - One-shot hand triggers (touch this panel to start the lift): fire for everyone.
    /// Controls that buy things or act on your own player stay local.
    /// </summary>
    public class InteractionSync : MonoBehaviour
    {
        public static InteractionSync Instance { get; private set; }

        internal enum HoldKind : byte { Start = 0, Stop = 1, Fail = 2, Finish = 3 }

        private const float LeverSendInterval = 0.1f;

        private readonly HashSet<CL_HoldLever> heldLevers = new HashSet<CL_HoldLever>();
        private readonly Dictionary<CL_HoldLever, float> lastSentAngle = new Dictionary<CL_HoldLever, float>();
        private readonly Dictionary<string, Component> lookup = new Dictionary<string, Component>();
        private float nextLeverSend;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            NetSession.Instance.On(PacketType.HoldInteract, (origin, r) => ApplyHold(r.ReadString(), (HoldKind)r.ReadByte()));
            NetSession.Instance.On(PacketType.LeverAngle, (origin, r) => ApplyLever(r.ReadString(), r.ReadSingle()));
            NetSession.Instance.On(PacketType.HandTouch, (origin, r) => ApplyHandTouch(r.ReadString()));
        }

        internal static bool Live => Instance != null && NetSession.Instance != null && NetSession.Instance.InLobby && WorldSpace.Ready && !ButtonSync.Applying;

        // ---------------------------------------------------------------- Sending

        internal static bool Shareable(CL_HoldButton b)
        {
            return ButtonSync.Shareable(b, b.activeEvent, b.stopEvent, b.finishEvent, b.failEvent);
        }

        internal void SendHold(CL_HoldButton b, HoldKind kind)
        {
            string id = ButtonSync.ObjectId(b);
            if (!Shareable(b))
            {
                Plugin.Log.LogInfo($"Hold control kept local ({kind}): {id}");
                return;
            }
            Plugin.Log.LogInfo($"Hold control {kind}: {id}");
            NetSession.Instance.Send(PacketType.HoldInteract, w =>
            {
                w.Write(id);
                w.Write((byte)kind);
            });
            if (b is CL_HoldLever lever)
            {
                if (kind == HoldKind.Start)
                {
                    heldLevers.Add(lever);
                }
                else
                {
                    SendLever(lever);
                    heldLevers.Remove(lever);
                }
            }
        }

        internal void SendHandTouch(UT_HandTrigger trigger)
        {
            string id = ButtonSync.ObjectId(trigger);
            if (!ButtonSync.Shareable(trigger, trigger.onEnter))
            {
                Plugin.Log.LogInfo("Hand trigger kept local: " + id);
                return;
            }
            Plugin.Log.LogInfo("Hand trigger: " + id);
            NetSession.Instance.Send(PacketType.HandTouch, w => w.Write(id));
        }

        private void SendLever(CL_HoldLever lever)
        {
            if (lever == null)
            {
                return;
            }
            float angle = Traverse.Create(lever).Field("currentAngle").GetValue<float>();
            lastSentAngle[lever] = angle;
            string id = ButtonSync.ObjectId(lever);
            NetSession.Instance.Send(PacketType.LeverAngle, w =>
            {
                w.Write(id);
                w.Write(angle);
            });
        }

        private void Update()
        {
            if (heldLevers.Count == 0 || Time.unscaledTime < nextLeverSend)
            {
                return;
            }
            nextLeverSend = Time.unscaledTime + LeverSendInterval;
            if (!Live)
            {
                heldLevers.Clear();
                return;
            }
            heldLevers.RemoveWhere(l => l == null);
            foreach (CL_HoldLever lever in heldLevers)
            {
                float angle = Traverse.Create(lever).Field("currentAngle").GetValue<float>();
                if (!lastSentAngle.TryGetValue(lever, out float last) || Mathf.Abs(last - angle) > 0.5f)
                {
                    SendLever(lever);
                }
            }
        }

        // ---------------------------------------------------------------- Receiving

        private T Find<T>(string id) where T : Component
        {
            if (lookup.TryGetValue(id, out Component c) && c is T cached && cached != null)
            {
                return cached;
            }
            foreach (T candidate in FindObjectsByType<T>(FindObjectsSortMode.None))
            {
                if (candidate != null && ButtonSync.ObjectId(candidate) == id)
                {
                    lookup[id] = candidate;
                    return candidate;
                }
            }
            return null;
        }

        private void ApplyHold(string id, HoldKind kind)
        {
            CL_HoldButton b = Find<CL_HoldButton>(id);
            if (b == null)
            {
                Plugin.Log.LogInfo($"Teammate used a hold control that isn't loaded here ({kind}): {id}");
                return;
            }
            ButtonSync.Applying = true;
            try
            {
                var t = Traverse.Create(b);
                switch (kind)
                {
                    case HoldKind.Start:
                        t.Field("presses").SetValue(t.Field("presses").GetValue<int>() + 1);
                        b.hasBeenPressed = true;
                        AudioManager.PlaySound(b.pressSound, b.transform.position, b.pressVolume);
                        ButtonSync.RunShared(() => b.activeEvent?.Invoke(), b.activeEvent);
                        break;
                    case HoldKind.Stop:
                        AudioManager.PlaySound(b.letgoSound, b.transform.position, b.pressVolume);
                        ButtonSync.RunShared(() => b.stopEvent?.Invoke(), b.stopEvent);
                        break;
                    case HoldKind.Fail:
                        ButtonSync.RunShared(() => b.failEvent?.Invoke(), b.failEvent);
                        if (b.resetOnStop) b.curValue = 0f;
                        break;
                    case HoldKind.Finish:
                        if (b.hasFinished) break;
                        b.hasFinished = true;
                        if (b.disableOnFinish) b.canUse = false;
                        b.curValue = b.maxValue;
                        b.progressEvent?.Invoke(b.curValue);
                        ButtonSync.RunShared(() => b.finishEvent?.Invoke(), b.finishEvent);
                        break;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Teammate's hold control failed here ({id}, {kind}): {e.Message}");
            }
            finally
            {
                ButtonSync.Applying = false;
            }
        }

        private void ApplyLever(string id, float angle)
        {
            CL_HoldLever lever = Find<CL_HoldLever>(id);
            if (lever == null || lever.locked || heldLevers.Contains(lever))
            {
                return;
            }
            lever.SetTargetAngle(angle);
        }

        private void ApplyHandTouch(string id)
        {
            UT_HandTrigger trigger = Find<UT_HandTrigger>(id);
            if (trigger == null)
            {
                Plugin.Log.LogInfo("Teammate touched a hand trigger that isn't loaded here: " + id);
                return;
            }
            var hasRun = Traverse.Create(trigger).Field("hasRun");
            if (hasRun.GetValue<bool>())
            {
                return;
            }
            ButtonSync.Applying = true;
            try
            {
                hasRun.SetValue(true);
                ButtonSync.RunShared(() => trigger.onEnter?.Invoke(trigger.transform), trigger.onEnter);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Teammate's hand trigger failed here ({id}): {e.Message}");
            }
            finally
            {
                ButtonSync.Applying = false;
            }
        }
    }

    [HarmonyPatch]
    internal static class InteractionSyncPatches
    {
        // Start of a hold: Interact() bumps presses when it actually starts.
        [HarmonyPatch(typeof(CL_HoldButton), nameof(CL_HoldButton.Interact), new System.Type[0])]
        [HarmonyPrefix]
        private static void HoldStartPrefix(CL_HoldButton __instance, out int __state)
        {
            __state = Traverse.Create(__instance).Field("presses").GetValue<int>();
        }

        [HarmonyPatch(typeof(CL_HoldButton), nameof(CL_HoldButton.Interact), new System.Type[0])]
        [HarmonyPostfix]
        private static void HoldStartPostfix(CL_HoldButton __instance, int __state)
        {
            if (InteractionSync.Live && Traverse.Create(__instance).Field("presses").GetValue<int>() > __state)
            {
                InteractionSync.Instance.SendHold(__instance, InteractionSync.HoldKind.Start);
            }
        }

        // Letting go (or finishing, which also stops).
        [HarmonyPatch(typeof(CL_HoldButton), "Stop")]
        [HarmonyPrefix]
        private static void StopPrefix(CL_HoldButton __instance, out bool __state)
        {
            __state = Traverse.Create(__instance).Field("holding").GetValue<bool>();
        }

        [HarmonyPatch(typeof(CL_HoldButton), "Stop")]
        [HarmonyPostfix]
        private static void StopPostfix(CL_HoldButton __instance, bool __state)
        {
            if (__state && InteractionSync.Live)
            {
                InteractionSync.Instance.SendHold(__instance, InteractionSync.HoldKind.Stop);
            }
        }

        // Releasing before the end fires failEvent.
        [HarmonyPatch(typeof(CL_HoldButton), nameof(CL_HoldButton.StopInteract))]
        [HarmonyPrefix]
        private static void StopInteractPrefix(CL_HoldButton __instance, out bool __state)
        {
            __state = __instance.curValue <= __instance.maxValue && !__instance.hasFinished;
        }

        [HarmonyPatch(typeof(CL_HoldButton), nameof(CL_HoldButton.StopInteract))]
        [HarmonyPostfix]
        private static void StopInteractPostfix(CL_HoldButton __instance, bool __state)
        {
            if (__state && __instance.holdUntil && InteractionSync.Live)
            {
                InteractionSync.Instance.SendHold(__instance, InteractionSync.HoldKind.Fail);
            }
        }

        // Finishing a hold happens in Update.
        [HarmonyPatch(typeof(CL_HoldButton), nameof(CL_HoldButton.Update))]
        [HarmonyPrefix]
        private static void UpdatePrefix(CL_HoldButton __instance, out bool __state)
        {
            __state = __instance.hasFinished;
        }

        [HarmonyPatch(typeof(CL_HoldButton), nameof(CL_HoldButton.Update))]
        [HarmonyPostfix]
        private static void UpdatePostfix(CL_HoldButton __instance, bool __state)
        {
            if (!__state && __instance.hasFinished && InteractionSync.Live)
            {
                InteractionSync.Instance.SendHold(__instance, InteractionSync.HoldKind.Finish);
            }
        }

        // One-shot hand triggers.
        [HarmonyPatch(typeof(UT_HandTrigger), "OnTriggerEnter")]
        [HarmonyPrefix]
        private static void HandPrefix(UT_HandTrigger __instance, out bool __state)
        {
            __state = __instance.runOnce && !Traverse.Create(__instance).Field("hasRun").GetValue<bool>();
        }

        [HarmonyPatch(typeof(UT_HandTrigger), "OnTriggerEnter")]
        [HarmonyPostfix]
        private static void HandPostfix(UT_HandTrigger __instance, bool __state)
        {
            if (__state && Traverse.Create(__instance).Field("hasRun").GetValue<bool>() && InteractionSync.Live)
            {
                InteractionSync.Instance.SendHandTouch(__instance);
            }
        }
    }
}
