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
            if (net == null || !net.InLobby)
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
            if (net != null && net.InLobby)
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

    /// <summary>One teammate hand drawn as the game's hand sprite, as a billboard at the end of the arm.</summary>
    public class HandSkinRenderer
    {
        private static readonly int PaletteBase = Shader.PropertyToID("_PaletteBase");
        private static readonly int PaletteBaseBlend = Shader.PropertyToID("_PaletteBaseBlend");
        private static readonly int HSV = Shader.PropertyToID("_HSV");
        private static readonly int StaminaAmount = Shader.PropertyToID("_StaminaAmount");

        private readonly int hand;
        private readonly SpriteRenderer renderer;
        private Material material;
        private string skin;
        private bool grip;
        private bool ready;
        private float retryAt;

        public HandSkinRenderer(Transform parent, int hand)
        {
            this.hand = hand;
            var go = new GameObject(hand == 0 ? "LeftHandSkin" : "RightHandSkin");
            go.transform.SetParent(parent, false);
            renderer = go.AddComponent<SpriteRenderer>();
            renderer.enabled = false;
        }

        /// <summary>
        /// Draws the hand at <paramref name="palm"/> with its fingers pointing along
        /// <paramref name="forearm"/> on screen. Returns false (and draws nothing) when the skin can't be
        /// shown, so the caller keeps the modeled glove.
        /// </summary>
        public bool Show(string wantedSkin, bool gripping, Vector3 palm, Vector3 forearm, float size)
        {
            Camera cam = Camera.main;
            if (!HandSkins.Enabled || cam == null)
            {
                Hide();
                return false;
            }
            if (wantedSkin != skin || gripping != grip || (!ready && Time.unscaledTime >= retryAt))
            {
                skin = wantedSkin;
                grip = gripping;
                ready = Apply();
                if (!ready)
                {
                    retryAt = Time.unscaledTime + 3f;
                }
            }
            if (!ready)
            {
                Hide();
                return false;
            }

            Transform camTransform = cam.transform;
            Vector3 toHand = palm - camTransform.position;
            // Fingers point down the arm as seen from this camera.
            float angle = Mathf.Atan2(Vector3.Dot(forearm, camTransform.right), Vector3.Dot(forearm, camTransform.up)) * Mathf.Rad2Deg;
            Quaternion facing = Quaternion.LookRotation(toHand, camTransform.up) * Quaternion.Euler(0, 0, -angle);
            // Sit just in front of the glove mesh so the sleeve doesn't cut through the sprite.
            renderer.transform.SetPositionAndRotation(palm - toHand.normalized * 0.05f, facing);
            Vector3 bounds = renderer.sprite.bounds.size;
            float scale = size / Mathf.Max(0.0001f, Mathf.Max(bounds.x, bounds.y));
            renderer.transform.localScale = new Vector3(scale, scale, scale);
            renderer.enabled = true;
            return true;
        }

        public void Hide()
        {
            if (renderer.enabled)
            {
                renderer.enabled = false;
            }
        }

        private bool Apply()
        {
            try
            {
                ENT_Player player = ENT_Player.playerObject;
                if (player == null || player.hands == null || player.hands.Length <= hand)
                {
                    return false;
                }
                ENT_Player.Hand localHand = player.hands[hand];
                Sprite baseSprite = localHand.GetPoseSprite(grip ? "grab" : "normal");
                Material baseMaterial = localHand.handSprite != null ? localHand.handSprite.sharedMaterial : null;
                if (baseSprite == null || baseMaterial == null)
                {
                    return false;
                }

                string[] parts = (skin ?? "").Split('|');
                Cosmetic_HandItem cosmetic = HandSkins.FindCosmetic(parts.Length > 0 ? parts[0] : null);
                int palette = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 0;
                Vector3 hsv = new Vector3(Part(parts, 2, 0f), Part(parts, 3, 1f), Part(parts, 4, 1f));

                Sprite sprite = baseSprite;
                Material source = baseMaterial;
                Texture paletteTexture = null;
                if (cosmetic != null && cosmetic.cosmeticData != null)
                {
                    Cosmetic_HandItem.Cosmetic_HandItem_Data data = cosmetic.cosmeticData;
                    if (data.swapSprites != null)
                    {
                        foreach (Cosmetic_HandItem.SwapSprite swap in data.swapSprites)
                        {
                            if (swap.spriteName == baseSprite.name && (swap.hand == -1 || swap.hand == hand)
                                && swap.replacementSprites != null && swap.replacementSprites.Count > 0 && swap.replacementSprites[0] != null)
                            {
                                sprite = swap.replacementSprites[0];
                                if (swap.material != null)
                                {
                                    source = swap.material;
                                }
                                break;
                            }
                        }
                    }
                    if (data.palettes != null && data.palettes.Count > 0)
                    {
                        Cosmetic_HandItem.Cosmetic_HandItem_Data.ColorPalette chosen = data.palettes[Mathf.Clamp(palette, 0, data.palettes.Count - 1)];
                        paletteTexture = chosen.texture;
                        if (chosen.material != null)
                        {
                            source = chosen.material;
                        }
                    }
                }

                if (material != null)
                {
                    UnityEngine.Object.Destroy(material);
                }
                material = new Material(source) { name = "Belay_HandSkin" };
                if (material.HasProperty(PaletteBase))
                {
                    material.SetTexture(PaletteBase, paletteTexture);
                }
                if (material.HasProperty(PaletteBaseBlend))
                {
                    material.SetFloat(PaletteBaseBlend, paletteTexture != null ? 1f : 0f);
                }
                if (cosmetic != null && material.HasProperty(HSV))
                {
                    material.SetVector(HSV, hsv);
                }
                if (material.HasProperty(StaminaAmount))
                {
                    material.SetFloat(StaminaAmount, 0f);
                }
                renderer.sharedMaterial = material;
                renderer.sprite = sprite;
                renderer.flipX = localHand.handSprite.flipX;
                renderer.flipY = localHand.handSprite.flipY;
                renderer.gameObject.layer = 0;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Hand skin unavailable: " + e.Message);
                return false;
            }
        }

        private static float Part(string[] parts, int index, float fallback)
        {
            return parts.Length > index && float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }
    }
}
