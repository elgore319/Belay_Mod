using System;
using System.Collections.Generic;
using Belay.Net;
using Belay.Players.Avatar;
using HarmonyLib;
using UnityEngine;

namespace Belay.Items
{
    /// <summary>
    /// In co-op a disk is a revive item, so it looks like one: every disk (on the ground, in the backpack,
    /// in your hand and in teammates' hands) is shown as the totem from <see cref="TotemLook"/>. Only the
    /// look changes: the item, its name in saves, its weight and how it's used stay the game's disk.
    /// Solo games keep the plain disk, and leaving the lobby turns totems back into disks.
    /// </summary>
    public class DiskTotem : MonoBehaviour
    {
        internal static bool CoOp => NetSession.Instance != null && NetSession.Instance.InLobby;

        internal static bool IsDisk(Item item)
        {
            return item != null && (item.HasTag("disk") || (item.prefabName ?? "").StartsWith("Item_Floppy", StringComparison.OrdinalIgnoreCase));
        }

        private bool wasCoOp;
        private float nextCheck;

        private void Update()
        {
            if (Time.unscaledTime < nextCheck)
            {
                return;
            }
            nextCheck = Time.unscaledTime + 0.5f;
            bool now = CoOp;
            if (now == wasCoOp)
            {
                return;
            }
            wasCoOp = now;
            // Disks already lying around (or in the bag) when the lobby starts or ends.
            foreach (Item_Object obj in FindObjectsByType<Item_Object>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (now)
                {
                    Wear(obj);
                }
                else
                {
                    obj.GetComponent<WorldSkin>()?.Restore();
                }
            }
        }

        // ------------------------------------------------------------------ World model

        private static Mesh totemMesh;
        private static Texture2D[] slotTextures;
        private static readonly Dictionary<Material, Material[]> materialsFor = new Dictionary<Material, Material[]>();

        /// <summary>Shows a disk lying in the world (or in the backpack) as the totem.</summary>
        internal static void Wear(Item_Object obj)
        {
            try
            {
                if (obj == null || !IsDisk(obj.itemData))
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
                Plugin.Log.LogWarning("Couldn't show a disk as a totem: " + e.Message);
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

        /// <summary>Remembers a disk's own model so the totem can be taken off again.</summary>
        internal class WorldSkin : MonoBehaviour
        {
            private MeshFilter filter;
            private MeshRenderer renderer;
            private Mesh originalMesh;
            private Material[] originalMaterials;
            private BoxCollider box;
            private Vector3 boxCenter, boxSize;
            internal bool Worn { get; private set; }

            internal void Setup(MeshFilter f, BoxCollider collider)
            {
                filter = f;
                renderer = f.GetComponent<MeshRenderer>();
                originalMesh = f.sharedMesh;
                originalMaterials = renderer.sharedMaterials;
                box = collider;
                if (box != null)
                {
                    boxCenter = box.center;
                    boxSize = box.size;
                }
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

            internal void Restore()
            {
                if (!Worn || filter == null || renderer == null)
                {
                    return;
                }
                Worn = false;
                filter.sharedMesh = originalMesh;
                renderer.sharedMaterials = originalMaterials;
                if (box != null)
                {
                    box.center = boxCenter;
                    box.size = boxSize;
                }
            }
        }

        [HarmonyPatch(typeof(Item_Object), nameof(Item_Object.Start))]
        private static class ItemObjectStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix(Item_Object __instance)
            {
                if (CoOp)
                {
                    Wear(__instance);
                }
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

        /// <summary>Swaps the disk picture in a held disk for the totem, and back if the lobby ends.</summary>
        internal class HandSkin : MonoBehaviour
        {
            private SpriteRenderer sprite;
            private Sprite original;

            internal void Setup(SpriteRenderer r)
            {
                sprite = r;
                original = r.sprite;
            }

            private void LateUpdate()
            {
                if (sprite == null)
                {
                    return;
                }
                Sprite want = CoOp ? HandSprite() : original;
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
                    if (!IsDisk(i))
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
                    Plugin.Log.LogWarning("Couldn't show a held disk as a totem: " + e.Message);
                }
            }
        }
    }
}
