using System;
using System.Collections.Generic;
using System.Text;
using Belay.Net;
using HarmonyLib;
using UnityEngine;

namespace Belay.Items
{
    /// <summary>
    /// Belay's own co-op items. Each one is a copy of one of the game's item prefabs (its model, sounds and
    /// hand animations) with a new name and id, registered as an extra asset database the way the game's
    /// own add-on databases are. Because they're normal items, they save, drop, show in teammates' hands
    /// and sync like any other. In a co-op run they're sold in the game's vending machines, paid from your
    /// own wallet.
    /// </summary>
    public class CoopItems : MonoBehaviour
    {
        public const string DatabaseId = "belay";

        public class Definition
        {
            /// <summary>Asset id (prefabName and GameObject name). Never rename: saves refer to it.</summary>
            public string Id;
            public string DisplayName;
            /// <summary>Picks the game item to copy.</summary>
            public Func<Item_Object, bool> Template;
            /// <summary>Used when no game item matches <see cref="Template"/>.</summary>
            public Func<Item_Object, bool> Fallback;
            /// <summary>Changes the copied hand item (adds the item's own behaviour).</summary>
            public Action<HandItem> SetupHand;
            /// <summary>Changes the copied world item.</summary>
            public Action<Item_Object> SetupItem;
            public int Price = 3;
            /// <summary>Weight against the machine's other offers (theirs are usually 1).</summary>
            public float Chance = 1f;

            public Item_Object Prefab;
            public Sprite ButtonSprite;
        }

        public static readonly List<Definition> Definitions = new List<Definition>();

        public static CoopItems Instance { get; private set; }

        private static GameObject holder;
        private static bool registered;
        private static readonly Dictionary<Type, Sprite> buttonSprites = new Dictionary<Type, Sprite>();

        public static void Define(Definition definition)
        {
            Definitions.Add(definition);
        }

        public static Definition Find(string id)
        {
            foreach (Definition d in Definitions)
            {
                if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
            }
            return null;
        }

        public static bool IsCoopItem(Item item, string id)
        {
            return item != null && string.Equals(item.prefabName, id, StringComparison.OrdinalIgnoreCase);
        }

        private void Awake()
        {
            Instance = this;
            Harpoon.Define();
            Totem.Define();
        }

        private void Update()
        {
            if (!registered && CL_AssetManager.baseDatabase != null)
            {
                registered = true;
                try
                {
                    Register();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Co-op items not registered: " + e);
                }
            }
        }

        private static void Register()
        {
            CL_AssetManager.InitializeAssetManager();
            WKAssetDatabase baseDb = CL_AssetManager.GetBaseAssetDatabase();
            if (baseDb == null)
            {
                Plugin.Log.LogWarning("Co-op items: no base asset database");
                return;
            }
            LogCatalog(baseDb);

            holder = new GameObject("Belay_ItemTemplates");
            holder.SetActive(false);
            DontDestroyOnLoad(holder);

            WKAssetDatabase db = ScriptableObject.CreateInstance<WKAssetDatabase>();
            db.id = DatabaseId;
            db.name = "Belay_Items";
            db.nullItemObject = baseDb.nullItemObject;

            foreach (Definition def in Definitions)
            {
                Item_Object template = FindTemplate(baseDb, def.Template) ?? FindTemplate(baseDb, def.Fallback);
                if (template == null)
                {
                    Plugin.Log.LogWarning("Co-op item " + def.Id + ": no game item to build it from");
                    continue;
                }
                try
                {
                    def.Prefab = Build(def, template);
                    db.itemPrefabs.Add(def.Prefab.gameObject);
                    Plugin.Log.LogInfo($"Co-op item {def.Id} built from {template.name}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Co-op item " + def.Id + " failed: " + e);
                }
            }
            if (db.itemPrefabs.Count > 0)
            {
                CL_AssetManager.AddNewDatabase(DatabaseId, db);
            }
        }

