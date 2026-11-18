using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Belay.Revive
{
    /// <summary>
    /// In co-op, sitting at a terminal while a teammate is down asks whether to revive them for roaches.
    /// The save page's disk functions are the game's own again (disks make team saves, see TeamDiskSave),
    /// so the offer comes up at every terminal, save terminals included.
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
            Show(os, status + "\nRevive them here?", "Revive", () => Show(os, ReviveSystem.TerminalRevive(), "OK", null, null), "Cancel");
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

        private static readonly Dictionary<int, bool> hasSaveApp = new Dictionary<int, bool>();

        /// <summary>Whether this terminal's desktop (any of its disk variants) has the save program.</summary>
        internal static bool HasSaveApp(OS_Computer_Interface terminal)
        {
            int key = terminal.GetInstanceID();
            if (hasSaveApp.TryGetValue(key, out bool known))
            {
                return known;
            }
            bool found = false;
            // The program may already be open as a window on this terminal.
            if (terminal.GetComponentInChildren<App_SavePage>(true) != null)
            {
                found = true;
            }
            OS_Manager os = terminal.GetComputer();
            if (!found && os != null && os.GetComponentInChildren<App_SavePage>(true) != null)
            {
                found = true;
            }
            if (!found && terminal.desktopData != null)
            {
                found = DiskHasSave(terminal.desktopData, 0);
            }
            hasSaveApp[key] = found;
            Plugin.Log.LogInfo($"Terminal {World.ButtonSync.ObjectId(terminal)} {(found ? "has" : "has no")} save program");
            return found;
        }

        private static bool DiskHasSave(OS_DiskData disk, int depth)
        {
            if (disk == null || depth > 3)
            {
                return false;
            }
            if (FilesHaveSave(disk.files, 0))
            {
                return true;
            }
            if (disk.diskDataSelectors != null)
            {
                foreach (OS_DiskData.DiskDataSelector sel in disk.diskDataSelectors)
                {
                    if (sel != null && FilesHaveSave(sel.files, 0)) return true;
                }
            }
            if (disk.randomDisks != null)
            {
                foreach (OS_DiskData d in disk.randomDisks)
                {
                    if (DiskHasSave(d, depth + 1)) return true;
                }
            }
            return false;
        }

        private static bool FilesHaveSave(List<OS_Filesystem.FileInfo> files, int depth)
        {
            if (files == null || depth > 6)
            {
                return false;
            }
            foreach (OS_Filesystem.FileInfo f in files)
            {
                if (f == null) continue;
                if (IsSave(f.windowAsset)) return true;
                if (f.fileDataAsset != null && f.fileDataAsset.fileInfo != null && IsSave(f.fileDataAsset.fileInfo.windowAsset)) return true;
                if (FilesHaveSave(f.children, depth + 1)) return true;
            }
            return false;
        }

        private static bool IsSave(OS_Window window) => window != null && window.GetComponent<App_SavePage>() != null;
    }
}
