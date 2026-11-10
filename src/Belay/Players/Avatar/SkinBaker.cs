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
        }

        /// <param name="pixels">Sprite pixels, row 0 at the bottom (Unity order).</param>
        /// <param name="palette">The palette strip, or null for a full-color sprite.</param>
        /// <param name="hsv">Hue shift, saturation and value multipliers; null leaves colors alone.</param>
        public static Result Bake(Color32[] pixels, int width, int height, Color32[] palette, Vector3? hsv)
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
            var result = new Result { Pixels = output, Width = width, Height = height, MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
            FindAxis(output, width, height, ref result);
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
        /// Packs the back-of-hand picture (left half) and the palm picture (right half) into one texture,
        /// each cropped to the hand and stretched to fill its half (nearest neighbor, so pixels stay crisp).
        /// With <paramref name="mirror"/>, both pictures are flipped left to right (for the other hand).
        /// </summary>
        public static Color32[] Atlas(Result back, Result palm, bool mirror, int cellWidth, int cellHeight)
        {
            var atlas = new Color32[cellWidth * 2 * cellHeight];
            Fill(atlas, cellWidth * 2, 0, back, mirror, cellWidth, cellHeight);
            Fill(atlas, cellWidth * 2, cellWidth, palm, mirror, cellWidth, cellHeight);
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