        private static Item_Object FindTemplate(WKAssetDatabase db, Func<Item_Object, bool> match)
        {
            if (match == null)
            {
                return null;
            }
            foreach (GameObject go in db.itemPrefabs)
            {
                Item_Object io = go != null ? go.GetComponent<Item_Object>() : null;
                if (io == null || io.itemData == null)
                {
                    continue;
                }
                try
                {
                    if (match(io))
                    {
                        return io;
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private static Item_Object Build(Definition def, Item_Object template)
        {
            GameObject copy = Instantiate(template.gameObject, holder.transform);
            copy.name = def.Id;
            Item_Object io = copy.GetComponent<Item_Object>();
            Item data = io.itemData;
            data.itemName = def.DisplayName;
            data.prefabName = def.Id;
            data.itemAsset = io;
            data.unlockType = Item.UnlockType.none;
            data.respawnLimit = -1;
            data.respawnLimitFlagID = "";
            data.useFlags = false;

            HandItem handTemplate = template.itemData.handItemAsset;
            if (handTemplate != null)
            {
                GameObject handCopy = Instantiate(handTemplate.gameObject, holder.transform);
                handCopy.name = def.Id + "_Hand";
                HandItem hand = handCopy.GetComponent<HandItem>();
                data.handItemAsset = hand;
                def.SetupHand?.Invoke(hand);
            }
            def.SetupItem?.Invoke(io);
            return io;
        }

        /// <summary>One log line listing the game's items and their hand item types, to pick templates from.</summary>
        private static void LogCatalog(WKAssetDatabase db)
        {
            var sb = new StringBuilder("Game items: ");
            foreach (GameObject go in db.itemPrefabs)
            {
                Item_Object io = go != null ? go.GetComponent<Item_Object>() : null;
                if (io == null || io.itemData == null)
                {
                    continue;
                }
                HandItem hand = io.itemData.handItemAsset;
                sb.Append(io.itemData.prefabName).Append('=').Append(hand != null ? hand.GetType().Name : "-").Append("; ");
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        // ------------------------------------------------------------------ vending machines

        internal static bool InCoop => NetSession.Instance != null && NetSession.Instance.InLobby;

        internal static void AddToVendor(ENV_VendingMachine vendor)
        {
            if (vendor.purchaseList == null || vendor.fillFromPurchaseListInOrder)
            {
                return;
            }
            // Only general vendors that sell items, not special ones (disk vendors, single-offer machines).
            ENV_VendingMachine.Purchase sample = null;
            int items = 0;
            foreach (ENV_VendingMachine.Purchase p in vendor.purchaseList)
            {
                if (p == null)
                {
                    continue;
                }
                if (p.itemObject != null)
                {
                    items++;
                    sample = sample ?? p;
                    RememberSprite(p);
                }
                if (p.name != null && p.name.StartsWith("Belay_", StringComparison.Ordinal))
                {
                    return; // already added
                }
            }
            if (sample == null || items < 3)
            {
                return;
            }
            foreach (Definition def in Definitions)
            {
                if (def.Prefab == null)
                {
                    continue;
                }
                Sprite sprite = def.ButtonSprite ?? SpriteFor(def) ?? sample.purchaseSprite;
                vendor.purchaseList.Add(new ENV_VendingMachine.Purchase
                {
                    name = def.Id,
                    chance = def.Chance,
                    spawnSettings = new SpawnTable.SpawnSettings(),
                    itemObject = def.Prefab,
                    spawnAssets = new List<GameObject>(),
                    price = def.Price,
                    purchaseSprite = sprite,
                    requiredItemTag = "",
                    competitive = true,
                    ignoreUnlocked = true,
                });
            }
        }

        private static void RememberSprite(ENV_VendingMachine.Purchase p)
        {
            HandItem hand = p.itemObject.itemData?.handItemAsset;
            if (hand != null && p.purchaseSprite != null && !buttonSprites.ContainsKey(hand.GetType()))
            {
                buttonSprites[hand.GetType()] = p.purchaseSprite;
            }
        }

        private static Sprite SpriteFor(Definition def)
        {
            HandItem hand = def.Prefab.itemData.handItemAsset;
            if (hand != null && buttonSprites.TryGetValue(hand.GetType(), out Sprite s))
            {
                return s;
            }
            return def.Prefab.itemData.normalSprite;
        }
    }

    [HarmonyPatch(typeof(ENV_VendingMachine), "GenerateOptions")]
    internal static class VendorOffersPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ENV_VendingMachine __instance)
        {
            if (!CoopItems.InCoop)
            {
                return;
            }
            try
            {
                CoopItems.AddToVendor(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Co-op items not added to a vendor: " + e.Message);
            }
        }
    }
}
