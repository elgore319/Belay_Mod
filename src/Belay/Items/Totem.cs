using System;
using System.Collections.Generic;
using Belay.Net;
using Belay.Players.Avatar;
using HarmonyLib;
using UnityEngine;

namespace Belay.Items
{
    /// <summary>
    /// The totem: Not Alone's own revive item, built from a copy of the game's save disk (so it has a
    /// disk's weight, pickup sounds and hand animation) but with its own id, name and look. Holding one
    /// and using it brings a dead teammate back right where you are, for free (see SaveTerminalPatches).
    /// It doesn't go into terminals. In co-op it's found where save disks are (half of the save disks
    /// placed in a level are totems instead, picked the same way on every PC) and sold in vending
    /// machines and by the shopkeeper. Every totem, on the ground, in the bag, in your hand or in a
    /// teammate's, wears the look from <see cref="TotemLook"/>.
    /// </summary>
    public static class Totem
    {
        public const string Id = "Belay_Totem";
        public const int Price = 4;

        /// <summary>The game's save disks, the ones a totem can stand in for.</summary>
        private static readonly HashSet<string> SaveDisks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Item_Floppy_T1", "Item_Floppy_T2", "Item_Floppy_T3",
        };

        internal static bool CoOp => NetSession.Instance != null && NetSession.Instance.InLobby;

        internal static bool IsTotem(Item item) => item != null && IsTotem(item.prefabName);

        internal static bool IsTotem(string prefabName) => string.Equals(prefabName, Id, StringComparison.OrdinalIgnoreCase);

        internal static void Define()
        {
            CoopItems.Define(new CoopItems.Definition
            {
                Id = Id,
                DisplayName = "Totem",
                Template = io => string.Equals(io.itemData.prefabName, "Item_Floppy_T1", StringComparison.OrdinalIgnoreCase),
                Fallback = io => (io.itemData.prefabName ?? "").StartsWith("Item_Floppy", StringComparison.OrdinalIgnoreCase),
                SetupItem = SetupItem,
                SetupHand = SetupHand,
                Price = Price,
                Chance = 1f,
            });
        }

        private static void SetupItem(Item_Object io)
        {
            Item data = io.itemData;
            // Not a disk any more: no disk tag (terminals, disk saves and the disk vendor ignore it),
            // no disk name or capacity, nothing stored on it.
            data.itemTag = "totem";
            data.itemTags = new List<string> { "totem" };
            data.data = new List<string>();
            data.dataModules = new List<Item_Data>();
            data.worth = Price;
            CoopItems.Find(Id).ButtonSprite = HandSprite();
        }

        private static void SetupHand(HandItem hand)
        {
            // A disk's hand slots it into terminals; a totem's doesn't.
            hand.interactionModules?.RemoveAll(m => m is Item_InteractionModule_Floppy);
        }

        // ------------------------------------------------------------------ The shopkeeper

        /// <summary>In co-op the shopkeeper has a totem among its goods, at the totem's price.</summary>
        [HarmonyPatch(typeof(ENV_RoachTrader), "Start")]
        private static class ShopkeeperPatch
        {
            [HarmonyPrefix]
            private static void Prefix(ENV_RoachTrader __instance)
            {
                try
                {
                    CoopItems.Definition def = CoopItems.Find(Id);
                    if (!CoOp || def?.Prefab == null || __instance.roachTradeGroups == null)
                    {
                        return;
                    }
                    foreach (ENV_RoachTrader.RoachTradeGroup group in __instance.roachTradeGroups)
                    {
                        if (group?.trades == null || group.trades.Count == 0 || group.trades.Exists(t => t != null && t.item == def.Prefab))
                        {
                            continue;
                        }
                        group.trades.Add(new ENV_RoachTrader.RoachTradeGroup.Trade
                        {
                            spawnSettings = new SpawnTable.SpawnSettings(),
                            item = def.Prefab,
                        });
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Couldn't give the shopkeeper a totem: " + e.Message);
                }
            }
        }

        // ------------------------------------------------------------------ Found in the world

