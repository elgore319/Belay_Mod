using Belay.Players.Avatar;
using UnityEngine;

namespace Belay.Items
{
    /// <summary>
    /// What a disk looks like in co-op: a small carved totem (three stacked faces in dark wood, rusty
    /// wire wrapped around it, bone teeth and horns, and glowing eyes on the top face). Holding one
    /// revives a teammate, so it reads as a charm, not a save file.
    ///
    /// Everything about the look lives in this file: the 3D model (on the ground, in the backpack and
    /// in teammates' hands), its pixel textures and the first-person hand sprite. To give the revive
    /// item a different look later, change this file only; <see cref="DiskTotem"/> just swaps it in.
    ///
    /// Pure math (no Unity object calls), so tools/avatar-preview can render it without the game.
    /// </summary>
    public static class TotemLook
    {
        // ------------------------------------------------------------------ 3D model

        public const int WoodSlot = 0;
        public const int WireSlot = 1;
        public const int BoneSlot = 2;
        public const int GlowSlot = 3;
        public const int SlotCount = 4;

        /// <summary>Height of the model from the bottom of the base to the horn tips, in meters.</summary>
        public const float Height = 0.27f;

        /// <summary>Texture repeat size, in meters (32 pixels across this).</summary>
        public const float TileSize = 0.12f;

        public static PixelTextures.Recipe[] Recipes()
        {
            var r = new PixelTextures.Recipe[SlotCount];
            r[WoodSlot] = new PixelTextures.Recipe
            {
                Base = new Color(0.33f, 0.2f, 0.13f),
                Grime = new Color(0.13f, 0.07f, 0.05f),
                Speck = new Color(0.5f, 0.34f, 0.22f),
                GrimeAmount = 0.35f,
                SpeckAmount = 0.06f,
                Contrast = 0.55f,
                Stripes = true, // wood grain
            };
            r[WireSlot] = new PixelTextures.Recipe
            {
                Base = new Color(0.6f, 0.27f, 0.13f),
                Grime = new Color(0.25f, 0.1f, 0.06f),
                Speck = new Color(0.85f, 0.5f, 0.25f),
                GrimeAmount = 0.3f,
                SpeckAmount = 0.1f,
                Contrast = 0.5f,
            };
            r[BoneSlot] = new PixelTextures.Recipe
            {
                Base = new Color(0.74f, 0.7f, 0.62f),
                Grime = new Color(0.4f, 0.36f, 0.3f),
                Speck = new Color(0.9f, 0.88f, 0.82f),
                GrimeAmount = 0.2f,
                SpeckAmount = 0.05f,
                Contrast = 0.35f,
            };
            r[GlowSlot] = new PixelTextures.Recipe
            {
                Base = new Color(1f, 0.7f, 0.22f),
                Flat = true,
            };
            return r;
        }

        /// <summary>
        /// Builds the totem standing up: the bottom of the base at <paramref name="origin"/>, growing along
        /// <paramref name="up"/>, faces looking along <paramref name="front"/>, scaled by <paramref name="scale"/>.
        /// </summary>
        public static void Build(MeshKit k, Vector3 origin, Vector3 up, Vector3 front, float scale = 1f)
        {
            int start = k.Vertices.Count;
            k.TileSize = TileSize;
            BuildUpright(k);
            // Move the upright model (y up, faces toward +z) into place.
            Vector3 u = up.normalized;
            Vector3 f = (front - u * Vector3.Dot(front, u)).normalized;
            Vector3 r = Vector3.Cross(u, f);
            for (int i = start; i < k.Vertices.Count; i++)
            {
                Vector3 p = k.Vertices[i] * scale;
                k.Vertices[i] = origin + r * p.x + u * p.y + f * p.z;
                Vector3 n = k.Normals[i];
                k.Normals[i] = r * n.x + u * n.y + f * n.z;
            }
        }

