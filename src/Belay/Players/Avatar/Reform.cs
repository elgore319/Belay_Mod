using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// The totem revive: the revived teammate is rebuilt from the inside out. A glowing ember of a heart
    /// lights up in the chest, a skeleton grows out of it (spine and ribs first, then out along the limbs to
    /// the hands and feet), then the body is built back over the bones layer by layer in the same order:
    /// raw muscle, then skin, then clothes and gear, each swelling out of the one under it, and the glow
    /// cools off. About three seconds.
    ///
    /// Timing and the bone shapes live here as plain math, so the avatar preview draws the same frames the
    /// game does. <see cref="ClimberAvatar"/> plays it.
    /// </summary>
    public static class Reform
    {
        /// <summary>Order parts form in, inside to outside.</summary>
        public enum Part { Torso, Head, UpperLimb, LowerLimb, Hand, Boot, Gear }

        public static int Stage(Part part)
        {
            switch (part)
            {
                case Part.Torso: return 0;
                case Part.Head: return 1;
                case Part.UpperLimb: return 1;
                case Part.LowerLimb: return 2;
                case Part.Hand: return 3;
                case Part.Boot: return 3;
                default: return 4; // backpack and its lid: gear comes last
            }
        }

        public const float Duration = 3.2f;

        private const float BoneStart = 0.2f, BoneStep = 0.14f, BoneTime = 0.32f;
        private const float LayerStep = 0.2f, LayerTime = 0.5f;

        /// <summary>
        /// What grows over the bones, inside to outside: raw red muscle, then a pale layer of skin,
        /// then the climber's own clothes and gear. Each layer swells out of the one under it.
        /// </summary>
        public enum Layer { Muscle, Skin, Clothes }

        public const int LayerCount = 3;

        private static float LayerStart(Layer layer)
        {
            switch (layer)
            {
                case Layer.Muscle: return 0.75f;
                case Layer.Skin: return 1.1f;
                default: return 1.45f;
            }
        }

        /// <summary>How big a layer finally gets next to the clothes (muscle sits inside the skin).</summary>
        public static float LayerSize(Layer layer)
        {
            switch (layer)
            {
                case Layer.Muscle: return 0.8f;
                case Layer.Skin: return 0.93f;
                default: return 1f;
            }
        }

        /// <summary>Tint for a layer's flesh texture.</summary>
        public static Color LayerTint(Layer layer)
        {
            return layer == Layer.Muscle ? new Color(0.85f, 0.42f, 0.4f) : Color.white;
        }

        /// <summary>
        /// Skin made from the meat texture: the same grain, washed toward a pale, bloodless flesh tone
        /// with the meat's red still showing through in its darker streaks.
        /// </summary>
        public static Color32[] SkinFrom(Color32[] meat)
        {
            var pale = new Color(0.86f, 0.72f, 0.64f);
            float average = 0f;
            foreach (Color32 c in meat)
            {
                average += (c.r + c.g + c.b) / 765f;
            }
            average = Mathf.Max(0.05f, average / Mathf.Max(1, meat.Length));
            var skin = new Color32[meat.Length];
            for (int i = 0; i < meat.Length; i++)
            {
                Color c = meat[i];
                float shade = Mathf.Clamp((c.r + c.g + c.b) / 3f / average, 0.55f, 1.25f);
                Color washed = Color.Lerp(c, pale * shade, 0.72f);
                washed.a = 1f;
                skin[i] = washed;
            }
            return skin;
        }

        /// <summary>How far a part's bone has grown out (0..1) at <paramref name="t"/> seconds in.</summary>
        public static float Bone(Part part, float t)
        {
            return Ease((t - BoneStart - Stage(part) * BoneStep) / BoneTime);
        }

        /// <summary>How far a layer has grown on a part (0..1, swelling a touch past 1 before it settles).</summary>
        public static float Grow(Part part, Layer layer, float t)
        {
            if (part == Part.Gear && layer != Layer.Clothes)
            {
                return 0f; // gear has no flesh
            }
            float x = Mathf.Clamp01((t - LayerStart(layer) - Stage(part) * LayerStep) / LayerTime);
            float e = Ease(x);
            // A wet, living swell: it bulges slightly past its size and settles, like it's breathing in.
            return e + 0.07f * Mathf.Sin(Mathf.PI * e) * (1f - e * 0.3f);
        }

        /// <summary>Clothes growth (kept for callers that only care when the body is whole).</summary>
        public static float Flesh(Part part, float t) => Mathf.Clamp01(Grow(part, Layer.Clothes, t));

        /// <summary>Whether a part's bone still shows (it's grown and the muscle hasn't covered it).</summary>
        public static bool BoneShows(Part part, float t)
        {
            return Bone(part, t) > 0.001f && (part == Part.Gear || Grow(part, Layer.Muscle, t) < 0.9f);
        }

        /// <summary>Whether a flesh layer shows on a part (it's grown and the next layer hasn't covered it).</summary>
        public static bool LayerShows(Part part, Layer layer, float t)
        {
            if (layer == Layer.Clothes)
            {
                return Grow(part, layer, t) > 0.001f;
            }
            if (Grow(part, layer, t) <= 0.001f)
            {
                return false;
            }
            Layer over = layer + 1;
            return Grow(part, over, t) < 0.995f;
        }

        /// <summary>
        /// Scale on a part's own axes for one layer. Limbs and the torso swell out sideways round the
        /// bone at full length; hands, feet, the head and gear grow out from their joint.
        /// </summary>
        public static Vector3 LayerScale(Part part, Layer layer, float t)
        {
            float f = Grow(part, layer, t) * LayerSize(layer);
            switch (part)
            {
                case Part.UpperLimb:
                case Part.LowerLimb:
                    // Limbs run along +z.
                    return new Vector3(f, f, Mathf.Lerp(0.7f, 1f, Mathf.Clamp01(f)));
                case Part.Torso:
                    return new Vector3(f, Mathf.Lerp(0.85f, 1f, Mathf.Clamp01(f)), f);
                default:
                    return new Vector3(f, f, f);
            }
        }

        /// <summary>Scale on a part's own axes for its clothes (the climber's own model).</summary>
        public static Vector3 FleshScale(Part part, float t) => LayerScale(part, Layer.Clothes, t);

        /// <summary>Scale on a part's axes for its bone: limbs grow out from the joint along +z.</summary>
        public static Vector3 BoneScale(Part part, float t)
        {
            float b = Bone(part, t);
            return part == Part.UpperLimb || part == Part.LowerLimb ? new Vector3(1f, 1f, b) : new Vector3(b, b, b);
        }

        /// <summary>
        /// The game's own meat texture (on the meat you can eat) is used for the flesh: this part of it,
        /// as fractions of the texture (x, y from the bottom, width, height), is all meat.
        /// </summary>
        public static readonly Vector4 MeatRegion = new Vector4(6f / 64f, 27f / 64f, 0.5f, 0.5f);

        /// <summary>The item whose texture holds the meat.</summary>
        public const string MeatItem = "Item_Food_Meat";

        /// <summary>Used when the game's meat can't be found.</summary>
        public static PixelTextures.Recipe FallbackMeat => new PixelTextures.Recipe
        {
            Base = new Color(0.72f, 0.33f, 0.34f),
            Grime = new Color(0.3f, 0.06f, 0.05f),
            Speck = new Color(0.95f, 0.7f, 0.7f),
            GrimeAmount = 0.25f,
            SpeckAmount = 0.12f,
            Contrast = 0.6f,
            Stripes = true,
        };

        /// <summary>Size of the glowing heart (meters across, before the avatar's scale).</summary>
        public static float Core(float t)
        {
            float grow = Ease(t / 0.35f);
            float fade = 1f - Ease((t - 1.0f) / 0.5f);
            float pulse = 1f + 0.12f * Mathf.Sin(t * 18f);
            return 0.12f * grow * fade * pulse;
        }

        /// <summary>Where the heart sits in the torso's design space (pelvis at the origin).</summary>
        public static readonly Vector3 CorePosition = new Vector3(0f, 0.32f, 0.02f);

        /// <summary>How hot the new body still glows (0..1): full while forming, cooling at the end.</summary>
        public static float Glow(float t)
        {
            return 1f - Ease((t - 2.2f) / (Duration - 2.2f));
        }

        public static readonly Color GlowColor = new Color(1f, 0.55f, 0.15f);

        private static float Ease(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        // ------------------------------------------------------------------ Bones, in each part's design space

        public static void BuildBone(MeshKit k, Part part, bool left = false)
        {
            k.Use(0);
            switch (part)
            {
                case Part.Torso:
                    // Spine from the tailbone up to the neck.
                    k.Tube(new Vector3(0, -0.06f, -0.04f), new Vector3(0, 0.58f, -0.03f), 0.024f, 0.02f, 6);
                    // Pelvis.
                    k.Torus(new Vector3(0, -0.02f, -0.01f), new Vector3(0, 1f, 0.35f), 0.1f, 0.016f, 8, 4);
                    // Ribs.
                    for (int i = 0; i < 4; i++)
                    {
                        float y = 0.24f + i * 0.065f;
                        float r = 0.115f - Mathf.Abs(i - 1.5f) * 0.012f;
                        k.Torus(new Vector3(0, y, 0.0f), new Vector3(0, 1f, -0.25f), r, 0.011f, 10, 4);
                    }
                    // Collarbones out to the shoulders.
                    k.Tube(new Vector3(-AvatarDesign.ShoulderWidth, AvatarDesign.ShoulderHeight, 0), new Vector3(0, 0.52f, 0.02f), 0.015f, 0.013f, 5);
                    k.Tube(new Vector3(AvatarDesign.ShoulderWidth, AvatarDesign.ShoulderHeight, 0), new Vector3(0, 0.52f, 0.02f), 0.015f, 0.013f, 5);
                    // Hip sockets down to the legs.
                    k.Tube(new Vector3(-AvatarDesign.HipWidth, -0.07f, 0), new Vector3(0, 0.0f, -0.03f), 0.016f, 0.016f, 5);
                    k.Tube(new Vector3(AvatarDesign.HipWidth, -0.07f, 0), new Vector3(0, 0.0f, -0.03f), 0.016f, 0.016f, 5);
                    break;
                case Part.Head:
                    // Neck, skull and jaw.
                    k.Tube(new Vector3(0, -0.03f, -0.01f), new Vector3(0, 0.06f, 0f), 0.02f, 0.02f, 6);
                    k.Ellipsoid(new Vector3(0, 0.14f, 0.0f), new Vector3(0.075f, 0.085f, 0.09f), 4, 9);
                    k.Block(new Vector3(0, 0.05f, 0.05f), 0.04f, 0.08f, 0.06f, 0.1f, 0.07f, 0.01f);
                    break;
                case Part.UpperLimb:
                case Part.LowerLimb:
                    // Along +z from joint (0) to joint (1).
                    k.Tube(new Vector3(0, 0, 0f), new Vector3(0, 0, 1f), 0.022f, 0.018f, 6);
                    break;
                case Part.Hand:
                    k.Ellipsoid(new Vector3(0, -0.02f, 0.05f), new Vector3(0.034f, 0.02f, 0.05f), 3, 6);
                    for (int i = 0; i < 4; i++)
                    {
                        float x = (i - 1.5f) * 0.018f;
                        k.Tube(new Vector3(x, -0.02f, 0.08f), new Vector3(x, -0.03f, 0.13f), 0.006f, 0.005f, 4);
                    }
                    break;
                case Part.Boot:
                    k.Tube(new Vector3(0, 0, 0), new Vector3(0, -0.055f, 0.15f), 0.02f, 0.014f, 5);
                    k.Ellipsoid(new Vector3(0, -0.04f, -0.02f), new Vector3(0.025f, 0.025f, 0.035f), 3, 6);
                    break;
            }
        }
    }
}
