using System.Collections.Generic;
using Belay.Players;
using Belay.World;
using UnityEngine;

namespace Belay.Bot
{
    /// <summary>
    /// What a climbing bot carries: picks up items it passes, eats when hurt or low on grip, hands items over from
    /// the Bot tab, and drops everything on death. All via ItemSync.
    /// </summary>
    internal static class BotItems
    {
        public const int MaxItems = 4;
        private const float PickupRadius = 1.8f;
        private const float ScanEvery = 0.4f;
        private const float EatTime = 1.4f;

        private static readonly Collider[] hits = new Collider[64];

        public static void Step(BotPlayer bot, bool youAlive)
        {
            BotClimber c = bot.Climber;
            if (c == null || bot.Dead)
            {
                return;
            }
            Vector3 me = WorldSpace.ToLocal(c.Shared);

            if (bot.EatingTimer > 0f)
            {
                bot.EatingTimer -= Time.deltaTime;
                if (bot.EatingTimer <= 0f)
                {
                    bot.EatingItem = null;
                }
            }
            else
            {
                TryEat(bot, c);
            }

            bot.ItemScan -= Time.deltaTime;
            if (bot.ItemScan > 0f || !bot.PickUpItems || !youAlive || c.Falling || bot.Items.Count >= MaxItems)
            {
                return;
            }
            bot.ItemScan = ScanEvery;
            int n = Physics.OverlapSphereNonAlloc(me, PickupRadius, hits, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                Item_Object obj = hits[i] != null ? hits[i].GetComponentInParent<Item_Object>() : null;
                if (obj == null || !obj.gameObject.activeInHierarchy || !obj.CanPickup())
                {
                    continue;
                }
                Item item = obj.itemData;
                // Story pickups (they set progress flags) and anything without a prefab are left for you.
                if (item == null || string.IsNullOrEmpty(item.prefabName) || item.useFlags || bot.Items.Contains(item))
                {
                    continue;
                }
                if (ItemSync.Instance != null && ItemSync.Instance.BotTake(obj, bot.Id))
                {
                    bot.Items.Add(item);
                    return;
                }
            }
        }

        private static void TryEat(BotPlayer bot, BotClimber c)
        {
            bool hurt = bot.Health < TestBot.MaxHealth * 0.5f;
            bool slipping = c.Hanging && c.GripFraction < 0.25f && (!c.Hands[0].Holding || !c.Hands[1].Holding || c.GripFraction < 0.1f);
            if (!hurt && !slipping)
            {
                return;
            }
            for (int i = 0; i < bot.Items.Count; i++)
            {
                Item item = bot.Items[i];
                HandItem_Food food = FoodOf(item);
                if (food == null)
                {
                    continue;
                }
                bot.Items.RemoveAt(i);
                bot.Health = Mathf.Min(TestBot.MaxHealth, bot.Health + food.healAmount);
                foreach (BotClimber.Hand h in c.Hands)
                {
                    h.Grip = Mathf.Min(BotClimber.GripMax, h.Grip + food.gripAmountAdded);
                }
                bot.EatingItem = item.prefabName;
                bot.EatingTimer = EatTime;
                Plugin.Log.LogInfo($"{bot.Name} ate {item.prefabName}");
                Item_Object obj = item.GetDropObject();
                if (obj != null)
                {
                    Object.Destroy(obj.gameObject);
                }
                return;
            }
        }

        public static HandItem_Food FoodOf(Item item)
        {
            if (item == null)
            {
                return null;
            }
            HandItem asset = item.handItemAsset;
            if (asset == null && item.itemAsset != null && item.itemAsset.itemData != null)
            {
                asset = item.itemAsset.itemData.handItemAsset;
            }
            return asset as HandItem_Food;
        }

        public static string Label(Item item)
        {
            if (item == null)
            {
                return "?";
            }
            return !string.IsNullOrEmpty(item.itemName) ? item.itemName : item.prefabName;
        }

        /// <summary>Pack mule: put one of the bot's items in your bag.</summary>
        public static void GiveToYou(BotPlayer bot, Item item)
        {
            if (!bot.Items.Remove(item))
            {
                return;
            }
            Inventory inv = Inventory.instance;
            if (inv == null || ENT_Player.playerObject == null || ENT_Player.playerObject.dead)
            {
                bot.Items.Add(item);
                return;
            }
            try
            {
                inv.AddItemToInventoryCenter(item);
                Plugin.Log.LogInfo($"{bot.Name} gave you {item.prefabName}");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't hand over {item.prefabName}: {e.Message}");
                DropOne(bot, item, WorldSpace.ToLocal(bot.State.Position));
            }
        }

        /// <summary>Everything it carried falls where it is (it died).</summary>
        public static void DropAll(BotPlayer bot)
        {
            Vector3 at = WorldSpace.ToLocal(bot.State.Position);
            foreach (Item item in new List<Item>(bot.Items))
            {
                DropOne(bot, item, at);
            }
            bot.Items.Clear();
        }

        private static void DropOne(BotPlayer bot, Item item, Vector3 at)
        {
            bot.Items.Remove(item);
            Vector2 r = Random.insideUnitCircle * 0.8f;
            ItemSync.Instance?.BotDrop(item, at + Vector3.up * 0.3f, new Vector3(r.x, 1.5f, r.y));
        }
    }
}
