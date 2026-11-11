using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// Turns a White Knuckle hand-skin sprite into a texture for the 3D glove. The game's hand sprites
    /// are palette-indexed (the red channel holds an index into a 16x1 palette strip), so this applies the
    /// chosen palette and the player's hue/saturation/value tweak, finds the hand's outline, and bleeds
    /// edge colors into the empty background so the 3D hand's sides pick up sensible colors.
    /// Pure pixel math, shared with the preview tool.
    /// </summary>
    public static class SkinBaker
    {
        public struct Result
        {
            public Color32[] Pixels;
            public int Width, Height;
            /// <summary>Bounds of the visible hand in pixels (inclusive min, exclusive max).</summary>
            public int MinX, MinY, MaxX, MaxY;
            /// <summary>
            /// The hand's own frame: the art is drawn at a slant, so this is its center, the direction from
            /// cuff to fingertips, and how far it reaches along and across that direction.
            /// </summary>
            public float CenterX, CenterY, AxisX, AxisY, MinAlong, MaxAlong, MinAcross, MaxAcross;
            /// <summary>Colors drawn on the fingers (outline and shadow lines left out), for the 3D fingers.</summary>
            public Color32[] FingerColors;
        }

        /// <summary>
        /// Stand-ins for skins whose look comes from a special shader instead of their own art
        /// (Inverted, Experienced, Streaked, Celestial, Sunspot), worked out on the CPU.
        /// </summary>
        public class Effect
        {
            /// <summary>Flip every color (the game's "Invert" hand materials).</summary>
            public bool Invert;
            /// <summary>A picture multiplied over the hand (scars, streaks), same layout as the sprite.</summary>
            public Color32[] Multiply;
            public int MultiplyWidth, MultiplyHeight;
            /// <summary>A picture the hand becomes a window onto (starfield, sunset), shaded by the art.</summary>
            public Color32[] Window;
            public int WindowWidth, WindowHeight;

            public static Effect For(string materialName, string secondaryName, Color32[] secondary, int secondaryWidth, int secondaryHeight)
            {
                materialName = materialName ?? "";
                var e = new Effect { Invert = materialName.IndexOf("Invert", System.StringComparison.OrdinalIgnoreCase) >= 0 };
                if (secondary != null && secondaryWidth > 0 && secondaryHeight > 0)
                {
                    if (secondaryName == "_OverlayMult")
                    {
                        e.Multiply = secondary; e.MultiplyWidth = secondaryWidth; e.MultiplyHeight = secondaryHeight;
                    }
                    else if (secondaryName == "_StaminaTexture" && materialName.IndexOf("EmissiveStamina", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        e.Window = secondary; e.WindowWidth = secondaryWidth; e.WindowHeight = secondaryHeight;
                    }
                }
                return e.Invert || e.Multiply != null || e.Window != null ? e : null;
            }

            /// <summary>A copy of this effect that also multiplies <paramref name="pixels"/> over the hand.</summary>
            public Effect WithMultiply(Color32[] pixels, int width, int height)
            {
                var e = (Effect)MemberwiseClone();
                e.Multiply = pixels; e.MultiplyWidth = width; e.MultiplyHeight = height;
                return e;
            }

            internal Color32 Apply(Color32 c, float u, float v)
            {
                if (Invert)
                {
                    c = new Color32((byte)(255 - c.r), (byte)(255 - c.g), (byte)(255 - c.b), c.a);
                }
                if (Multiply != null)
                {
                    Color32 m = Sample(Multiply, MultiplyWidth, MultiplyHeight, u, v);
                    if (m.a > 0)
                    {
                        float k = m.a / 255f;
                        c = new Color32(Mix(c.r, c.r * m.r / 255, k), Mix(c.g, c.g * m.g / 255, k), Mix(c.b, c.b * m.b / 255, k), c.a);
                    }
                }
                if (Window != null)
                {
                    float light = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
                    Color32 w = Sample(Window, WindowWidth, WindowHeight, u, v);
                    float glow = (0.299f * w.r + 0.587f * w.g + 0.114f * w.b) / 255f;
                    if (Invert)
                    {
                        // Celestial: a dark hand full of colored stars.
                        float star = Mathf.Pow(glow, 2.2f) * 1.6f;
                        c = new Color32(Clamp(c.r * 0.35f + w.r * star), Clamp(c.g * 0.35f + w.g * star), Clamp(c.b * 0.4f + 10 + w.b * star), c.a);
                    }
                    else
                    {
                        // Sunrise: a glowing pale hand that warms into the picture toward the fingertips.
                        float k = Mathf.SmoothStep(0.35f, 0.9f, v) * 0.85f;
                        float pale = Mathf.Lerp(0.75f, 1.1f, light);
                        c = new Color32(Clamp(Mathf.Lerp(255 * pale, w.r, k)), Clamp(Mathf.Lerp(250 * pale, w.g, k)), Clamp(Mathf.Lerp(245 * pale, w.b, k)), c.a);
                    }
                }
                return c;
            }

            private static Color32 Sample(Color32[] px, int w, int h, float u, float v)
            {
                int x = Mathf.Clamp(Mathf.FloorToInt(u * w), 0, w - 1), y = Mathf.Clamp(Mathf.FloorToInt(v * h), 0, h - 1);
                return px[y * w + x];
            }

            private static byte Mix(int a, int b, float k) => (byte)Mathf.Clamp(Mathf.RoundToInt(a + (b - a) * k), 0, 255);
            private static byte Clamp(float x) => (byte)Mathf.Clamp(Mathf.RoundToInt(x), 0, 255);
        }

        /// <param name="pixels">Sprite pixels, row 0 at the bottom (Unity order).</param>
        /// <param name="palette">The palette strip, or null for a full-color sprite.</param>
        /// <param name="hsv">Hue shift, saturation and value multipliers; null leaves colors alone.</param>
        /// <param name="effect">A shader effect to imitate, or null.</param>
        public static Result Bake(Color32[] pixels, int width, int height, Color32[] palette, Vector3? hsv, Effect effect = null)
        {
            var output = new Color32[pixels.Length];
            bool indexed = palette != null && palette.Length > 0 && LooksIndexed(pixels, palette.Length);
            int minX = width, minY = height, maxX = 0, maxY = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color32 p = pixels[y * width + x];
                    if (p.a < 128)
                    {
                        output[y * width + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }
                    Color32 c = indexed ? palette[Mathf.Min(p.r, palette.Length - 1)] : p;
                    if (indexed && c.a < 128)
                    {
                        output[y * width + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }
                    if (hsv.HasValue)
                    {
                        c = Shift(c, hsv.Value);
                    }
                    if (effect != null)
                    {
                        c = effect.Apply(c, (x + 0.5f) / width, (y + 0.5f) / height);
                    }
                    c.a = 255;
                    output[y * width + x] = c;
                    minX = Mathf.Min(minX, x);
                    minY = Mathf.Min(minY, y);
                    maxX = Mathf.Max(maxX, x + 1);
                    maxY = Mathf.Max(maxY, y + 1);
                }
            }
            if (maxX <= minX || maxY <= minY)
            {
                minX = minY = 0;
                maxX = width;
                maxY = height;
            }
            // The art has a one pixel outline around the hand. On the 3D hand it would wrap onto the sides
            // as bright or dark streaks, so peel it off.
            Erode(output, width, height);
            var result = new Result { Pixels = output, Width = width, Height = height, MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
            FindAxis(output, width, height, ref result);
            result.FingerColors = FingerColors(output, width, height, result);
            Bleed(output, width, height);
            return result;
        }

        /// <summary>Principal axis of the visible pixels, pointing up the image (toward the fingers).</summary>
        private static void FindAxis(Color32[] px, int w, int h, ref Result r)
        {
            double sx = 0, sy = 0, n = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (px[y * w + x].a != 0) { sx += x; sy += y; n++; }
            if (n < 1)
            {
                r.CenterX = w * 0.5f; r.CenterY = h * 0.5f; r.AxisX = 0; r.AxisY = 1;
                r.MinAlong = -h * 0.5f; r.MaxAlong = h * 0.5f; r.MinAcross = -w * 0.5f; r.MaxAcross = w * 0.5f;
                return;
            }
            double cx = sx / n, cy = sy / n, cxx = 0, cyy = 0, cxy = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (px[y * w + x].a != 0)
                    {
                        double dx = x - cx, dy = y - cy;
                        cxx += dx * dx; cyy += dy * dy; cxy += dx * dy;
                    }
            double angle = 0.5 * System.Math.Atan2(2 * cxy, cxx - cyy);
            float ax = (float)System.Math.Cos(angle), ay = (float)System.Math.Sin(angle);
            // Hands are drawn fingers-up; never let the axis lie flatter than 45 degrees or point down.
            if (Mathf.Abs(ay) < Mathf.Abs(ax))
            {
                float t = ax; ax = -ay; ay = t;
            }
            if (ay < 0) { ax = -ax; ay = -ay; }
            float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (px[y * w + x].a != 0)
                    {
                        float dx = (float)(x + 0.5 - cx), dy = (float)(y + 0.5 - cy);
                        float along = dx * ax + dy * ay, across = dx * ay - dy * ax;
                        minA = Mathf.Min(minA, along); maxA = Mathf.Max(maxA, along);
                        minB = Mathf.Min(minB, across); maxB = Mathf.Max(maxB, across);
                    }
            r.CenterX = (float)cx; r.CenterY = (float)cy; r.AxisX = ax; r.AxisY = ay;
            r.MinAlong = minA; r.MaxAlong = maxA; r.MinAcross = minB; r.MaxAcross = maxB;
        }

        /// <summary>Clears every visible pixel that touches an empty one (left, right, above or below).</summary>
        private static void Erode(Color32[] px, int w, int h)
        {
            var clear = new System.Collections.Generic.List<int>();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (px[i].a == 0)
                    {
                        continue;
                    }
                    if (x == 0 || y == 0 || x == w - 1 || y == h - 1
                        || px[i - 1].a == 0 || px[i + 1].a == 0 || px[i - w].a == 0 || px[i + w].a == 0)
                    {
                        clear.Add(i);
                    }
                }
            }
            // Never erase a hand that is all edge.
            int opaque = 0;
            foreach (Color32 c in px)
            {
                if (c.a != 0) opaque++;
            }
            if (clear.Count * 2 > opaque)
            {
                return;
            }
            foreach (int i in clear)
            {
                px[i] = new Color32(0, 0, 0, 0);
            }
        }

        /// <summary>
        /// The colors of the top part of the hand (the fingers), skipping the darkest fifth, which is
        /// the shading and the lines between fingers.
        /// </summary>
        private static Color32[] FingerColors(Color32[] px, int w, int h, Result r)
        {
            var picked = new System.Collections.Generic.List<Color32>();
            float from = Mathf.Lerp(r.MinAlong, r.MaxAlong, 0.62f);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color32 c = px[y * w + x];
                    if (c.a == 0)
                    {
                        continue;
                    }
                    float along = (x + 0.5f - r.CenterX) * r.AxisX + (y + 0.5f - r.CenterY) * r.AxisY;
                    if (along >= from)
                    {
                        picked.Add(c);
                    }
                }
            }
            if (picked.Count == 0)
            {
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i].a != 0) picked.Add(px[i]);
                }
            }
            if (picked.Count == 0)
            {
                return new[] { new Color32(90, 80, 76, 255) };
            }
            picked.Sort((a, b) => Luma(a).CompareTo(Luma(b)));
            int skip = picked.Count >= 10 ? picked.Count / 5 : 0;
            return picked.GetRange(skip, picked.Count - skip).ToArray();
        }

        private static int Luma(Color32 c) => c.r * 299 + c.g * 587 + c.b * 114;

        private static bool LooksIndexed(Color32[] pixels, int paletteSize)
        {
            int opaque = 0;
            foreach (Color32 p in pixels)
            {
                if (p.a < 128)
                {
                    continue;
                }
                opaque++;
                if (p.g != 0 || p.b != 0 || p.r >= paletteSize)
                {
                    return false;
                }
            }
            return opaque > 0;
        }

        private static Color32 Shift(Color32 c, Vector3 hsv)
        {
            Color.RGBToHSV(c, out float h, out float s, out float v);
            h = Mathf.Repeat(h + hsv.x, 1f);
            Color shifted = Color.HSVToRGB(h, Mathf.Clamp01(s * hsv.y), Mathf.Clamp01(v * hsv.z));
            return shifted;
        }

        /// <summary>Fills transparent pixels with the nearest visible color, a few pixels at a time.</summary>
        private static void Bleed(Color32[] px, int w, int h)
        {
            var next = (Color32[])px.Clone();
            for (int pass = 0; pass < 24; pass++)
            {
                bool changed = false;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (px[y * w + x].a != 0)
                        {
                            continue;
                        }
                        int r = 0, g = 0, b = 0, n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                                {
                                    continue;
                                }
                                Color32 q = px[ny * w + nx];
                                if (q.a != 0)
                                {
                                    r += q.r; g += q.g; b += q.b; n++;
                                }
                            }
                        }
                        if (n > 0)
                        {
                            next[y * w + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 254);
                            changed = true;
                        }
                    }
                }
                System.Array.Copy(next, px, px.Length);
                if (!changed)
                {
                    break;
                }
            }
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].a == 0)
                {
                    px[i] = new Color32(40, 36, 36, 255);
                }
                px[i].a = 255;
            }
        }

        /// <summary>
        /// Packs the back-of-hand picture (left third), the palm picture (middle third) and a swatch of
        /// the finger colors (right third) into one texture, each picture cropped to the hand and
        /// stretched to fill its cell (nearest neighbor, so pixels stay crisp).
        /// With <paramref name="mirror"/>, both pictures are flipped left to right (for the other hand).
        /// The texture is <paramref name="cellWidth"/> * 3 wide.
        /// </summary>
        public static Color32[] Atlas(Result back, Result palm, bool mirror, int cellWidth, int cellHeight)
        {
            int stride = cellWidth * 3;
            var atlas = new Color32[stride * cellHeight];
            Fill(atlas, stride, 0, back, mirror, cellWidth, cellHeight);
            Fill(atlas, stride, cellWidth, palm, mirror, cellWidth, cellHeight);
            Color32[] fingers = back.FingerColors != null && back.FingerColors.Length > 0 ? back.FingerColors
                : new[] { new Color32(90, 80, 76, 255) };
            // Pixel noise in 2x2 blocks, like the rest of the avatar's textures.
            for (int y = 0; y < cellHeight; y++)
            {
                for (int x = 0; x < cellWidth; x++)
                {
                    uint hash = (uint)(x / 2) * 73856093u ^ (uint)(y / 2) * 19349663u;
                    hash ^= hash >> 13;
                    hash *= 0x5bd1e995u;
                    hash ^= hash >> 15;
                    atlas[y * stride + cellWidth * 2 + x] = fingers[(int)(hash % (uint)fingers.Length)];
                }
            }
            return atlas;
        }

        private static void Fill(Color32[] atlas, int stride, int left, Result r, bool mirror, int w, int h)
        {
            // Walk the hand's own slanted frame so the art lines up with the straight 3D hand:
            // v runs cuff to fingertips, u runs across (pinky side to thumb side of the picture).
            for (int y = 0; y < h; y++)
            {
                float along = Mathf.Lerp(r.MinAlong, r.MaxAlong, (y + 0.5f) / h);
                for (int x = 0; x < w; x++)
                {
                    float u = ((mirror ? w - 1 - x : x) + 0.5f) / w;
                    float across = Mathf.Lerp(r.MinAcross, r.MaxAcross, u);
                    float sx = r.CenterX + r.AxisX * along + r.AxisY * across;
                    float sy = r.CenterY + r.AxisY * along - r.AxisX * across;
                    int ix = Mathf.Clamp(Mathf.FloorToInt(sx), 0, r.Width - 1);
                    int iy = Mathf.Clamp(Mathf.FloorToInt(sy), 0, r.Height - 1);
                    atlas[y * stride + left + x] = r.Pixels[iy * r.Width + ix];
                }
            }
        }
    }
}