        /// <summary>
        /// Whether a save disk the level just put down is a totem instead. Decided from the run's seed and
        /// the disk's place in its level, so every PC turns the same disks into totems.
        /// </summary>
        private static bool BecomesTotem(Item_Object obj)
        {
            if (!CoOp || obj == null || obj.itemData == null || obj.itemData.hasBeenPickedUp
                || !SaveDisks.Contains(obj.itemData.prefabName ?? ""))
            {
                return false;
            }
            if (obj.GetComponentInParent<M_Level>() == null || World.ItemSync.Instance == null
                || World.ItemSync.Instance.RemoteCopies.Contains(obj))
            {
                return false;
            }
            // Disks a vendor pushes out land next to it, under the same parent: those stay disks.
            Transform parent = obj.transform.parent;
            if (parent != null && (parent.GetComponentInChildren<ENV_Vendor_Disk>(true) != null
                || parent.GetComponentInChildren<ENV_VendingMachine>(true) != null))
            {
                return false;
            }
            string key = NetSession.Instance.CurrentRun.Seed + "|" + World.ButtonSync.ObjectId(obj);
            uint hash = 2166136261;
            foreach (char c in key)
            {
                hash = (hash ^ c) * 16777619;
            }
            return (hash & 1) == 0;
        }

        [HarmonyPatch(typeof(Item_Object), nameof(Item_Object.Start))]
        private static class FoundInWorldPatch
        {
            // Before ItemSync names the item, so it's known as a totem everywhere from the start.
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            private static void Prefix(Item_Object __instance)
            {
                try
                {
                    CoopItems.Definition def = CoopItems.Find(Id);
                    if (def?.Prefab == null || !Players.WorldSpace.Ready || !BecomesTotem(__instance))
                    {
                        return;
                    }
                    __instance.itemData = def.Prefab.itemData.GetClone();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Couldn't place a totem: " + e.Message);
                }
            }
        }

        // ------------------------------------------------------------------ World model

        private static Mesh totemMesh;
        private static Texture2D[] slotTextures;
        private static readonly Dictionary<Material, Material[]> materialsFor = new Dictionary<Material, Material[]>();

        /// <summary>Shows a totem lying in the world (or in the backpack) with its own model.</summary>
        internal static void Wear(Item_Object obj)
        {
            try
            {
                if (obj == null || !IsTotem(obj.itemData))
                {
                    return;
                }
                WorldSkin skin = obj.GetComponent<WorldSkin>();
                if (skin != null)
                {
                    skin.Apply();
                    return;
                }
                MeshFilter filter = null;
                foreach (MeshFilter f in obj.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null)
                    {
                        filter = f;
                        break;
                    }
                }
                if (filter == null)
                {
                    return;
                }
                skin = obj.gameObject.AddComponent<WorldSkin>();
                skin.Setup(filter, obj.GetComponent<BoxCollider>());
                skin.Apply();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't show a totem: " + e.Message);
            }
        }

        internal static Mesh TotemMesh()
        {
            if (totemMesh != null)
            {
                return totemMesh;
            }
            // Upright along the mesh's +Y, centered, faces toward +Z. The disk's model is turned so its
            // +Z points up out of the item, so a dropped totem lies on its back, faces up.
            var kit = new MeshKit(TotemLook.SlotCount);
            TotemLook.Build(kit, new Vector3(0, -TotemLook.Height * 0.5f, 0), Vector3.up, Vector3.forward);
            Mesh mesh = kit.ToMesh("Belay_Totem");
            // One more submesh holding every triangle, for the game's outline material (Unity draws a
            // renderer's extra materials over its last submesh).
            var all = new List<int>();
            foreach (List<int> sub in kit.Submeshes)
            {
                all.AddRange(sub);
            }
            mesh.subMeshCount = TotemLook.SlotCount + 1;
            mesh.SetTriangles(all, TotemLook.SlotCount);
            mesh.RecalculateBounds();
            totemMesh = mesh;
            return mesh;
        }

        private static Texture2D[] SlotTextures()
        {
            if (slotTextures != null)
            {
                return slotTextures;
            }
            PixelTextures.Recipe[] recipes = TotemLook.Recipes();
            slotTextures = new Texture2D[recipes.Length];
            for (int i = 0; i < recipes.Length; i++)
            {
                var texture = new Texture2D(PixelTextures.Size, PixelTextures.Size, TextureFormat.RGBA32, false)
                {
                    name = "Belay_Totem_" + i,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Repeat,
                };
                texture.SetPixels32(PixelTextures.Generate(recipes[i], 401 + i * 7));
                texture.Apply(false, true);
                slotTextures[i] = texture;
            }
            return slotTextures;
        }

