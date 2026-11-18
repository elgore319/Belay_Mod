using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// The totem revive: the revived teammate is rebuilt from the inside out. A glowing skeleton grows
    /// out of the chest (spine and ribs first, then out along the limbs to
    /// the hands and feet), then the body grows back over the bones in the same order: strands of raw
    /// muscle knit from joint to joint, skin buds and spreads over them in patches, then clothes and gear
    /// spread over the skin the same way, and the glow cools off. About three and a half seconds.
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

        public const float Duration = 3.6f;

        private const float BoneStart = 0.2f, BoneStep = 0.14f, BoneTime = 0.32f;
        private const float StageStep = 0.2f;

        /// <summary>
        /// What grows over the bones, inside to outside: strands of raw muscle knitting along the bones,
        /// then skin spreading over them in budding patches, then the climber's clothes and gear the same way.
        /// </summary>
        public enum Layer { Muscle, Skin, Clothes }

        private static float LayerStart(Layer layer)
        {
            switch (layer)
            {
                case Layer.Muscle: return 0.65f;
                case Layer.Skin: return 1.25f;
                default: return 1.8f;
            }
        }

        private static float LayerTime(Layer layer) => layer == Layer.Muscle ? 0.75f : 0.65f;

        /// <summary>The skin sits just inside the clothes.</summary>
        public const float SkinSize = 0.94f;

        /// <summary>Scale on a part's axes for its skin: just inside the clothes (limbs keep their length).</summary>
        public static Vector3 SkinScale(Part part)
        {
            return part == Part.UpperLimb || part == Part.LowerLimb ? new Vector3(SkinSize, SkinSize, 1f) : Vector3.one * SkinSize;
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

        /// <summary>How far a layer has grown over a part (0..1).</summary>
        public static float Progress(Part part, Layer layer, float t)
        {
            if (part == Part.Gear && layer != Layer.Clothes)
            {
                return 0f; // gear has no flesh
            }
            return Mathf.Clamp01((t - LayerStart(layer) - Stage(part) * StageStep) / LayerTime(layer));
        }

        /// <summary>Whether a part's bone still shows (grown, and the skin hasn't closed over it).</summary>
        public static bool BoneShows(Part part, float t)
        {
            return Bone(part, t) > 0.001f && (part == Part.Gear || Progress(part, Layer.Skin, t) < 0.7f);
        }

        /// <summary>Whether a layer shows on a part (started, and the layer over it isn't finished).</summary>
        public static bool LayerShows(Part part, Layer layer, float t)
        {
            if (Progress(part, layer, t) <= 0f)
            {
                return false;
            }
            return layer == Layer.Clothes || Progress(part, layer + 1, t) < 1f;
        }

        /// <summary>Scale on a part's axes for its bone: limbs grow out from the joint along +z.</summary>
        public static Vector3 BoneScale(Part part, float t)
        {
            float b = Bone(part, t);
            return part == Part.UpperLimb || part == Part.LowerLimb ? new Vector3(1f, 1f, b) : new Vector3(b, b, b);
        }

        // ------------------------------------------------------------------ Spreading over the body

        /// <summary>How wide the growing edge is, as a share of the whole spread.</summary>
        private const float Front = 0.3f;

        /// <summary>
        /// When each triangle of a part's mesh starts to grow (0..1): it spreads out from a few seed points
        /// (the joint a limb grows out of, the chest and back of the torso), unevenly, like skin healing over.
        /// One key per triangle, in the order of <paramref name="triangles"/> (three indices each).
        /// </summary>
        public static float[] SpreadKeys(System.Collections.Generic.IList<Vector3> vertices, System.Collections.Generic.IList<int> triangles, Part part, int seed)
        {
            int count = triangles.Count / 3;
            var keys = new float[count];
            if (count == 0)
            {
                return keys;
            }
            Vector3 min = Vector3.one * 1e9f, max = Vector3.one * -1e9f;
            foreach (int i in triangles)
            {
                min = Vector3.Min(min, vertices[i]);
                max = Vector3.Max(max, vertices[i]);
            }
            Vector3 size = max - min;
            var seeds = new System.Collections.Generic.List<Vector3>();
            switch (part)
            {
                case Part.UpperLimb:
                case Part.LowerLimb:
                    seeds.Add(new Vector3(0, 0, 0)); // the joint it grows from
                    break;
                case Part.Torso:
                    seeds.Add(new Vector3(0, 0.32f, 0.12f));
                    seeds.Add(new Vector3(0, 0.3f, -0.12f));
                    break;
                case Part.Head:
                case Part.Hand:
                case Part.Boot:
                    seeds.Add(Vector3.zero);
                    break;
            }
            // A few random patches too, so it doesn't creep along as one tidy line.
            for (int s = 0; s < 3; s++)
            {
                seeds.Add(min + Vector3.Scale(size, new Vector3(Hash(s, 1, seed), Hash(s, 2, seed), Hash(s, 3, seed))));
            }
            // Limbs are stored one unit long: measure them in meters so the spread goes round as well as along.
            Vector3 metric = part == Part.UpperLimb || part == Part.LowerLimb ? new Vector3(1f, 1f, 0.3f) : Vector3.one;
            var distance = new float[count];
            float far = 1e-4f;
            for (int t = 0; t < count; t++)
            {
                Vector3 c = (vertices[triangles[t * 3]] + vertices[triangles[t * 3 + 1]] + vertices[triangles[t * 3 + 2]]) / 3f;
                float best = float.MaxValue;
                for (int s = 0; s < seeds.Count; s++)
                {
                    // Random patches start a little later than the main seeds.
                    float d = Vector3.Scale(c - seeds[s], metric).magnitude + (s >= seeds.Count - 3 ? 0.03f : 0f);
                    best = Mathf.Min(best, d);
                }
                distance[t] = best;
                far = Mathf.Max(far, best);
            }
            for (int t = 0; t < count; t++)
            {
                keys[t] = Mathf.Clamp01(0.8f * distance[t] / far + 0.2f * Hash(t, 7, seed));
            }
            return keys;
        }

        /// <summary>
        /// Grows a mesh over a part: each triangle buds from its own center out to full size once the
        /// spreading edge reaches it, swelling a touch past its size as it does, so the surface grows
        /// across the body cell by cell. Writes into <paramref name="output"/> (same length as
        /// <paramref name="vertices"/>; MeshKit gives every triangle its own three vertices).
        /// </summary>
        public static void Spread(System.Collections.Generic.IList<Vector3> vertices, System.Collections.Generic.IList<int> triangles, float[] keys, float progress, Vector3[] output)
        {
            float edge = progress * (1f + Front);
            for (int t = 0; t < keys.Length; t++)
            {
                float local = Mathf.Clamp01((edge - keys[t]) / Front);
                float grow = Ease(local);
                grow += 0.25f * Mathf.Sin(Mathf.PI * grow) * (1f - grow * 0.5f);
                int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];
                Vector3 center = (vertices[a] + vertices[b] + vertices[c]) / 3f;
                output[a] = center + (vertices[a] - center) * grow;
                output[b] = center + (vertices[b] - center) * grow;
                output[c] = center + (vertices[c] - center) * grow;
            }
        }

        // ------------------------------------------------------------------ Muscle strands

        private struct Strand
        {
            public Vector3 A, C, B;
            public float Thickness;
        }

        /// <summary>
        /// Strands of muscle knitting over a part's bones: each starts at its own moment, creeps from one
        /// attachment to the other along a curve, and thickens into a belly as it goes.
        /// </summary>
        public static void BuildMuscle(MeshKit k, Part part, float progress, int seed)
        {
            if (progress <= 0f)
            {
                return;
            }
            k.Use(0);
            var strands = Strands(part);
            for (int i = 0; i < strands.Count; i++)
            {
                Strand s = strands[i];
                float delay = 0.45f * Hash(i, 11, seed);
                float g = Ease((progress - delay) / 0.5f);
                if (g <= 0.01f)
                {
                    continue;
                }
                const int segments = 4;
                Vector3 previous = Bezier(s, 0f);
                float previousRadius = s.Thickness * 0.35f;
                for (int j = 1; j <= segments; j++)
                {
                    float u = g * j / segments;
                    Vector3 p = Bezier(s, u);
                    // Thin at the tendons, fat in the belly; the growing tip is thin.
                    float belly = 0.45f + 0.55f * Mathf.Sin(Mathf.PI * u);
                    float tip = j == segments && g < 1f ? 0.5f : 1f;
                    float radius = s.Thickness * belly * tip * (0.4f + 0.6f * g);
                    k.Tube(previous, p, previousRadius, radius, 5, caps: j == segments);
                    previous = p;
                    previousRadius = radius;
                }
            }
        }

        private static Vector3 Bezier(Strand s, float u)
        {
            float v = 1f - u;
            return s.A * (v * v) + s.C * (2f * u * v) + s.B * (u * u);
        }

        private static readonly System.Collections.Generic.Dictionary<Part, System.Collections.Generic.List<Strand>> strandCache =
            new System.Collections.Generic.Dictionary<Part, System.Collections.Generic.List<Strand>>();

        private static System.Collections.Generic.List<Strand> Strands(Part part)
        {
            if (strandCache.TryGetValue(part, out var cached))
            {
                return cached;
            }
            var list = new System.Collections.Generic.List<Strand>();
            void Add(Vector3 a, Vector3 c, Vector3 b, float thick) => list.Add(new Strand { A = a, C = c, B = b, Thickness = thick });
            switch (part)
            {
                case Part.UpperLimb:
                case Part.LowerLimb:
                    // Around the bone, twisting a little from joint to joint (limbs run along +z, one unit long).
                    for (int i = 0; i < 9; i++)
                    {
                        float a = i * Mathf.PI * 2f / 9f + 0.3f * Hash(i, 3, 5);
                        float r = 0.034f + 0.008f * Hash(i, 4, 5);
                        Vector3 Ring(float angle, float radius, float z) => new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, z);
                        Add(Ring(a, r * 0.7f, 0.03f), Ring(a + 0.2f, r * 1.45f, 0.48f), Ring(a + 0.4f, r * 0.75f, 0.97f), 0.017f);
                    }
                    break;
                case Part.Torso:
                    for (int side = -1; side <= 1; side += 2)
                    {
                        // Chest, fanning from the breastbone to the shoulder.
                        for (int j = 0; j < 3; j++)
                        {
                            Add(new Vector3(0.01f * side, 0.3f + j * 0.05f, 0.1f), new Vector3(side * 0.1f, 0.36f + j * 0.03f, 0.135f), new Vector3(side * 0.19f, 0.45f, 0.02f), 0.024f);
                        }
                        // Stomach, in two columns.
                        Add(new Vector3(side * 0.035f, 0.02f, 0.095f), new Vector3(side * 0.04f, 0.16f, 0.125f), new Vector3(side * 0.04f, 0.3f, 0.11f), 0.022f);
                        Add(new Vector3(side * 0.08f, 0.03f, 0.085f), new Vector3(side * 0.09f, 0.16f, 0.115f), new Vector3(side * 0.09f, 0.29f, 0.105f), 0.02f);
                        // Sides, slanting in.
                        Add(new Vector3(side * 0.16f, 0.04f, 0.0f), new Vector3(side * 0.165f, 0.17f, 0.07f), new Vector3(side * 0.08f, 0.3f, 0.11f), 0.02f);
                        // Back: along the spine and out under the arms.
                        Add(new Vector3(side * 0.035f, -0.02f, -0.09f), new Vector3(side * 0.07f, 0.24f, -0.125f), new Vector3(side * 0.05f, 0.5f, -0.085f), 0.024f);
                        Add(new Vector3(side * 0.05f, 0.1f, -0.1f), new Vector3(side * 0.16f, 0.24f, -0.1f), new Vector3(side * 0.19f, 0.43f, -0.02f), 0.024f);
                        // Shoulders and neck.
                        Add(new Vector3(side * 0.02f, 0.5f, -0.05f), new Vector3(side * 0.1f, 0.55f, -0.04f), new Vector3(side * 0.2f, 0.47f, 0.0f), 0.022f);
                        Add(new Vector3(side * 0.03f, 0.47f, 0.04f), new Vector3(side * 0.045f, 0.54f, 0.03f), new Vector3(side * 0.02f, 0.61f, 0.0f), 0.016f);
                        // Hips.
                        Add(new Vector3(side * 0.03f, -0.06f, 0.07f), new Vector3(side * 0.12f, -0.02f, 0.09f), new Vector3(side * 0.15f, 0.06f, 0.0f), 0.02f);
                    }
                    break;
                case Part.Head:
                    // Over the skull from jaw to crown, and across the jaw.
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * Mathf.PI * 2f / 8f;
                        Vector3 around = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                        Add(new Vector3(0, 0.05f, 0) + around * 0.055f, new Vector3(0, 0.14f, 0) + new Vector3(around.x * 0.1f, 0, around.z * 0.105f), new Vector3(0, 0.235f, 0), 0.017f);
                    }
                    Add(new Vector3(-0.065f, 0.09f, 0.02f), new Vector3(-0.04f, 0.04f, 0.08f), new Vector3(0.0f, 0.035f, 0.095f), 0.014f);
                    Add(new Vector3(0.065f, 0.09f, 0.02f), new Vector3(0.04f, 0.04f, 0.08f), new Vector3(0.0f, 0.035f, 0.095f), 0.014f);
                    break;
                case Part.Hand:
                    for (int i = 0; i < 5; i++)
                    {
                        float x = (i - 2) * 0.02f;
                        Add(new Vector3(x * 0.5f, -0.02f, 0f), new Vector3(x, -0.008f, 0.06f), new Vector3(x * 1.1f, -0.025f, 0.13f), 0.012f);
                    }
                    break;
                case Part.Boot:
                    foreach (float x in new[] { -0.045f, -0.016f, 0.016f, 0.045f })
                    {
                        Add(new Vector3(x * 0.6f, 0f, -0.02f), new Vector3(x, -0.04f, 0.06f), new Vector3(x * 1.2f, -0.06f, 0.17f), 0.016f);
                    }
                    Add(new Vector3(0f, 0f, 0f), new Vector3(0f, -0.04f, -0.05f), new Vector3(0f, -0.07f, -0.02f), 0.02f);
                    break;
            }
            strandCache[part] = list;
            return list;
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

        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + seed * 1442695041;
                h = (h ^ (h >> 13)) * 1274126177;
                h ^= h >> 16;
                return (h & 0xFFFF) / 65535f;
            }
        }

        /// <summary>How hot the new body still glows (0..1): full while forming, cooling at the end.</summary>
        public static float Glow(float t)
        {
            return 1f - Ease((t - 2.6f) / (Duration - 2.6f));
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
