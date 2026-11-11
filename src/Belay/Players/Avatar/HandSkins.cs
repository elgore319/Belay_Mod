using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using Belay.Net;
using Steamworks;
using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// Shows each teammate's equipped White Knuckle hand cosmetic (glove skin, palette and color tweak) on
    /// their avatar's hands.
    ///
    /// Nothing is redrawn or shipped: every player has the same cosmetics installed with the game, so we
    /// only share which one each hand wears (Steam lobby member data, no packets) and draw that
    /// cosmetic's own sprite with the game's own hand material. Bots and anyone whose skin is unknown
    /// wear the local player's skins.
    /// </summary>
    public static class HandSkins
    {
        private const string KeyPrefix = "belay_hand";
        private const string DefaultSkin = "default|0|0|1|1";

        private static ConfigEntry<bool> enabled;
        private static readonly string[] published = new string[2];
        private static float nextPublish;
        private static Dictionary<string, Cosmetic_HandItem> cosmetics;

        public static bool Enabled
        {
            get
            {
                if (enabled == null && Plugin.Instance != null)
                {
                    enabled = Plugin.Instance.Config.Bind("Avatars", "ShowHandSkins", true,
                        "Draw teammates' hands with the glove cosmetics they have equipped.");
                }
                return enabled == null || enabled.Value;
            }
        }

        /// <summary>Shares the local player's hand skins with the lobby when they change. Cheap; call often.</summary>
        public static void PublishTick()
        {
            if (Time.unscaledTime < nextPublish)
            {
                return;
            }
            nextPublish = Time.unscaledTime + 2f;
            NetSession net = NetSession.Instance;
            if (net == null || !net.CurrentLobby.HasValue)
            {
                published[0] = published[1] = null;
                return;
            }
            for (int hand = 0; hand < 2; hand++)
            {
                string skin = LocalSkin(hand);
                if (skin != published[hand])
                {
                    net.CurrentLobby.Value.SetMemberData(KeyPrefix + hand, skin);
                    published[hand] = skin;
                }
            }
        }

        /// <summary>"id|palette|hue|saturation|value" for the cosmetic the local player wears on a hand.</summary>
        public static string LocalSkin(int hand)
        {
            try
            {
                Cosmetic_HandItem cosmetic = CL_CosmeticManager.GetCosmeticInHand(hand);
                if (cosmetic == null || cosmetic.cosmeticInfo == null)
                {
                    return DefaultSkin;
                }
                Vector3 hsv = cosmetic.cosmeticData.hsv;
                return string.Join("|", cosmetic.cosmeticInfo.id, cosmetic.currentPaletteId.ToString(CultureInfo.InvariantCulture),
                    hsv.x.ToString(CultureInfo.InvariantCulture), hsv.y.ToString(CultureInfo.InvariantCulture),
                    hsv.z.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                return DefaultSkin;
            }
        }

        /// <summary>The skin a teammate wears on a hand, or the local player's when they haven't shared one.</summary>
        public static string SkinOf(SteamId id, int hand)
        {
            // Looked up every frame per hand, so keep answers for a second.
            var key = new KeyValuePair<ulong, int>(id.Value, hand);
            if (cache.TryGetValue(key, out KeyValuePair<float, string> hit) && Time.unscaledTime < hit.Key)
            {
                return hit.Value;
            }
            string skin = LookUp(id, hand);
            cache[key] = new KeyValuePair<float, string>(Time.unscaledTime + 1f, skin);
            return skin;
        }

        private static readonly Dictionary<KeyValuePair<ulong, int>, KeyValuePair<float, string>> cache =
            new Dictionary<KeyValuePair<ulong, int>, KeyValuePair<float, string>>();

        private static string LookUp(SteamId id, int hand)
        {
            NetSession net = NetSession.Instance;
            if (net != null && net.CurrentLobby.HasValue)
            {
                string skin = net.CurrentLobby.Value.GetMemberData(new Friend(id), KeyPrefix + hand);
                if (!string.IsNullOrEmpty(skin))
                {
                    return skin;
                }
            }
            return LocalSkin(hand);
        }

        internal static Cosmetic_HandItem FindCosmetic(string id)
        {
            if (cosmetics == null)
            {
                try
                {
                    var found = new Dictionary<string, Cosmetic_HandItem>();
                    foreach (Cosmetic_Base cosmetic in CL_CosmeticManager.GetAllLoadedCosmetics())
                    {
                        if (cosmetic is Cosmetic_HandItem hand && hand.cosmeticInfo != null && !found.ContainsKey(hand.cosmeticInfo.id))
                        {
                            found.Add(hand.cosmeticInfo.id, hand);
                        }
                    }
                    if (found.Count == 0)
                    {
                        return null; // not loaded yet; try again later
                    }
                    cosmetics = found;
                }
                catch (Exception)
                {
                    return null;
                }
            }
            return id != null && cosmetics.TryGetValue(id, out Cosmetic_HandItem item) ? item : null;
        }
    }

    /// <summary>
    /// Builds (and caches) the material that makes a 3D glove look like a hand skin: the skin's own
    /// sprite, palette-mapped and color-tweaked, projected over the back of the hand.
    /// </summary>
    public static class HandSkinMaterials
    {
        private static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();
        private static readonly Dictionary<string, float> failedUntil = new Dictionary<string, float>();

        /// <summary>The glove material for a skin on a hand, or null when it can't be built (yet).</summary>
        public static Material For(string skin, int hand, Shader shader)
        {
            if (!HandSkins.Enabled || string.IsNullOrEmpty(skin) || shader == null)
            {
                return null;
            }
            string key = hand + "/" + skin;
            if (cache.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }
            if (failedUntil.TryGetValue(key, out float until) && Time.unscaledTime < until)
            {
                return null;
            }
            Material built = null;
            try
            {
                built = Build(skin, hand, shader);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Hand skin " + skin + " unavailable: " + e.Message);
            }
            if (built == null)
            {
                failedUntil[key] = Time.unscaledTime + 5f;
                return null;
            }
            cache[key] = built;
            return built;
        }

        private static Material Build(string skin, int hand, Shader shader)
        {
            ENT_Player player = ENT_Player.playerObject;
            if (player == null || player.hands == null || player.hands.Length <= hand)
            {
                return null;
            }
            ENT_Player.Hand localHand = player.hands[hand];
            string[] parts = skin.Split('|');
            Cosmetic_HandItem cosmetic = HandSkins.FindCosmetic(parts[0]);
            int paletteIndex = parts.Length > 1 && int.TryParse(parts[1], out int pi) ? pi : 0;

            Texture palette = null;
            Vector3? hsv = null;
            if (cosmetic != null && cosmetic.cosmeticData != null)
            {
                Cosmetic_HandItem.Cosmetic_HandItem_Data data = cosmetic.cosmeticData;
                if (data.palettes != null && data.palettes.Count > 0)
                {
                    palette = data.palettes[Mathf.Clamp(paletteIndex, 0, data.palettes.Count - 1)].texture;
                }
                // Only skins with the game's color sliders carry a hue/saturation/value tweak.
                if (cosmetic.cosmeticInfo.flags != null && cosmetic.cosmeticInfo.flags.Contains("setting-hsv"))
                {
                    var tweak = new Vector3(Part(parts, 2, data.hsv.x), Part(parts, 3, data.hsv.y), Part(parts, 4, data.hsv.z));
                    if (tweak.y > 0.01f || tweak.z > 0.01f)
                    {
                        hsv = tweak;
                    }
                }
            }
            // Without a palette of its own a skin is drawn with its sprite's own colors (the game sets
            // _PaletteBaseBlend to 0), so never borrow the palette of whatever the local hand wears.
            Color32[] palettePixels = palette != null ? TextureReader.Read(palette, new Rect(0, 0, palette.width, 1), false) : null;
            SkinBaker.Effect effect = EffectOf(cosmetic);

            // The game's "normal" pose is the back of the hand; "open" shows the palm.
            SkinBaker.Result? back = Picture(localHand, cosmetic, hand, "normal", palettePixels, hsv, effect);
            if (!back.HasValue)
            {
                return null;
            }
            SkinBaker.Result palm = Picture(localHand, cosmetic, hand, "open", palettePixels, hsv, effect) ?? back.Value;

            // Our glove UVs put the thumb on the right of the back picture. The skin art for each hand
            // shows that hand from behind once the game's own flip is applied, drawn as a left hand.
            bool flip = localHand.handSprite != null && localHand.handSprite.flipX;
            bool mirror = flip != (hand == 1);
            const int cellWidth = 64, cellHeight = 96;
            var texture = new Texture2D(cellWidth * 2, cellHeight, TextureFormat.RGBA32, false)
            {
                name = "Belay_HandSkin_" + parts[0],
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(SkinBaker.Atlas(back.Value, palm, mirror, cellWidth, cellHeight));
            texture.Apply(false, true);

            var material = new Material(shader) { name = "Belay_HandSkin", color = Color.white, mainTexture = texture };
            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                material.SetTexture("_EmissionMap", texture);
                material.SetColor("_EmissionColor", Color.white * ClimberAvatar.SelfLight);
            }
            if (material.HasProperty("_Glossiness"))
            {
                material.SetFloat("_Glossiness", 0.05f);
            }
            return material;
        }

        /// <summary>
        /// Skins like Inverted, Experienced or Celestial have no art of their own: a special material
        /// recolors the default hand. Imitate the common ones.
        /// </summary>
        private static SkinBaker.Effect EffectOf(Cosmetic_HandItem cosmetic)
        {
            Cosmetic_HandItem.Cosmetic_HandItem_Data data = cosmetic?.cosmeticData;
            if (data == null || string.IsNullOrEmpty(data.globalMaterialBase))
            {
                return null;
            }
            string name = null;
            Color32[] pixels = null;
            int w = 0, h = 0;
            if (data.useGlobalSecondary && data.globalSecondary != null)
            {
                foreach (Cosmetic_HandItem.SwapSprite.SecondaryTextures secondary in data.globalSecondary)
                {
                    if ((secondary.name == "_OverlayMult" || secondary.name == "_StaminaTexture")
                        && secondary.secondaryTextureSprites != null && secondary.secondaryTextureSprites.Count > 0
                        && secondary.secondaryTextureSprites[0] != null)
                    {
                        Sprite sprite = secondary.secondaryTextureSprites[0];
                        Rect rect = sprite.textureRect;
                        pixels = TextureReader.Read(sprite.texture, rect, false);
                        w = Mathf.RoundToInt(rect.width);
                        h = Mathf.RoundToInt(rect.height);
                        name = secondary.name;
                        break;
                    }
                }
            }
            return SkinBaker.Effect.For(data.globalMaterialBase, name, pixels, w, h);
        }

        private static Sprite OverlayOf(List<Cosmetic_HandItem.SwapSprite.SecondaryTextures> secondaries)
        {
            if (secondaries == null)
            {
                return null;
            }
            foreach (Cosmetic_HandItem.SwapSprite.SecondaryTextures secondary in secondaries)
            {
                if (secondary.name == "_OverlayMult" && secondary.secondaryTextureSprites != null
                    && secondary.secondaryTextureSprites.Count > 0 && secondary.secondaryTextureSprites[0] != null)
                {
                    return secondary.secondaryTextureSprites[0];
                }
            }
            return null;
        }

        private static SkinBaker.Result? Picture(ENT_Player.Hand localHand, Cosmetic_HandItem cosmetic, int hand, string pose,
            Color32[] palette, Vector3? hsv, SkinBaker.Effect effect)
        {
            Sprite sprite = localHand.GetPoseSprite(pose);
            if (sprite == null)
            {
                return null;
            }
            List<Cosmetic_HandItem.SwapSprite> swaps = cosmetic?.cosmeticData?.swapSprites;
            if (swaps != null)
            {
                string baseName = sprite.name;
                foreach (Cosmetic_HandItem.SwapSprite swap in swaps)
                {
                    if (swap.spriteName != baseName || (swap.hand != -1 && swap.hand != hand))
                    {
                        continue;
                    }
                    if (swap.replacementSprites != null && swap.replacementSprites.Count > 0 && swap.replacementSprites[0] != null)
                    {
                        sprite = swap.replacementSprites[0];
                    }
                    // Some skins (Bloodied) keep the default art and multiply a picture over it.
                    Sprite overlay = OverlayOf(swap.secondaryTextures);
                    if (overlay != null)
                    {
                        Rect o = overlay.textureRect;
                        Color32[] px = TextureReader.Read(overlay.texture, o, false);
                        if (px != null)
                        {
                            effect = (effect ?? new SkinBaker.Effect()).WithMultiply(px, Mathf.RoundToInt(o.width), Mathf.RoundToInt(o.height));
                        }
                    }
                    break;
                }
            }
            // Read the sprite's whole frame (not just its trimmed pixels), so overlays drawn for the
            // full frame line up.
            Rect rect = sprite.rect;
            if (rect.xMin < 0 || rect.yMin < 0 || rect.xMax > sprite.texture.width || rect.yMax > sprite.texture.height
                || !sprite.textureRect.Overlaps(rect))
            {
                rect = sprite.textureRect;
            }
            Color32[] pixels = TextureReader.Read(sprite.texture, rect, palette != null);
            if (pixels == null)
            {
                return null;
            }
            return SkinBaker.Bake(pixels, Mathf.RoundToInt(rect.width), Mathf.RoundToInt(rect.height), palette, hsv, effect);
        }

        private static float Part(string[] parts, int index, float fallback)
        {
            return parts.Length > index && float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }
    }

    /// <summary>Reads pixels from any texture, readable or not, without changing the stored values.</summary>
    internal static class TextureReader
    {
        public static Color32[] Read(Texture texture, Rect rect, bool expectIndexed)
        {
            if (texture == null)
            {
                return null;
            }
            int x = Mathf.RoundToInt(rect.x), y = Mathf.RoundToInt(rect.y);
            int w = Mathf.RoundToInt(rect.width), h = Mathf.RoundToInt(rect.height);
            if (texture is Texture2D t2 && t2.isReadable)
            {
                Color32[] all = t2.GetPixels32();
                return Crop(all, t2.width, x, y, w, h);
            }
            // Not readable: copy through the GPU. Try a raw copy first, then an sRGB round trip, and keep
            // whichever preserves palette indexes when we expect them.
            Color32[] linear = Blit(texture, x, y, w, h, RenderTextureReadWrite.Linear);
            if (!expectIndexed || IndexLike(linear))
            {
                return linear;
            }
            Color32[] srgb = Blit(texture, x, y, w, h, RenderTextureReadWrite.sRGB);
            return IndexLike(srgb) ? srgb : linear;
        }

        private static Color32[] Blit(Texture texture, int x, int y, int w, int h, RenderTextureReadWrite mode)
        {
            RenderTexture rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, mode);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, mode == RenderTextureReadWrite.Linear);
                copy.ReadPixels(new Rect(x, y, w, h), 0, 0, false);
                copy.Apply(false);
                Color32[] pixels = copy.GetPixels32();
                UnityEngine.Object.Destroy(copy);
                return pixels;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static bool IndexLike(Color32[] pixels)
        {
            if (pixels == null)
            {
                return false;
            }
            int opaque = 0;
            foreach (Color32 p in pixels)
            {
                if (p.a < 128)
                {
                    continue;
                }
                opaque++;
                if (p.g != 0 || p.b != 0 || p.r >= 64)
                {
                    return false;
                }
            }
            return opaque > 0;
        }

        private static Color32[] Crop(Color32[] all, int stride, int x, int y, int w, int h)
        {
            if (x == 0 && y == 0 && w == stride && all.Length == w * h)
            {
                return all;
            }
            var result = new Color32[w * h];
            for (int row = 0; row < h; row++)
            {
                Array.Copy(all, (y + row) * stride + x, result, row * w, w);
            }
            return result;
        }
    }
}