        /// <summary>
        /// The totem's materials, made from the disk's own (so it's lit and outlined like every other item
        /// on the ground), with the totem's pixel textures in place of the disk's.
        /// </summary>
        internal static Material[] MaterialsFor(Material[] original)
        {
            Material sheet = original != null && original.Length > 0 ? original[0] : null;
            if (sheet != null && materialsFor.TryGetValue(sheet, out Material[] cached))
            {
                return cached;
            }
            Texture2D[] textures = SlotTextures();
            var slots = new List<Material>();
            if (sheet != null && sheet.HasProperty("_MainTex"))
            {
                for (int i = 0; i < textures.Length; i++)
                {
                    var m = new Material(sheet) { name = "Belay_Totem_" + i, mainTexture = textures[i] };
                    m.mainTextureScale = Vector2.one;
                    m.mainTextureOffset = Vector2.zero;
                    slots.Add(m);
                }
            }
            else
            {
                slots.AddRange(ClimberAvatar.MakeItemMaterials(TotemLook.Recipes(), -1));
            }
            Material glow = slots[TotemLook.GlowSlot];
            if (glow.HasProperty("_EmissionColor"))
            {
                glow.EnableKeyword("_EMISSION");
                glow.SetColor("_EmissionColor", new Color(1f, 0.6f, 0.15f));
            }
            // Keep the disk's extra materials (its outline) after the totem's own.
            for (int i = 1; original != null && i < original.Length; i++)
            {
                slots.Add(original[i]);
            }
            Material[] result = slots.ToArray();
            if (sheet != null)
            {
                materialsFor[sheet] = result;
            }
            return result;
        }

        /// <summary>Puts the totem's model on an item that was built from a disk.</summary>
        internal class WorldSkin : MonoBehaviour
        {
            private MeshFilter filter;
            private MeshRenderer renderer;
            private Material[] originalMaterials;
            private BoxCollider box;
            internal bool Worn { get; private set; }

            internal void Setup(MeshFilter f, BoxCollider collider)
            {
                filter = f;
                renderer = f.GetComponent<MeshRenderer>();
                originalMaterials = renderer.sharedMaterials;
                box = collider;
            }

            internal void Apply()
            {
                if (Worn || filter == null || renderer == null)
                {
                    return;
                }
                Worn = true;
                filter.sharedMesh = TotemMesh();
                renderer.sharedMaterials = MaterialsFor(originalMaterials);
                if (box != null)
                {
                    // Fit the collider to the totem, in the item root's space.
                    Bounds local = filter.sharedMesh.bounds;
                    Matrix4x4 toRoot = box.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                    var b = new Bounds(toRoot.MultiplyPoint3x4(local.center), Vector3.zero);
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 corner = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        b.Encapsulate(toRoot.MultiplyPoint3x4(corner));
                    }
                    box.center = b.center;
                    box.size = b.size;
                }
            }
        }

        [HarmonyPatch(typeof(Item_Object), nameof(Item_Object.Start))]
        private static class ItemObjectStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Item_Object __instance)
            {
                Wear(__instance);
            }
        }

        // ------------------------------------------------------------------ First-person hand

        private static Sprite handSprite;

        internal static Sprite HandSprite()
        {
            if (handSprite != null)
            {
                return handSprite;
            }
            int n = TotemLook.SpriteSize;
            var texture = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                name = "Belay_Totem_Hand",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(TotemLook.DrawSprite());
            texture.Apply(false, true);
            handSprite = Sprite.Create(texture, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), TotemLook.SpritePixelsPerUnit);
            handSprite.name = "Belay_Totem_Hand";
            return handSprite;
        }

        /// <summary>Swaps the disk picture the totem's copied hand still has for the totem's own.</summary>
        internal class HandSkin : MonoBehaviour
        {
            private SpriteRenderer sprite;

            internal void Setup(SpriteRenderer r)
            {
                sprite = r;
            }

            private void LateUpdate()
            {
                if (sprite == null)
                {
                    return;
                }
                Sprite want = HandSprite();
                if (sprite.sprite != want)
                {
                    sprite.sprite = want;
                }
            }
        }

        [HarmonyPatch(typeof(HandItem), nameof(HandItem.Initialize))]
        private static class HandItemPatch
        {
            [HarmonyPostfix]
            private static void Postfix(HandItem __instance, Item i)
            {
                try
                {
                    if (!IsTotem(i))
                    {
                        return;
                    }
                    foreach (SpriteRenderer r in __instance.GetComponentsInChildren<SpriteRenderer>(true))
                    {
                        // The disk picture, not the hand holding it.
                        if (r.sprite == null || r.GetComponent<Hand_SpriteController>() != null
                            || r.sprite.name.IndexOf("Floppy", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                        if (r.GetComponent<HandSkin>() == null)
                        {
                            r.gameObject.AddComponent<HandSkin>().Setup(r);
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Couldn't show a held totem: " + e.Message);
                }
            }
        }
    }
}
