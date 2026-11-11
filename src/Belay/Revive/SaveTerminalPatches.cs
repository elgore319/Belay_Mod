using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Belay.Revive
{
    /// <summary>
    /// Co-op replaces the save terminal's disk function with a revive station. In a lobby:
    /// - The "reformat disks" action (which made disk saves) revives a dead teammate for roaches instead.
    ///   Physical disks aren't needed, used or wiped.
    /// - Loading a disk save is blocked: it put you back at the terminal you saved at, usually far below
    ///   the team and in the mass's path, and regenerated the map differently from everyone else's.
    /// Solo play is untouched.
    /// </summary>
    [HarmonyPatch(typeof(App_SavePage))]
    internal static class SaveTerminalPatches
    {
        private static bool CoOp => ReviveSystem.TerminalsAreReviveStations;

        private static OS_Window WindowOf(App_SavePage page) => Traverse.Create(page).Field("window").GetValue<OS_Window>();

        private static void ShowMessage(App_SavePage page, string text, Action proceed = null)
        {
            OS_Window window = WindowOf(page);
            OS_Manager os = window != null ? Traverse.Create(window).Field("os").GetValue<OS_Manager>() : null;
            if (os == null || os.messageManager == null)
            {
                Run.RunFlow.Instance?.ShowNotice(text);
                return;
            }
            var packet = new Message_Manager.Message_Packet
            {
                type = "default",
                message = text,
                screenPos = Vector2.zero,
            };
            if (proceed != null)
            {
                packet.closeText = "Revive";
                packet.aText = "Cancel";
                packet.closeFunction = proceed;
                packet.optionAFunction = () => { };
            }
            else
            {
                packet.closeText = "OK";
            }
            os.messageManager.CreateMessage(packet);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(App_SavePage.StartReformat))]
        private static bool StartReformatPrefix(App_SavePage __instance)
        {
            if (!CoOp)
            {
                return true;
            }
            string status = ReviveSystem.TerminalStatus();
            if (status.Contains("EVERYONE IS ALIVE"))
            {
                ShowMessage(__instance, "Co-op: this terminal is a revive station. Everyone is alive right now.");
            }
            else
            {
                ShowMessage(__instance, "Co-op: this terminal is a revive station.\n" + status + "\nRevive them here?",
                    () => ShowMessage(__instance, ReviveSystem.TerminalRevive()));
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(App_SavePage.AddSave))]
        private static bool AddSavePrefix() => !CoOp;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(App_SavePage.LoadSave))]
        private static bool LoadSavePrefix(App_SavePage __instance)
        {
            if (!CoOp)
            {
                return true;
            }
            ShowMessage(__instance, "Disk saves are off in co-op. They'd put you back at the terminal you saved at, below your team.");
            return false;
        }

        /// <summary>
        /// The revive page doesn't need disks, so don't send the player to the "no disks" screen. The
        /// switch is skipped outright: ChangeMenu("reformat") with no disks calls ChangeMenu("nodisks")
        /// itself, so turning that call back into "reformat" (as 0.5.17 did) recursed until the game
        /// crashed with a stack overflow.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(App_SavePage.ChangeMenu))]
        private static bool ChangeMenuPrefix(string menuName) => !(CoOp && menuName == "nodisks");

        private static Type tmpTextType;
        private static readonly System.Text.RegularExpressions.Regex Relabel = new System.Text.RegularExpressions.Regex(
            "biological backup|reformat(ting)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Relabel the page for co-op ("biological backup"/"reformat" become revive wording) and log every
        /// label once, so the wording can be tuned to the real screen text.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("OnOpen")]
        private static void OnOpenPostfix(App_SavePage __instance)
        {
            if (!CoOp)
            {
                return;
            }
            try
            {
                tmpTextType = tmpTextType ?? AccessTools.TypeByName("TMPro.TMP_Text");
                if (tmpTextType == null)
                {
                    return;
                }
                var textProp = AccessTools.Property(tmpTextType, "text");
                foreach (Component label in __instance.GetComponentsInChildren(tmpTextType, true))
                {
                    string text = textProp.GetValue(label) as string;
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }
                    Plugin.Log.LogInfo($"Save terminal label '{label.name}': {text.Replace('\n', ' ')}");
                    string relabeled = Relabel.Replace(text, m =>
                    {
                        string word = m.Value.ToLowerInvariant().StartsWith("bio") ? "teammate revive" : m.Value.ToLowerInvariant() == "reformatting" ? "reviving" : "revive";
                        return m.Value == m.Value.ToUpperInvariant() ? word.ToUpperInvariant()
                            : char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(word[0]) + word.Substring(1) : word;
                    });
                    if (relabeled != text)
                    {
                        textProp.SetValue(label, relabeled);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't relabel the save terminal: " + e.Message);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("UpdateSaveText")]
        private static void UpdateSaveTextPostfix(App_SavePage __instance)
        {
            if (!CoOp)
            {
                return;
            }
            try
            {
                // floppyText is a TextMeshPro label; Belay doesn't reference TextMeshPro, so set it by name.
                object label = Traverse.Create(__instance).Field("floppyText").GetValue();
                if (label is UnityEngine.Object o && o != null)
                {
                    AccessTools.Property(label.GetType(), "text")?.SetValue(label, ReviveSystem.TerminalStatus());
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't relabel the save terminal: " + e.Message);
            }
        }
    }

    /// <summary>
    /// In a lobby, using a disk you're holding (the normal use button) revives a dead teammate right
    /// where you are, for free, and uses up the disk. With nobody dead, the disk works as it normally does.
    /// </summary>
    [HarmonyPatch(typeof(Item), nameof(Item.Use))]
    internal static class DiskInHandPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Item __instance)
        {
            if (__instance == null || !ReviveSystem.TerminalsAreReviveStations || !__instance.HasTag("disk"))
            {
                return true;
            }
            if (!ReviveSystem.DiskRevive())
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
                Plugin.Log.LogWarning("Disk revive: couldn't find the hand holding the disk");
            }
            return false;
        }
    }

    /// <summary>In a lobby, disks can't be inserted into terminals: they're held revive items instead.</summary>
    [HarmonyPatch(typeof(OS_FloppyReader))]
    internal static class FloppyReaderPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(OS_FloppyReader.CanInteract))]
        private static void CanInteractPostfix(ref bool __result)
        {
            if (ReviveSystem.TerminalsAreReviveStations)
            {
                __result = false;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(OS_FloppyReader.InsertFloppy))]
        private static bool InsertFloppyPrefix() => !ReviveSystem.TerminalsAreReviveStations;
    }
}