        private static void BuildUpright(MeshKit k)
        {
            const float baseH = 0.026f, bodyH = 0.17f, capH = 0.02f;
            const float bodyBottom = baseH, bodyTop = baseH + bodyH;

            // Plinth, pillar and cap: dark carved wood.
            k.Use(WoodSlot);
            k.Block(Vector3.zero, baseH, 0.1f, 0.094f, 0.088f, 0.082f);
            k.Block(new Vector3(0, bodyBottom, 0), bodyH, 0.074f, 0.068f, 0.066f, 0.06f);
            k.Block(new Vector3(0, bodyTop, 0), capH, 0.084f, 0.078f, 0.074f, 0.066f);

            // Three faces stacked up the front.
            const int faces = 3;
            float section = bodyH / faces;
            for (int i = 0; i < faces; i++)
            {
                float cy = bodyBottom + section * (i + 0.5f);
                // Front of the pillar at this height (it tapers toward the top).
                float t = (cy - bodyBottom) / bodyH;
                float zf = Mathf.Lerp(0.034f, 0.03f, t);
                float halfW = Mathf.Lerp(0.037f, 0.033f, t);
                bool top = i == faces - 1;

                // Heavy brow ridge.
                k.Use(WoodSlot);
                k.Box(new Vector3(0, cy + 0.015f, zf + 0.004f), new Vector3(halfW * 1.9f, 0.007f, 0.009f));
                // Eyes: glowing on the top face, bone inlays below.
                k.Use(top ? GlowSlot : BoneSlot);
                k.Box(new Vector3(-0.014f, cy + 0.007f, zf + 0.0015f), new Vector3(0.012f, 0.006f, 0.005f));
                k.Box(new Vector3(0.014f, cy + 0.007f, zf + 0.0015f), new Vector3(0.012f, 0.006f, 0.005f));
                // A wedge of a nose.
                k.Use(WoodSlot);
                k.Block(new Vector3(0, cy - 0.008f, zf), 0.012f, 0.011f, 0.012f, 0.004f, 0.004f, -0.004f);
                // A row of square teeth.
                k.Use(BoneSlot);
                for (int tooth = 0; tooth < 4; tooth++)
                {
                    float x = (tooth - 1.5f) * 0.0075f;
                    k.Box(new Vector3(x, cy - 0.016f, zf + 0.002f), new Vector3(0.006f, 0.006f, 0.006f));
                }
            }

            // Two horns sweeping up and out of the cap.
            k.Use(BoneSlot);
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 root = new Vector3(side * 0.024f, bodyTop + capH - 0.002f, 0);
                Vector3 mid = root + new Vector3(side * 0.008f, 0.024f, 0);
                Vector3 tip = mid + new Vector3(side * 0.008f, Height - (bodyTop + capH) - 0.024f, -0.004f);
                k.Tube(root, mid, 0.009f, 0.006f, 6);
                k.Tube(mid, tip, 0.006f, 0.0006f, 6);
            }

            // Rusty wire wrapped round the pillar in two bands of two strands.
            k.Use(WireSlot);
            float[] bands = { bodyBottom + bodyH / 3f - 0.003f, bodyBottom + bodyH * 2f / 3f - 0.003f };
            foreach (float y in bands)
            {
                for (int strand = 0; strand < 2; strand++)
                {
                    float sy = y + strand * 0.0065f;
                    float t = (sy - bodyBottom) / bodyH;
                    float radius = Mathf.Lerp(0.052f, 0.048f, t);
                    // Tilted a touch so the wraps look hand tied.
                    Vector3 normal = new Vector3(0.06f * (strand == 0 ? 1f : -1f), 1f, 0.05f);
                    k.Torus(new Vector3(0, sy, 0), normal, radius, 0.0026f, 8, 4);
                }
            }
        }

        // ------------------------------------------------------------------ First-person sprite

        /// <summary>Size of the hand sprite in pixels, the same as the game's disk sprite.</summary>
        public const int SpriteSize = 128;

        /// <summary>Pixels per meter of the hand sprite, the same as the game's disk sprite.</summary>
        public const float SpritePixelsPerUnit = 100f;

        private static readonly Color32 Clear = new Color32(0, 0, 0, 0);
        private static readonly Color32 Outline = new Color32(150, 142, 132, 255);

        private enum Mat : byte { None, Wood, WoodSide, WoodDark, WoodLight, Wire, WireDark, Bone, BoneDark, Glow, GlowCore }

