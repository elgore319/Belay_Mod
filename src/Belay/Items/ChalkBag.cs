using System.Collections.Generic;
using Belay.Net;
using HarmonyLib;
using UnityEngine;

namespace Belay.Items
{
    /// <summary>
    /// The chalk bag: does nothing in your own hands. Throw (or drop) it to a teammate, and when they catch
    /// it, both their hands' grip stamina refills and the bag is used up. Whoever let go of it last is
    /// written into the item's data, which travels with the shared drop.
    /// </summary>
    public static class ChalkBag
    {
        public const string Id = "Belay_ChalkBag";
        private const string ThrowerKey = "belaythrower";

        internal static void Define()
        {
            CoopItems.Define(new CoopItems.Definition
            {
                Id = Id,
                DisplayName = "Chalk Bag",
                Template = io => io.itemData.handItemAsset != null && io.itemData.handItemAsset.GetType() == typeof(HandItem)
                    && io.itemData.pocketable,
                Fallback = io => io.itemData.handItemAsset is HandItem_Food,
                SetupHand = hand => hand.gameObject.AddComponent<NoSelfUse>(),
                SetupItem = io =>
                {
                    io.itemData.data = new List<string>();
                    io.itemData.dataModules = new List<Item_Data>();
                    io.itemData.executionModules = new List<ItemExecutionModule>();
                },
                Price = 2,
                Chance = 0.5f,
            });
        }

        internal static void MarkThrower(Item item)
        {
            if (item.data == null)
            {
                item.data = new List<string>();
            }
            item.data.RemoveAll(d => d != null && d.StartsWith(ThrowerKey + ":"));
            item.data.Add(ThrowerKey + ":" + NetSession.LocalId.Value);
        }

        /// <summary>A teammate threw it to us.</summary>
        internal static bool FromTeammate(Item item)
        {
            string thrower = item.GetFirstDataStringByType(ThrowerKey);
            return !string.IsNullOrEmpty(thrower) && thrower != NetSession.LocalId.Value.ToString();
        }

        internal static void Refill()
        {
            ENT_Player player = ENT_Player.playerObject;
            if (player == null)
            {
                return;
            }
            foreach (ENT_Player.Hand hand in player.hands)
            {
                hand?.SetGripStrength(float.MaxValue);
            }
            Plugin.Log.LogInfo("Caught a chalk bag: grip refilled");
        }
    }

    /// <summary>On a co-op item's hand item: using it in your own hand does nothing.</summary>
    public class NoSelfUse : MonoBehaviour
    {
    }

    [HarmonyPatch]
    internal static class ChalkBagPatches
    {
        [HarmonyPatch(typeof(HandItem), nameof(HandItem.TryUse))]
        [HarmonyPrefix]
        private static bool TryUse(HandItem __instance)
        {
            return __instance.GetComponent<NoSelfUse>() == null;
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.DropItemIntoWorld))]
        [HarmonyPrefix]
        private static void Drop(Inventory __instance, Item item)
        {
            if (__instance == Inventory.instance && CoopItems.IsCoopItem(item, ChalkBag.Id) && NetSession.Instance != null)
            {
                ChalkBag.MarkThrower(item);
            }
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItemToHand), new[] { typeof(Item), typeof(ENT_Player.Hand) })]
        [HarmonyPrefix]
        private static void TakePrefix(Inventory __instance, Item i, out bool __state)
        {
            __state = __instance == Inventory.instance && CoopItems.IsCoopItem(i, ChalkBag.Id) && ChalkBag.FromTeammate(i);
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItemToHand), new[] { typeof(Item), typeof(ENT_Player.Hand) })]
        [HarmonyPostfix]
        private static void TakePostfix(Inventory __instance, ENT_Player.Hand playerHand, bool __state)
        {
            if (!__state || playerHand == null)
            {
                return;
            }
            try
            {
                ChalkBag.Refill();
                __instance.DestroyItemInHand(playerHand.id);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Chalk bag catch failed: " + e.Message);
            }
        }
    }
}
