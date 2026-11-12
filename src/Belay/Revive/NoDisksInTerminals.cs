using HarmonyLib;
using UnityEngine;

namespace Belay.Revive
{
    /// <summary>
    /// In co-op, disks don't go into terminals: disk saves are off (they'd send you back below the team)
    /// and a disk you hold is a free revive instead. Holding a disk up to a terminal's slot does nothing
    /// and says why. Other slots (the ATM) are untouched. This runs on every player's PC, host or not.
    /// </summary>
    [HarmonyPatch]
    internal static class NoDisksInTerminals
    {
        private static bool CoOp => ReviveSystem.TerminalsAreReviveStations;
        private static float lastNotice = -10f;

        private static void Tell()
        {
            if (Time.unscaledTime - lastNotice > 3f)
            {
                lastNotice = Time.unscaledTime;
            }
        }

        [HarmonyPatch(typeof(Item_InteractionModule_Floppy), nameof(Item_InteractionModule_Floppy.Interact))]
        [HarmonyPrefix]
        private static bool ModulePrefix(Interaction interaction, ref Sprite interactSprite)
        {
            if (!CoOp || interaction == null || interaction.clickable == null)
            {
                return true;
            }
            GameObject target = interaction.clickable.GetGameObject();
            if (target == null || target.GetComponent<OS_FloppyReader>() == null)
            {
                return true;
            }
            interactSprite = null;
            if (InputManager.GetButton(interaction.fireButton).Down)
            {
                Tell();
            }
            return false;
        }

        // Backstops, in case something else feeds a disk to a terminal.
        [HarmonyPatch(typeof(OS_FloppyReader), nameof(OS_FloppyReader.InsertFloppy))]
        [HarmonyPrefix]
        private static bool InsertPrefix()
        {
            if (!CoOp)
            {
                return true;
            }
            Tell();
            return false;
        }

        [HarmonyPatch(typeof(OS_FloppyReader), nameof(OS_FloppyReader.CanInteract))]
        [HarmonyPrefix]
        private static bool CanInteractPrefix(ref bool __result)
        {
            if (!CoOp)
            {
                return true;
            }
            __result = false;
            return false;
        }
    }
}