        /// <summary>
        /// The totem as the player sees it in their own hand: a 128 px pixel-art sprite in the game's
        /// style (grainy, a few shades, a light one-pixel outline), tilted like the disk was.
        /// Rows run bottom to top, as Texture2D.SetPixels32 expects.
        /// </summary>
        public static Color32[] DrawSprite()
        {
            const int n = SpriteSize;
            var design = new Mat[n, n];
            Paint(design);

            // Tilt it a little, as the disk sprite is, sampling the design at each output pixel.
            float angle = -9f * Mathf.Deg2Rad;
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            const float cx = 64f, cy = 62f;
            var mats = new Mat[n, n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    int sx = Mathf.FloorToInt(cx + dx * cos + dy * sin);
                    int sy = Mathf.FloorToInt(cy - dx * sin + dy * cos);
                    mats[x, y] = sx >= 0 && sy >= 0 && sx < n && sy < n ? design[sx, sy] : Mat.None;
                }
            }

            var pixels = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    Mat m = mats[x, y];
                    Color32 c;
                    if (m != Mat.None)
                    {
                        c = Shade(m, x, y);
                    }
                    else if (Near(mats, x, y))
                    {
                        c = Outline;
                    }
                    else
                    {
                        c = Clear;
                    }
                    pixels[y * n + x] = c;
                }
            }
            return pixels;
        }

        private static bool Near(Mat[,] mats, int x, int y)
        {
            int n = mats.GetLength(0);
            return (x > 0 && mats[x - 1, y] != Mat.None) || (x < n - 1 && mats[x + 1, y] != Mat.None)
                || (y > 0 && mats[x, y - 1] != Mat.None) || (y < n - 1 && mats[x, y + 1] != Mat.None);
        }

        /// <summary>Draws the upright totem into the grid (x right, y up), centered on x = 64.</summary>
        private static void Paint(Mat[,] g)
        {
            const int mid = 64;
            // Plinth.
            Trapezoid(g, 12, 21, 27, 24, Mat.Wood, 5);
            Fill(g, mid - 27, 12, mid + 27, 13, Mat.WoodDark);
            // Pillar.
            const int bodyBottom = 21, bodyTop = 90;
            Trapezoid(g, bodyBottom, bodyTop, 20, 17, Mat.Wood, 5);
            // Cap.
            Trapezoid(g, bodyTop, bodyTop + 8, 25, 22, Mat.Wood, 5);
            Fill(g, mid - 25, bodyTop, mid + 25, bodyTop, Mat.WoodDark);
            Fill(g, mid - 22, bodyTop + 8, mid + 22, bodyTop + 8, Mat.WoodLight);

            // Horns, curving up and out from the cap.
            for (int side = -1; side <= 1; side += 2)
            {
                for (int y = bodyTop + 9; y <= 124; y++)
                {
                    float t = (y - (bodyTop + 9)) / (float)(124 - (bodyTop + 9));
                    float center = mid + side * (13f + 9f * t * t);
                    float half = Mathf.Lerp(3.6f, 0.4f, t);
                    for (int x = Mathf.FloorToInt(center - half); x <= Mathf.CeilToInt(center + half); x++)
                    {
                        if (Mathf.Abs(x + 0.5f - center) <= half + 0.01f)
                        {
                            Set(g, x, y, (x + 0.5f - center) * side > half * 0.3f ? Mat.BoneDark : Mat.Bone);
                        }
                    }
                }
            }

            // Three faces.
            const int faces = 3;
            float section = (bodyTop - bodyBottom) / (float)faces;
            for (int i = 0; i < faces; i++)
            {
                int cy = Mathf.RoundToInt(bodyBottom + section * (i + 0.5f));
                bool top = i == faces - 1;
                // Brow: a lit top edge over a dark shadow.
                Fill(g, mid - 17, cy + 8, mid + 15, cy + 8, Mat.WoodLight);
                Fill(g, mid - 17, cy + 6, mid + 15, cy + 7, Mat.WoodDark);
                // Eyes.
                for (int side = -1; side <= 1; side += 2)
                {
                    int ex = mid + side * 7;
                    Fill(g, ex - 3, cy + 2, ex + 2, cy + 5, Mat.WoodDark);
                    if (top)
                    {
                        Fill(g, ex - 3, cy + 2, ex + 2, cy + 4, Mat.Glow);
                        Fill(g, ex - 1, cy + 3, ex, cy + 3, Mat.GlowCore);
                    }
                    else
                    {
                        Fill(g, ex - 2, cy + 3, ex + 1, cy + 4, Mat.Bone);
                    }
                }
                // Nose: a wedge, lit on its left.
                for (int y = cy - 4; y <= cy + 1; y++)
                {
                    int half = (cy + 1 - y) / 2 + 1;
                    Fill(g, mid - half, y, mid - 1, y, Mat.WoodLight);
                    Fill(g, mid, y, mid + half - 1, y, Mat.WoodDark);
                }
                // Mouth and teeth.
                Fill(g, mid - 9, cy - 9, mid + 8, cy - 6, Mat.WoodDark);
                for (int tooth = 0; tooth < 4; tooth++)
                {
                    int tx = mid - 8 + tooth * 4;
                    Fill(g, tx, cy - 8, tx + 2, cy - 7, Mat.Bone);
                    Set(g, tx + 2, cy - 8, Mat.BoneDark);
                }
                // A carved groove between faces.
                if (i > 0)
                {
                    int gy = Mathf.RoundToInt(bodyBottom + section * i);
                    Fill(g, mid - 20, gy, mid + 18, gy, Mat.WoodDark);
                }
            }

            // Wire wraps: two strands tied round each groove between the faces, sticking out past the sides.
            for (int band = 1; band < faces; band++)
            {
                int groove = Mathf.RoundToInt(bodyBottom + section * band);
                for (int strand = 0; strand < 2; strand++)
                {
                    float y0 = groove - 1 + strand * 3;
                    for (int x = mid - 24; x <= mid + 23; x++)
                    {
                        float t = (x - (mid - 24)) / 47f;
                        int y = Mathf.RoundToInt(y0 + (strand == 0 ? 1f : -1f) * (t - 0.5f) + Mathf.Sin(t * 9f + band) * 0.45f);
                        Set(g, x, y + 1, Mat.Wire);
                        Set(g, x, y, Mat.WireDark);
                    }
                }
            }
        }

        /// <summary>A horizontally centered trapezoid; the right <paramref name="side"/> pixels are in shadow.</summary>
        private static void Trapezoid(Mat[,] g, int y0, int y1, int halfBottom, int halfTop, Mat m, int side)
        {
            for (int y = y0; y <= y1; y++)
            {
                float t = y1 == y0 ? 0f : (y - y0) / (float)(y1 - y0);
                int half = Mathf.RoundToInt(Mathf.Lerp(halfBottom, halfTop, t));
                for (int x = 64 - half; x < 64 + half; x++)
                {
                    Set(g, x, y, x >= 64 + half - side ? Mat.WoodSide : m);
                }
                Set(g, 64 - half, y, Mat.WoodLight);
            }
        }

        private static void Fill(Mat[,] g, int x0, int y0, int x1, int y1, Mat m)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    Set(g, x, y, m);
                }
            }
        }

        private static void Set(Mat[,] g, int x, int y, Mat m)
        {
            if (x >= 0 && y >= 0 && x < g.GetLength(0) && y < g.GetLength(1))
            {
                g[x, y] = m;
            }
        }

        /// <summary>Color of one pixel: the material's color with grainy, banded noise like the game's art.</summary>
        private static Color32 Shade(Mat m, int x, int y)
        {
            Color c;
            switch (m)
            {
                case Mat.Wood: c = new Color(0.36f, 0.22f, 0.14f); break;
                case Mat.WoodSide: c = new Color(0.24f, 0.14f, 0.09f); break;
                case Mat.WoodDark: c = new Color(0.14f, 0.08f, 0.05f); break;
                case Mat.WoodLight: c = new Color(0.5f, 0.33f, 0.21f); break;
                case Mat.Wire: c = new Color(0.72f, 0.33f, 0.15f); break;
                case Mat.WireDark: c = new Color(0.42f, 0.17f, 0.08f); break;
                case Mat.Bone: c = new Color(0.8f, 0.77f, 0.7f); break;
                case Mat.BoneDark: c = new Color(0.55f, 0.51f, 0.45f); break;
                case Mat.Glow: return new Color32(255, 170, 40, 255);
                case Mat.GlowCore: return new Color32(255, 236, 150, 255);
                default: return Clear;
            }
            // Wood grain runs up the totem; everything gets fine speckle.
            float grain = m == Mat.Wood || m == Mat.WoodSide ? Hash(x / 2, y / 7, 17) : 0.5f;
            float speck = Hash(x, y, 31);
            float shade = 0.82f + 0.22f * grain + (speck > 0.9f ? 0.12f : speck < 0.08f ? -0.14f : 0f);
            c *= shade;
            c.a = 1f;
            return c;
        }

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
    }
}
