using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace Belay.UI
{
    /// <summary>
    /// The game's menu font ("Ticketing") for the Belay menu. The game only ships it as a bitmap baked at one size, which
    /// IMGUI can't scale, so the first time it's seen its pixels are traced into a small TrueType file in BepInEx/cache
    /// and loaded from there as a normal scalable font. Nothing of the game's is changed. If anything goes wrong the menu
    /// keeps Unity's default font.
    /// </summary>
    internal static class GameFont
    {
        private const string SourceName = "Ticketing";
        private const string CacheFile = "Ticketing-v1.ttf";

        private static Font font;
        private static bool failed;
        private static float nextSearch;
        private static float giveUpAt = -1f;

        /// <summary>The scalable game font, or null while it isn't available (yet).</summary>
        public static Font Font => font;

        private static string CachePath => Path.Combine(Path.Combine(Paths.CachePath, "Belay"), CacheFile);

        /// <summary>Call from Update. Loads the cached font, or builds it once the game's font is in memory.</summary>
        public static void Tick()
        {
            if (font != null || failed)
            {
                return;
            }
            float now = Time.unscaledTime;
            if (giveUpAt < 0f)
            {
                giveUpAt = now + 120f;
                if (File.Exists(CachePath) && TryLoad(CachePath))
                {
                    return;
                }
            }
            if (now < nextSearch)
            {
                return;
            }
            nextSearch = now + 2f;
            try
            {
                Font source = null;
                foreach (Font f in Resources.FindObjectsOfTypeAll<Font>())
                {
                    if (f != null && f.name == SourceName && !f.dynamic)
                    {
                        source = f;
                        break;
                    }
                }
                if (source == null)
                {
                    if (now > giveUpAt)
                    {
                        failed = true;
                        Plugin.Log.LogInfo("[Theme] The game's menu font wasn't found; the Belay menu uses the default font.");
                    }
                    return;
                }
                byte[] ttf = Build(source);
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                File.WriteAllBytes(CachePath, ttf);
                if (!TryLoad(CachePath))
                {
                    failed = true;
                }
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogWarning("[Theme] Couldn't make the game font for the Belay menu: " + e.Message);
            }
        }

        private static bool TryLoad(string path)
        {
            try
            {
                var loaded = new Font(path);
                loaded.name = "Belay Ticketing";
                loaded.hideFlags = HideFlags.HideAndDontSave;
                if (!loaded.dynamic)
                {
                    Plugin.Log.LogWarning("[Theme] The game font file didn't load as a font: " + path);
                    return false;
                }
                font = loaded;
                Plugin.Log.LogInfo("[Theme] Belay menu uses the game font (" + path + ")");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Theme] Couldn't load " + path + ": " + e.Message);
                return false;
            }
        }

        /// <summary>Traces every character of the baked font into pixel grids and writes them as a TrueType font.</summary>
        private static byte[] Build(Font source)
        {
            Texture texture = source.material != null ? source.material.mainTexture : null;
            if (texture == null)
            {
                throw new InvalidOperationException("the font has no texture");
            }
            int width = texture.width, height = texture.height;
            byte[] coverage = ReadCoverage(texture);

            var glyphs = new List<TtfWriter.Glyph>();
            foreach (CharacterInfo ci in source.characterInfo)
            {
                int w = ci.maxX - ci.minX, h = ci.maxY - ci.minY;
                var pixels = new bool[Math.Max(0, w), Math.Max(0, h)];
                for (int gy = 0; gy < h; gy++)
                {
                    float t = (h - gy - 0.5f) / h; // from the bottom
                    for (int gx = 0; gx < w; gx++)
                    {
                        float s = (gx + 0.5f) / w;
                        Vector2 uv = ci.uvBottomLeft + s * (ci.uvBottomRight - ci.uvBottomLeft) + t * (ci.uvTopLeft - ci.uvBottomLeft);
                        int x = Mathf.Clamp((int)(uv.x * width), 0, width - 1);
                        int y = Mathf.Clamp((int)(uv.y * height), 0, height - 1);
                        pixels[gx, gy] = coverage[y * width + x] > 127;
                    }
                }
                glyphs.Add(new TtfWriter.Glyph
                {
                    Codepoint = ci.index,
                    Advance = Mathf.RoundToInt(ci.advance),
                    Left = ci.minX,
                    Top = ci.maxY,
                    Pixels = pixels,
                });
            }
            FixOrientation(glyphs);
            int size = source.fontSize > 0 ? source.fontSize : 64;
            int ascent = source.ascent > 0 ? source.ascent : Mathf.RoundToInt(size * 0.9f);
            int descent = Mathf.Max(1, Mathf.RoundToInt(source.lineHeight) - ascent);
            Plugin.Log.LogInfo($"[Theme] Tracing {glyphs.Count} characters of the game font ({size} px, {width}x{height} atlas)");
            return TtfWriter.Build("Belay Ticketing", glyphs, size, ascent, -descent);
        }

        /// <summary>Copies a GPU-only texture into memory and returns each texel's coverage (0-255), row 0 at the bottom.</summary>
        private static byte[] ReadCoverage(Texture texture)
        {
            int width = texture.width, height = texture.height;
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            var copy = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                copy.ReadPixels(new UnityEngine.Rect(0, 0, width, height), 0, 0);
                copy.Apply(false);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
            Color32[] texels = copy.GetPixels32();
            UnityEngine.Object.Destroy(copy);

            // An alpha-only atlas normally comes through in alpha. If alpha is solid everywhere, it came through in red.
            int solid = 0;
            foreach (Color32 c in texels)
            {
                if (c.a > 127)
                {
                    solid++;
                }
            }
            bool useRed = solid > texels.Length * 0.9f;
            var coverage = new byte[texels.Length];
            for (int i = 0; i < texels.Length; i++)
            {
                coverage[i] = useRed ? texels[i].r : texels[i].a;
            }
            return coverage;
        }

        /// <summary>Checks 'A' (pointed top) and 'F' (stem on the left) and flips every glyph if they came out mirrored.</summary>
        private static void FixOrientation(List<TtfWriter.Glyph> glyphs)
        {
            TtfWriter.Glyph a = glyphs.Find(g => g.Codepoint == 'A');
            TtfWriter.Glyph f = glyphs.Find(g => g.Codepoint == 'F');
            bool flipY = a != null && Filled(a, 0f, 1f, 0.7f, 1f) > Filled(a, 0f, 1f, 0f, 0.3f);
            bool flipX = f != null && Filled(f, 0.6f, 1f, 0f, 1f) > Filled(f, 0f, 0.4f, 0f, 1f);
            if (!flipX && !flipY)
            {
                return;
            }
            Plugin.Log.LogInfo($"[Theme] Game font atlas is mirrored (x {flipX}, y {flipY}); flipping it back.");
            foreach (TtfWriter.Glyph g in glyphs)
            {
                int w = g.Pixels.GetLength(0), h = g.Pixels.GetLength(1);
                var p = new bool[w, h];
                for (int x = 0; x < w; x++)
                {
                    for (int y = 0; y < h; y++)
                    {
                        p[x, y] = g.Pixels[flipX ? w - 1 - x : x, flipY ? h - 1 - y : y];
                    }
                }
                g.Pixels = p;
            }
        }

        /// <summary>Filled pixels in a part of the glyph; y fractions are measured from the bottom.</summary>
        private static int Filled(TtfWriter.Glyph g, float x0, float x1, float y0, float y1)
        {
            int w = g.Pixels.GetLength(0), h = g.Pixels.GetLength(1), n = 0;
            for (int x = (int)(x0 * w); x < (int)(x1 * w); x++)
            {
                for (int yb = (int)(y0 * h); yb < (int)(y1 * h); yb++)
                {
                    if (g.Pixels[x, h - 1 - yb])
                    {
                        n++;
                    }
                }
            }
            return n;
        }
    }
}
