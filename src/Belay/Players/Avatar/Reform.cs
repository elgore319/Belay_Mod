using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// The totem revive: the revived teammate is rebuilt from the inside out. A glowing ember of a heart
    /// lights up in the chest, a skeleton grows out of it (spine and ribs first, then out along the limbs to
    /// the hands and feet), then flesh and clothes swell out around the bones in the same order, and the
    /// glow cools off. About two and a half seconds.
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

        public const float Duration = 2.6f;

        private const float BoneStart = 0.2f, BoneStep = 0.14f, BoneTime = 0.32f;
        private const float FleshStart = 0.95f, FleshStep = 0.2f, FleshTime = 0.42f;

        /// <summary>How far a part's bone has grown out (0..1) at <paramref name="t"/> seconds in.</summary>
        public static float Bone(Part part, float t)
        {
            return Ease((t - BoneStart - Stage(part) * BoneStep) / BoneTime);
        }

        /// <summary>How far a part's flesh has swelled out round its bone (0..1).</summary>
        public static float Flesh(Part part, float t)
        {
            return Ease((t - FleshStart - Stage(part) * FleshStep) / FleshTime);
        }

        /// <summary>Whether a part's bone still shows (it's grown and not yet covered).</summary>
        public static bool BoneShows(Part part, float t)
        {
            return Bone(part, t) > 0.001f && Flesh(part, t) < 0.999f;
        }

        /// <summary>
        /// Scale on a part's own axes for its flesh. Limbs and the torso swell out sideways round the
        /// bone at full length; hands, feet, the head and gear grow out from their joint.
        /// </summary>
        public static Vector3 FleshScale(Part part, float t)
        {
            float f = Flesh(part, t);
            switch (part)
            {
                case Part.UpperLimb:
                case Part.LowerLimb:
                    // Limbs run along +z.
                    return new Vector3(f, f, Mathf.Lerp(0.6f, 1f, f));
                case Part.Torso:
                    return new Vector3(f, Mathf.Lerp(0.85f, 1f, f), f);
                default:
                    return new Vector3(f, f, f);
            }
        }

        /// <summary>Scale on a part's axes for its bone: limbs grow out from the joint along +z.</summary>
        public static Vector3 BoneScale(Part part, float t)
        {
            float b = Bone(part, t);
            return part == Part.UpperLimb || part == Part.LowerLimb ? new Vector3(1f, 1f, b) : new Vector3(b, b, b);
        }

        /// <summary>Size of the glowing heart (meters across, before the avatar's scale).</summary>
        public static float Core(float t)
        {
            float grow = Ease(t / 0.35f);
            float fade = 1f - Ease((t - 1.2f) / 0.5f);
            float pulse = 1f + 0.12f * Mathf.Sin(t * 18f);
            return 0.12f * grow * fade * pulse;
        }

        /// <summary>Where the heart sits in the torso's design space (pelvis at the origin).</summary>
        public static readonly Vector3 CorePosition = new Vector3(0f, 0.32f, 0.02f);

        /// <summary>How hot the new body still glows (0..1): full while forming, cooling at the end.</summary>
        public static float Glow(float t)
        {
            return 1f - Ease((t - 1.7f) / (Duration - 1.7f));
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
