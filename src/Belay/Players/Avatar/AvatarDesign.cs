using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>Material slots shared by every avatar mesh.</summary>
    public static class Slot
    {
        public const int Jacket = 0;
        public const int JacketTrim = 1;
        public const int Pants = 2;
        public const int Webbing = 3;
        public const int Metal = 4;
        public const int Pack = 5;
        public const int Glove = 6;
        public const int Boot = 7;
        public const int Visor = 8;
        public const int Lamp = 9;
        public const int Buff = 10;
        public const int Helmet = 11;
        public const int Rope = 12;
        public const int Pad = 13;
        public const int Count = 14;
    }

    /// <summary>
    /// The look of a Belay climber: a hooded shell jacket in the player's color, a hard-shell helmet with
    /// a headlamp and goggles, a sit harness with gear loops, a pack with a rope coil, gloves and boots.
    /// All original geometry, built from code (no imported assets).
    ///
    /// Units are meters. Each Build* method fills one part in its own local space, described above it.
    /// </summary>
    public static class AvatarDesign
    {
        // Skeleton proportions, measured from the soles of the feet.
        public const float PelvisHeight = 0.95f;
        public const float ShoulderHeight = 0.48f;   // above the pelvis
        public const float ShoulderWidth = 0.205f;   // half width
        public const float NeckHeight = 0.56f;       // above the pelvis
        public const float HipWidth = 0.095f;        // half width
        public const float UpperArm = 0.29f;
        public const float Forearm = 0.27f;
        public const float Thigh = 0.45f;
        public const float Shin = 0.43f;
        public const float AnkleHeight = 0.08f;
        public const float HarnessLoopDrop = 0.06f;  // belay loop sits just under the pelvis origin

        // Palette pulled toward the game's: rusty maroon grime, bone, charcoal, slate.
        public static readonly Color GrimeColor = new Color(0.27f, 0.11f, 0.13f);
        public static readonly Color RustColor = new Color(0.64f, 0.28f, 0.16f);
        public static readonly Color BoneColor = new Color(0.7f, 0.65f, 0.62f);

        /// <summary>The player's lobby color, muted and darkened so it sits in the game's grimy palette.</summary>
        public static Color Muted(Color player, float saturation = 0.6f, float value = 0.7f)
        {
            Color.RGBToHSV(player, out float h, out float s, out float v);
            return Color.HSVToRGB(h, Mathf.Clamp01(s * saturation), Mathf.Clamp01(v * value));
        }

        public static PixelTextures.Recipe[] Recipes(Color player)
        {
            PixelTextures.Recipe R(Color b, float grime, float speck, float contrast = 0.45f, Color? speckColor = null)
            {
                return new PixelTextures.Recipe
                {
                    Base = b, Grime = GrimeColor, Speck = speckColor ?? Color.Lerp(b * 1.3f, BoneColor, 0.3f),
                    GrimeAmount = grime, SpeckAmount = speck, Contrast = contrast,
                };
            }
            var r = new PixelTextures.Recipe[Slot.Count];
            r[Slot.Jacket] = R(Muted(player), 0.3f, 0.06f);
            r[Slot.JacketTrim] = R(Muted(player, 0.5f, 0.42f), 0.3f, 0.05f);
            r[Slot.Pants] = R(new Color(0.27f, 0.23f, 0.26f), 0.3f, 0.03f);
            r[Slot.Webbing] = R(new Color(0.15f, 0.14f, 0.15f), 0.1f, 0.02f, 0.3f);
            r[Slot.Webbing].Stripes = true;
            r[Slot.Metal] = R(new Color(0.55f, 0.55f, 0.56f), 0.15f, 0.08f, 0.3f, RustColor);
            r[Slot.Pack] = R(new Color(0.3f, 0.27f, 0.24f), 0.35f, 0.06f);
            r[Slot.Glove] = R(new Color(0.38f, 0.31f, 0.29f), 0.3f, 0.05f);
            r[Slot.Boot] = R(new Color(0.15f, 0.13f, 0.14f), 0.25f, 0.06f, 0.3f, RustColor * 0.7f);
            r[Slot.Visor] = R(new Color(0.06f, 0.06f, 0.08f), 0f, 0.03f, 0.15f, new Color(0.5f, 0.52f, 0.6f));
            r[Slot.Lamp] = new PixelTextures.Recipe { Base = new Color(1f, 0.94f, 0.78f), Flat = true };
            r[Slot.Buff] = R(new Color(0.2f, 0.18f, 0.2f), 0.2f, 0.04f);
            r[Slot.Helmet] = R(Muted(player, 0.7f, 0.8f), 0.4f, 0.1f, 0.4f, RustColor);
            r[Slot.Rope] = R(Color.Lerp(Muted(player, 0.5f, 0.9f), BoneColor, 0.5f), 0.15f, 0.08f);
            r[Slot.Pad] = R(BoneColor * 0.85f, 0.45f, 0.12f, 0.45f, RustColor);
            return r;
        }

        /// <summary>Torso and harness (the pack is its own part). Origin at the pelvis, +y up, +z forward.</summary>
        public static void BuildTorso(MeshKit k)
        {
            // Hips and seat of the pants.
            k.Use(Slot.Pants).Loft(new[]
            {
                new Vector4(-0.14f, 0.14f, 0.095f, 0f),
                new Vector4(-0.06f, 0.168f, 0.108f, -0.005f),
                new Vector4(0.0f, 0.17f, 0.108f, 0f),
            }, 12, true, false);

            // Padded jacket: pinched at the waist, full through the chest, rounding into the shoulders.
            k.Use(Slot.Jacket).Loft(new[]
            {
                new Vector4(-0.01f, 0.172f, 0.11f, 0f),
                new Vector4(0.09f, 0.165f, 0.108f, 0.003f),
                new Vector4(0.22f, 0.19f, 0.118f, 0.01f),
                new Vector4(0.34f, 0.212f, 0.126f, 0.016f),
                new Vector4(0.43f, 0.205f, 0.118f, 0.012f),
                new Vector4(0.49f, 0.165f, 0.1f, 0.004f),
                new Vector4(0.52f, 0.11f, 0.08f, 0f),
            });
            // Hood rolled into a thick collar.
            k.Use(Slot.JacketTrim);
            k.Loft(new[]
            {
                new Vector4(0.47f, 0.135f, 0.1f, -0.01f),
                new Vector4(0.54f, 0.118f, 0.092f, -0.014f),
                new Vector4(0.58f, 0.1f, 0.08f, -0.018f),
            }, 10, false, true);
            k.Ellipsoid(new Vector3(0, 0.55f, -0.09f), new Vector3(0.12f, 0.07f, 0.06f), 3, 7);
            // Zip flap, chest pocket, hem band.
            k.Box(new Vector3(0, 0.25f, 0.128f), new Vector3(0.035f, 0.4f, 0.016f));
            k.Box(new Vector3(0.095f, 0.33f, 0.126f), new Vector3(0.11f, 0.1f, 0.02f));
            k.Loft(new[] { new Vector4(-0.005f, 0.177f, 0.115f, 0f), new Vector4(0.035f, 0.174f, 0.114f, 0.001f) });

            // Padded shoulder plates.
            k.Use(Slot.Pad);
            for (int side = -1; side <= 1; side += 2)
            {
                k.Ellipsoid(new Vector3(side * (ShoulderWidth - 0.005f), ShoulderHeight - 0.02f, 0.005f), new Vector3(0.075f, 0.06f, 0.09f), 3, 8, true);
            }

            // Harness: waist belt, buckle, belay loop, gear loops with carabiners, pack straps.
            k.Use(Slot.Webbing);
            k.Loft(new[] { new Vector4(-0.075f, 0.178f, 0.116f, 0f), new Vector4(-0.01f, 0.178f, 0.116f, 0f) });
            k.Block(new Vector3(0, -0.14f, 0.095f), 0.1f, 0.055f, 0.045f, 0.065f, 0.045f, 0.015f);
            k.Box(new Vector3(-0.11f, 0.33f, 0.12f), new Vector3(0.04f, 0.26f, 0.02f));
            k.Box(new Vector3(0.11f, 0.33f, 0.12f), new Vector3(0.04f, 0.26f, 0.02f));
            k.Box(new Vector3(0, 0.37f, 0.138f), new Vector3(0.23f, 0.022f, 0.012f));
            k.Use(Slot.Metal);
            k.Box(new Vector3(0, -0.04f, 0.118f), new Vector3(0.075f, 0.04f, 0.014f));
            k.Use(Slot.Rope);
            k.Torus(new Vector3(0, -HarnessLoopDrop - 0.02f, 0.13f), Vector3.right, 0.038f, 0.011f, 8);
            k.Use(Slot.Metal);
            for (int side = -1; side <= 1; side += 2)
            {
                for (int i = 0; i < 3; i++)
                {
                    float x = side * (0.125f + i * 0.025f);
                    float z = 0.07f - i * 0.065f;
                    k.Torus(new Vector3(x, -0.11f, z), new Vector3(side, 0, 0.4f), 0.024f, 0.006f, 6, 3);
                }
            }

            // Chalk bag on the back of the belt.
            k.Use(Slot.JacketTrim).Tube(new Vector3(0, -0.14f, -0.16f), new Vector3(0, -0.03f, -0.165f), 0.05f, 0.055f, 7);
            k.Use(Slot.Buff).Ellipsoid(new Vector3(0, -0.03f, -0.165f), new Vector3(0.055f, 0.015f, 0.055f), 2, 7, true);
        }

        // Where the pack rides on the back, in torso space (its panel against the jacket).
        public static readonly Vector3 PackOnBack = new Vector3(0, 0.26f, -0.125f);

        /// <summary>
        /// The backpack. Origin at the middle of its back panel, +z away from the wearer, +y up.
        /// The lid is separate (see <see cref="BuildPackLid"/>) so it can flip open.
        /// </summary>
        public static void BuildPack(MeshKit k)
        {
            k.Use(Slot.Pack);
            k.Block(new Vector3(0, -0.19f, 0.07f), 0.36f, 0.31f, 0.14f, 0.28f, 0.12f, 0.0f);
            k.Box(new Vector3(0, -0.08f, 0.155f), new Vector3(0.2f, 0.15f, 0.04f));       // front pocket
            k.Box(new Vector3(-0.165f, -0.06f, 0.07f), new Vector3(0.03f, 0.18f, 0.09f)); // side pockets
            k.Box(new Vector3(0.165f, -0.06f, 0.07f), new Vector3(0.03f, 0.18f, 0.09f));
            // The open mouth, seen when the lid is up.
            k.Use(Slot.Buff).Block(new Vector3(0, 0.166f, 0.065f), 0.008f, 0.24f, 0.09f, 0.24f, 0.09f);
            k.Use(Slot.Webbing);
            k.Box(new Vector3(-0.09f, -0.02f, 0.177f), new Vector3(0.028f, 0.3f, 0.012f));
            k.Box(new Vector3(0.09f, -0.02f, 0.177f), new Vector3(0.028f, 0.3f, 0.012f));
            k.Use(Slot.Metal);
            k.Box(new Vector3(-0.09f, -0.12f, 0.185f), new Vector3(0.04f, 0.02f, 0.008f));
            k.Box(new Vector3(0.09f, -0.12f, 0.185f), new Vector3(0.04f, 0.02f, 0.008f));
            // Rope coil strapped under the pack.
            k.Use(Slot.Rope).Tube(new Vector3(-0.15f, -0.24f, 0.07f), new Vector3(0.15f, -0.24f, 0.07f), 0.055f, 0.055f, 8);
            k.Use(Slot.Webbing);
            k.Tube(new Vector3(-0.07f, -0.24f, 0.07f), new Vector3(-0.05f, -0.24f, 0.07f), 0.06f, 0.06f, 8, false);
            k.Tube(new Vector3(0.05f, -0.24f, 0.07f), new Vector3(0.07f, -0.24f, 0.07f), 0.06f, 0.06f, 8, false);
        }

        /// <summary>Hinge of the lid, in pack space: the top edge of the back panel.</summary>
        public static readonly Vector3 PackLidHinge = new Vector3(0, 0.17f, 0.0f);

        /// <summary>The pack lid. Origin at its hinge, closed lying along +z.</summary>
        public static void BuildPackLid(MeshKit k)
        {
            k.Use(Slot.Pack).Block(new Vector3(0, -0.01f, 0.07f), 0.055f, 0.3f, 0.15f, 0.26f, 0.12f, 0.005f);
            k.Use(Slot.Webbing).Box(new Vector3(0, 0.02f, 0.148f), new Vector3(0.05f, 0.05f, 0.012f));
        }

        /// <summary>Head, helmet, goggles and headlamp. Origin at the base of the neck, +z is where they look.</summary>
        public static void BuildHead(MeshKit k)
        {
            // Neck gaiter and a face covered by a balaclava.
            k.Use(Slot.Buff);
            k.Tube(new Vector3(0, -0.02f, 0), new Vector3(0, 0.08f, 0.01f), 0.07f, 0.064f, 8);
            k.Block(new Vector3(0, 0.06f, 0.015f), 0.13f, 0.16f, 0.18f, 0.15f, 0.17f, 0.005f);
            k.Ellipsoid(new Vector3(0, 0.09f, 0.095f), new Vector3(0.05f, 0.04f, 0.03f), 2, 6); // mouth guard

            // Goggles: strap all the way round and a wide dark lens in a padded frame.
            k.Use(Slot.Webbing).Block(new Vector3(0, 0.15f, 0.015f), 0.03f, 0.175f, 0.195f, 0.175f, 0.195f);
            k.Use(Slot.Pad).Block(new Vector3(0, 0.125f, 0.098f), 0.078f, 0.176f, 0.028f, 0.166f, 0.028f, 0.008f);
            k.Use(Slot.Visor).Block(new Vector3(0, 0.133f, 0.105f), 0.062f, 0.16f, 0.03f, 0.15f, 0.026f, 0.008f);

            // Hard-shell helmet: rounded dome with a thick rim.
            k.Use(Slot.Helmet);
            k.Ellipsoid(new Vector3(0, 0.19f, 0.01f), new Vector3(0.124f, 0.105f, 0.14f), 4, 10, true);
            k.Torus(new Vector3(0, 0.19f, 0.01f), Vector3.up, 0.128f, 0.014f, 10, 4);
            k.Use(Slot.Webbing);
            k.Box(new Vector3(-0.088f, 0.12f, 0.03f), new Vector3(0.012f, 0.12f, 0.016f));
            k.Box(new Vector3(0.088f, 0.12f, 0.03f), new Vector3(0.012f, 0.12f, 0.016f));

            // Headlamp on the front of the helmet.
            k.Use(Slot.Webbing).Box(new Vector3(0, 0.215f, 0.14f), new Vector3(0.075f, 0.05f, 0.04f));
            k.Use(Slot.Lamp).Tube(new Vector3(0, 0.215f, 0.157f), new Vector3(0, 0.215f, 0.166f), 0.019f, 0.019f, 8);
        }

        // Limbs are built one unit long along +z and stretched to the bone length in game. Local +y is the
        // side the joint bends toward (front of the knee, point of the elbow).

        /// <summary>Upper arm sleeve, along +z from shoulder (0) to elbow (1).</summary>
        public static void BuildUpperArm(MeshKit k)
        {
            k.UvAxisScale = new Vector3(1, 1, UpperArm);
            k.Use(Slot.Jacket).Tube(new Vector3(0, 0, -0.04f), new Vector3(0, 0, 1.03f), 0.068f, 0.055f, 8);
            k.Use(Slot.JacketTrim).Tube(new Vector3(0, 0, 0.4f), new Vector3(0, 0, 0.5f), 0.07f, 0.068f, 8, false);
        }

        /// <summary>Forearm sleeve with an elbow pad, along +z from elbow (0) to wrist (1).</summary>
        public static void BuildForearm(MeshKit k)
        {
            k.UvAxisScale = new Vector3(1, 1, Forearm);
            k.Use(Slot.Jacket).Tube(new Vector3(0, 0, -0.05f), new Vector3(0, 0, 0.88f), 0.058f, 0.048f, 8);
            k.Use(Slot.JacketTrim).Tube(new Vector3(0, 0, 0.82f), new Vector3(0, 0, 1.01f), 0.054f, 0.052f, 8);
            k.Use(Slot.Pad).Ellipsoid(new Vector3(0, 0.04f, 0.05f), new Vector3(0.055f, 0.045f, 0.3f), 3, 7);
        }

        /// <summary>Thigh, along +z from hip (0) to knee (1), with the harness leg loop and a cargo pocket.</summary>
        public static void BuildThigh(MeshKit k)
        {
            k.UvAxisScale = new Vector3(1, 1, Thigh);
            k.Use(Slot.Pants).Tube(new Vector3(0, 0, -0.06f), new Vector3(0, 0, 1.02f), 0.092f, 0.07f, 8);
            k.Use(Slot.Webbing).Tube(new Vector3(0, 0, 0.1f), new Vector3(0, 0, 0.18f), 0.096f, 0.094f, 8, false);
            k.Use(Slot.JacketTrim).Ellipsoid(new Vector3(0.075f, 0f, 0.5f), new Vector3(0.03f, 0.06f, 0.16f), 2, 6);
        }

        /// <summary>Shin, along +z from knee (0) to ankle (1), with a knee pad.</summary>
        public static void BuildShin(MeshKit k)
        {
            k.UvAxisScale = new Vector3(1, 1, Shin);
            k.Use(Slot.Pants).Tube(new Vector3(0, 0, -0.04f), new Vector3(0, 0, 0.95f), 0.07f, 0.056f, 8);
            k.Use(Slot.Pad).Ellipsoid(new Vector3(0, 0.07f, 0.04f), new Vector3(0.075f, 0.04f, 0.16f), 3, 7);
        }

        /// <summary>Chunky climbing boot. Origin at the ankle, +z toward the toes, +y up.</summary>
        public static void BuildBoot(MeshKit k)
        {
            k.Use(Slot.Boot);
            k.Block(new Vector3(0, -AnkleHeight + 0.02f, 0.04f), 0.08f, 0.12f, 0.29f, 0.11f, 0.22f, 0.01f);
            k.Block(new Vector3(0, -0.03f, -0.005f), 0.11f, 0.11f, 0.13f, 0.1f, 0.12f);
            k.Ellipsoid(new Vector3(0, -0.03f, 0.15f), new Vector3(0.06f, 0.05f, 0.05f), 3, 6);
            k.Use(Slot.Webbing).Block(new Vector3(0, -AnkleHeight - 0.005f, 0.04f), 0.028f, 0.125f, 0.305f, 0.125f, 0.3f);
            k.Use(Slot.Metal).Box(new Vector3(0, 0.02f, 0.07f), new Vector3(0.055f, 0.014f, 0.06f));
        }

        // Hands. Origin at the wrist, +z toward the fingertips, +y the back of the hand. The whole hand,
        // cuff to fingertips, is projected onto UVs from above and below, so the game's pictures of a
        // glove skin (back of the hand and palm) wrap onto it the way the game draws them.
        public const float HandUvMinZ = -0.05f;
        public const float HandUvLength = 0.22f;
        public const float HandUvMinX = -0.052f;
        public const float HandUvWidth = 0.142f;

        private static void HandSetup(MeshKit k, float thumbSide)
        {
            // The skin texture is an atlas in thirds: the back of the hand, the palm (seen from the front,
            // so the thumb is on the other side), and a swatch of the skin's finger colors. The game's
            // pictures are drawn in perspective with the fingers spread, so they only line up with the
            // flat parts of the 3D hand; fingers and thumb take the swatch instead of a slice of picture.
            k.UvProjection = (p, n) =>
            {
                float u = Mathf.Clamp01((p.x * thumbSide - HandUvMinX) / HandUvWidth);
                float v = Mathf.Clamp01((p.z - HandUvMinZ) / HandUvLength);
                return n.y >= -0.2f ? new Vector2(u / 3f, v) : new Vector2((1f + (1f - u)) / 3f, v);
            };
            k.Softness = 0.75f;
            k.Use(Slot.Glove);
            // Cuff, running into the heel of the hand.
            k.Tube(new Vector3(0, -0.008f, HandUvMinZ), new Vector3(0, -0.008f, 0.012f), 0.042f, 0.04f, 8);
        }

        /// <summary>From here on, faces take the finger swatch (the right third of the skin atlas).</summary>
        private static void FingerUv(MeshKit k, float thumbSide)
        {
            k.UvProjection = (p, n) =>
            {
                float u = Mathf.Clamp01((p.x * thumbSide - HandUvMinX) / HandUvWidth);
                float v = Mathf.Clamp01((p.z - HandUvMinZ) / HandUvLength);
                return new Vector2((2f + u * 0.98f + 0.01f) / 3f, v);
            };
        }

        private static void Finger(MeshKit k, float radius, params Vector3[] joints)
        {
            for (int i = 0; i < joints.Length - 1; i++)
            {
                float r0 = Mathf.Lerp(radius, radius * 0.8f, (float)i / (joints.Length - 1));
                float r1 = Mathf.Lerp(radius, radius * 0.8f, (float)(i + 1) / (joints.Length - 1));
                k.Tube(joints[i], joints[i + 1], r0, r1, 6);
                k.Ellipsoid(joints[i + 1], Vector3.one * r1, 2, 6);
            }
        }

        /// <summary>A gloved fist gripping a hold: fingers curled under, thumb wrapped across.</summary>
        public static void BuildFist(MeshKit k, bool left)
        {
            float t = left ? 1f : -1f;
            HandSetup(k, t);
            // Palm from the wrist to the knuckles, so the curled fingers grow out of it.
            k.Block(new Vector3(0, -0.032f, 0.052f), 0.048f, 0.08f, 0.1f, 0.088f, 0.104f, 0.006f);
            FingerUv(k, t);
            float[] curl = { 0.0f, 0.004f, 0.0f, -0.008f };
            for (int i = 0; i < 4; i++)
            {
                float x = (i - 1.5f) * 0.022f * -t + t * 0.003f;
                float c = curl[i];
                Finger(k, 0.0125f,
                    new Vector3(x, -0.006f, 0.095f),
                    new Vector3(x, -0.016f, 0.13f + c),
                    new Vector3(x, -0.048f, 0.128f + c),
                    new Vector3(x * 0.95f, -0.058f, 0.1f + c));
            }
            Finger(k, 0.015f,
                new Vector3(t * 0.042f, -0.02f, 0.02f),
                new Vector3(t * 0.05f, -0.045f, 0.065f),
                new Vector3(t * 0.012f, -0.064f, 0.095f));
        }

        /// <summary>An open gloved hand reaching for a hold, fingers slightly curled.</summary>
        public static void BuildOpenHand(MeshKit k, bool left)
        {
            float t = left ? 1f : -1f;
            HandSetup(k, t);
            k.Block(new Vector3(0, -0.022f, 0.05f), 0.034f, 0.08f, 0.1f, 0.088f, 0.102f, 0.0f);
            FingerUv(k, t);
            float[] length = { 0.072f, 0.082f, 0.078f, 0.062f };
            for (int i = 0; i < 4; i++)
            {
                float x = (i - 1.5f) * 0.022f * -t;
                float splay = (i - 1.5f) * 0.006f * -t;
                float l = length[i];
                Finger(k, 0.011f,
                    new Vector3(x, -0.004f, 0.1f),
                    new Vector3(x + splay, -0.012f, 0.1f + l * 0.55f),
                    new Vector3(x + splay * 1.6f, -0.026f, 0.1f + l * 0.98f));
            }
            Finger(k, 0.014f,
                new Vector3(t * 0.04f, -0.012f, 0.02f),
                new Vector3(t * 0.068f, -0.016f, 0.055f),
                new Vector3(t * 0.082f, -0.022f, 0.09f));
        }
    }
}
