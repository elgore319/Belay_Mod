using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace Belay.Revive
{
    /// <summary>
    /// In co-op, sitting at any terminal while a teammate is down offers a revive for roaches. Disk functions stay
    /// vanilla (team saves, see TeamDiskSave).
    /// </summary>
    [HarmonyPatch(typeof(OS_Manager), nameof(OS_Manager.Activate))]
    internal static class AnyTerminalRevive
    {
        [HarmonyPostfix]
        private static void Postfix(OS_Manager __instance)
        {
            try
            {
                if (!ReviveSystem.TerminalsAreReviveStations || __instance == null || __instance.worldInterface == null
                    || World.VoteTerminal.Is(__instance) || World.TerminalGuard.ReplayingTeammate)
                {
                    return;
                }
                if (ReviveSystem.TerminalStatus().Contains("EVERYONE IS ALIVE"))
                {
                    return;
                }
                ReviveSystem.Instance.StartCoroutine(Offer(__instance));
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Couldn't offer a revive at this terminal: " + e.Message);
            }
        }

        private static IEnumerator Offer(OS_Manager os)
        {
            yield return new WaitForSecondsRealtime(0.6f);
            if (os == null || os.messageManager == null || !os.canControl)
            {
                yield break;
            }
            string status = ReviveSystem.TerminalStatus();
            if (status.Contains("EVERYONE IS ALIVE"))
            {
                yield break;
            }
            Plugin.Log.LogInfo("Offering a revive at a terminal: " + World.ButtonSync.ObjectId(os.worldInterface));
            Show(os, status + "\nRevive them here?", "Revive", () => Show(os, ReviveSystem.TerminalRevive(os), "OK", null, null), "Cancel");
        }

        private static void Show(OS_Manager os, string text, string main, System.Action onMain, string other)
        {
            var packet = new Message_Manager.Message_Packet
            {
                type = "default",
                message = text,
                closeText = main,
                closeFunction = onMain,
                screenPos = Vector2.zero,
            };
            if (other != null)
            {
                packet.aText = other;
                packet.optionAFunction = () => { };
            }
            os.messageManager.CreateMessage(packet);
        }

        private static bool IsSave(OS_Window window) => window != null && window.GetComponent<App_SavePage>() != null;
    }
}
