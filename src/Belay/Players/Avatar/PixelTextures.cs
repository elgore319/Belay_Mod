using UnityEngine;

namespace Belay.Players.Avatar
{
    /// <summary>
    /// Generates the small, point-filtered, grimy pixel textures that give the climber White Knuckle's
    /// look: blocky value noise quantized into a few shades, dark blotches of grime and bright specks,
    /// all tileable. Pure math (no Unity texture calls), so the preview tool can use it too.
    /// </summary>
    public static class PixelTextures
    {
        public const int Size = 32;

        public struct Recipe
        {
            public Color Base;
            public Color Grime;
            public Color Speck;
            public float GrimeAmount;  // 0..1 share of the surface covered in grime blotches
            public float SpeckAmount;  // 0..1 share of single bright/dark pixels
            public float Contrast;     // spread of the shade bands
            public bool Stripes;       // woven webbing rows
            public bool Flat;          // a single color (lamp lens)
        }

        private static readonly float[] Shades = { 0.5f, 0.68f, 0.84f, 1f, 1.16f };

        public static Color32[] Generate(Recipe r, int seed)
        {
            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    Color c = r.Base;
                    if (!r.Flat)
                    {
                        float v = 0.4f * Noise(x, y, 8, seed) + 0.3f * Noise(x, y, 4, seed + 11) + 0.3f * Hash(x, y, seed + 23);
                        if (r.Stripes)
                        {
                            v = 0.6f * v + 0.4f * ((y % 4) < 2 ? 0.75f : 0.25f);
                        }
                        int band = Mathf.Clamp(Mathf.FloorToInt(Mathf.Lerp(0.5f - r.Contrast, 0.5f + r.Contrast, v) * Shades.Length), 0, Shades.Length - 1);
                        c = r.Base * Shades[band];

                        float grime = 0.6f * Noise(x, y, 8, seed + 37) + 0.4f * Noise(x, y, 2, seed + 41);
                        if (grime > 1f - r.GrimeAmount)
                        {
                            c = Color.Lerp(c, r.Grime, grime > 1f - r.GrimeAmount * 0.5f ? 0.9f : 0.55f);
                        }
                        if (Hash(x, y, seed + 53) > 1f - r.SpeckAmount)
                        {
                            c = Hash(x, y, seed + 59) > 0.5f ? r.Speck : Color.Lerp(c, r.Grime, 0.8f);
                        }
                    }
                    c.a = 1f;
                    pixels[y * Size + x] = c;
                }
            }
            return pixels;
        }

        /// <summary>Blocky, tileable value noise with cells <paramref name="cell"/> pixels wide.</summary>
        private static float Noise(int x, int y, int cell, int seed)
        {
            int cells = Size / cell;
            float fx = (float)x / cell, fy = (float)y / cell;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = Smooth(fx - x0), ty = Smooth(fy - y0);
            float a = Hash(x0 % cells, y0 % cells, seed);
            float b = Hash((x0 + 1) % cells, y0 % cells, seed);
            float c = Hash(x0 % cells, (y0 + 1) % cells, seed);
            float d = Hash((x0 + 1) % cells, (y0 + 1) % cells, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);

        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }
    }
}
