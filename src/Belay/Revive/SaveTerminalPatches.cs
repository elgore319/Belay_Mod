using System;
using HarmonyLib;
using UnityEngine;

namespace Belay.Revive
{
    /// <summary>
    /// The save terminal's disk functions are the game's own in co-op too: insert disks and reformat
    /// them into a disk save. In co-op that save belongs to the whole team (TeamDiskSave): it's only used
    /// when everyone is down, and then everyone comes back from it together. Loading it by hand from the
    /// terminal stays off, since that would send one player back alone.
    /// </summary>
    [HarmonyPatch(typeof(App_SavePage))]
    internal static class SaveTerminalPatches
    {
        private static bool CoOp => ReviveSystem.TerminalsAreReviveStations;

        private static OS_Window WindowOf(App_SavePage page) => Traverse.Create(page).Field("window").GetValue<OS_Window>();

        private static OS_Manager OsOf(App_SavePage page)
        {
            OS_Window window = WindowOf(page);
            return window != null ? Traverse.Create(window).Field("os").GetValue<OS_Manager>() : null;
        }

        private static void ShowMessage(App_SavePage page, string text)
        {
            OS_Manager os = OsOf(page);
            if (os == null || os.messageManager == null)
            {
                Run.RunFlow.Instance?.ShowNotice(text);
                return;
            }
            os.messageManager.CreateMessage(new Message_Manager.Message_Packet
            {
                type = "default",
                message = text,
                screenPos = Vector2.zero,
                closeText = "OK",
            });
        }

        /// <summary>A disk save made here is the team's: tell everyone.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(App_SavePage.AddSave))]
        private static void AddSavePostfix(App_SavePage __instance)
        {
            if (!CoOp)
            {
                return;
            }
            try
            {
                OS_Manager os = OsOf(__instance);
                TeamDiskSave.Share(os != null && os.worldInterface != null ? os.worldInterface.computerID : null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't share the disk save with the team: " + e.Message);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(App_SavePage.LoadSave))]
        private static bool LoadSavePrefix(App_SavePage __instance)
        {
            if (!CoOp)
            {
                return true;
            }
            ShowMessage(__instance, "In co-op, a disk save brings the whole team back when everyone is down.");
            return false;
        }
    }

    /// <summary>
    /// In a lobby, using a totem you're holding (the normal use button) revives a dead teammate right
    /// where you are, for free, and uses up the totem.
    /// </summary>
    [HarmonyPatch(typeof(Item), nameof(Item.Use))]
    internal static class TotemInHandPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Item __instance)
        {
            if (__instance == null || !ReviveSystem.TerminalsAreReviveStations || !Items.Totem.IsTotem(__instance))
            {
                return true;
            }
            if (!ReviveSystem.HeldTotemRevive())
            {
                return true;
            }
            ENT_Player.Hand hand = __instance.GetHandItem()?.hand;
            if (hand != null)
            {
                hand.DestroyItem();
            }
            else
            {
                Plugin.Log.LogWarning("Totem revive: couldn't find the hand holding the totem");
            }
            return false;
        }
    }
}
