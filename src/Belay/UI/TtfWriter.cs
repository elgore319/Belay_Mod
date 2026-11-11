using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Belay.UI
{
    /// <summary>
    /// Writes a minimal TrueType font whose glyphs are made of square pixels. It turns the game's baked bitmap font
    /// into a font file Unity can load at any size (see GameFont). It has no Unity dependencies.
    /// </summary>
    internal static class TtfWriter
    {
        /// <summary>One character: a grid of filled pixels, row 0 at the top.</summary>
        internal class Glyph
        {
            public int Codepoint;
            /// <summary>Horizontal advance in pixels.</summary>
            public int Advance;
            /// <summary>Left edge of the pixel grid, in pixels from the pen position.</summary>
            public int Left;
            /// <summary>Top edge of the pixel grid, in pixels above the baseline.</summary>
            public int Top;
            /// <summary>[x, y] with y = 0 the top row.</summary>
            public bool[,] Pixels;
        }

        private struct Rect
        {
            public int X0, Y0, X1, Y1;
        }

        /// <param name="unitsPerPixel">Font units per source pixel; unitsPerEm = pixelsPerEm * unitsPerPixel.</param>
        public static byte[] Build(string family, IList<Glyph> glyphs, int pixelsPerEm, int ascentPx, int descentPx, int unitsPerPixel = 10)
        {
            int upp = unitsPerPixel;
            int unitsPerEm = pixelsPerEm * upp;
            var sorted = new List<Glyph>(glyphs);
            sorted.RemoveAll(g => g.Codepoint <= 0 || g.Codepoint > 0xFFFE);
            sorted.Sort((a, b) => a.Codepoint.CompareTo(b.Codepoint));
            // Drop duplicates.
            for (int i = sorted.Count - 1; i > 0; i--)
            {
                if (sorted[i].Codepoint == sorted[i - 1].Codepoint)
                {
                    sorted.RemoveAt(i);
                }
            }

            int numGlyphs = sorted.Count + 1; // + .notdef
            var glyf = new MemoryStream();
            var loca = new List<uint> { 0 };
            var advances = new List<int> { 0 };
            var lsbs = new List<int> { 0 };
            int maxPoints = 0, maxContours = 0;
            int xMinAll = 0, yMinAll = 0, xMaxAll = 0, yMaxAll = 0, advMax = 0;
            bool any = false;
            loca.Add(0); // .notdef is empty

            foreach (Glyph g in sorted)
            {
                List<Rect> rects = Rectangles(g.Pixels);
                int adv = g.Advance * upp;
                advances.Add(adv);
                advMax = Math.Max(advMax, adv);
                if (rects.Count == 0)
                {
                    lsbs.Add(0);
                    loca.Add((uint)glyf.Length);
                    continue;
                }
                // Pixel (x, y) covers font units [Left + x, Left + x + 1] by [Top - y - 1, Top - y].
                int xMin = int.MaxValue, yMin = int.MaxValue, xMax = int.MinValue, yMax = int.MinValue;
                var pts = new List<int[]>();
                foreach (Rect r in rects)
                {
                    int x0 = (g.Left + r.X0) * upp, x1 = (g.Left + r.X1) * upp;
                    int yTop = (g.Top - r.Y0) * upp, yBottom = (g.Top - r.Y1) * upp;
                    // Clockwise with y up: bottom-left, top-left, top-right, bottom-right.
                    pts.Add(new[] { x0, yBottom });
                    pts.Add(new[] { x0, yTop });
                    pts.Add(new[] { x1, yTop });
                    pts.Add(new[] { x1, yBottom });
                    xMin = Math.Min(xMin, x0); xMax = Math.Max(xMax, x1);
                    yMin = Math.Min(yMin, yBottom); yMax = Math.Max(yMax, yTop);
                }
                lsbs.Add(xMin);
                if (!any)
                {
                    xMinAll = xMin; yMinAll = yMin; xMaxAll = xMax; yMaxAll = yMax; any = true;
                }
                else
                {
                    xMinAll = Math.Min(xMinAll, xMin); yMinAll = Math.Min(yMinAll, yMin);
                    xMaxAll = Math.Max(xMaxAll, xMax); yMaxAll = Math.Max(yMaxAll, yMax);
                }
                maxPoints = Math.Max(maxPoints, pts.Count);
                maxContours = Math.Max(maxContours, rects.Count);

                var w = new BigEndian(glyf);
                w.I16(rects.Count);
                w.I16(xMin); w.I16(yMin); w.I16(xMax); w.I16(yMax);
                for (int i = 0; i < rects.Count; i++)
                {
                    w.U16(i * 4 + 3);
                }
                w.U16(0); // no instructions
                for (int i = 0; i < pts.Count; i++)
                {
                    glyf.WriteByte(0x01); // on curve, 16-bit deltas
                }
                int px = 0;
                foreach (int[] p in pts)
                {
                    w.I16(p[0] - px);
                    px = p[0];
                }
                int py = 0;
                foreach (int[] p in pts)
                {
                    w.I16(p[1] - py);
                    py = p[1];
                }
                while (glyf.Length % 4 != 0)
                {
                    glyf.WriteByte(0);
                }
                loca.Add((uint)glyf.Length);
            }

            int ascent = ascentPx * upp;
            int descent = Math.Abs(descentPx) * upp;
            var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

            // head
            {
                var s = new MemoryStream(); var w = new BigEndian(s);
                w.U32(0x00010000); w.U32(0x00010000); w.U32(0); w.U32(0x5F0F3CF5);
                w.U16(0x000B); w.U16(unitsPerEm);
                w.U32(0); w.U32(0); w.U32(0); w.U32(0); // created, modified
                w.I16(xMinAll); w.I16(yMinAll); w.I16(xMaxAll); w.I16(yMaxAll);
                w.U16(0); w.U16(8); w.I16(2); w.I16(1); w.I16(0);
                tables["head"] = s.ToArray();
            }
            // hhea
            {
                var s = new MemoryStream(); var w = new BigEndian(s);
                w.U32(0x00010000); w.I16(ascent); w.I16(-descent); w.I16(0);
                w.U16(advMax); w.I16(Math.Min(0, xMinAll)); w.I16(0); w.I16(xMaxAll);
                w.I16(1); w.I16(0); w.I16(0);
                w.I16(0); w.I16(0); w.I16(0); w.I16(0);
                w.I16(0); w.U16(numGlyphs);
                tables["hhea"] = s.ToArray();
            }
            // maxp
            {
                var s = new MemoryStream(); var w = new BigEndian(s);
                w.U32(0x00010000); w.U16(numGlyphs); w.U16(maxPoints); w.U16(maxContours);
                w.U16(0); w.U16(0); w.U16(2); w.U16(0); w.U16(0); w.U16(0); w.U16(0); w.U16(0); w.U16(0); w.U16(0); w.U16(0);
                tables["maxp"] = s.ToArray();
            }
            // hmtx
            {
                var s = new MemoryStream(); var w = new BigEndian(s);
                for (int i = 0; i < numGlyphs; i++)
                {
                    w.U16(advances[i]); w.I16(lsbs[i]);
                }
                tables["hmtx"] = s.ToArray();
            }
            // loca (long)
            {
                var s = new MemoryStream(); var w = new BigEndian(s);
                foreach (uint o in loca)
                {
                    w.U32(o);
                }
                tables["loca"] = s.ToArray();
            }
            tables["glyf"] = glyf.ToArray();
            if (tables["glyf"].Length == 0)
            {
                tables["glyf"] = new byte[4];
            }
            tables["cmap"] = Cmap(sorted);
            // OS/2 version 4
            {
                var s = new MemoryStream(); var w = new BigEndian(s);
                int avg = sorted.Count > 0 ? advances[1] : unitsPerEm / 2;
                w.U16(4); w.I16(avg); w.U16(400); w.U16(5); w.U16(0);
                for (int i = 0; i < 10; i++)
                {
                    w.I16(unitsPerEm / 2);
                }
                w.I16(0); // sFamilyClass
                s.Write(new byte[10], 0, 10); // panose
                w.U32(1); w.U32(0); w.U32(0); w.U32(0);
                s.Write(Encoding.ASCII.GetBytes("BLAY"), 0, 4);
                w.U16(0x40);
                w.U16(sorted.Count > 0 ? sorted[0].Codepoint : 32);
                w.U16(sorted.Count > 0 ? sorted[sorted.Count - 1].Codepoint : 32);
                w.I16(ascent); w.I16(-descent); w.I16(0);
                w.U16(ascent); w.U16(descent);
                w.U32(1); w.U32(0);
                w.I16(ascent / 2); w.I16(ascent); w.U16(0); w.U16(32); w.U16(1);
                tables["OS/2"] = s.ToArray();
            }
            tables["name"] = Name(family);
            // post version 3
            {
                var s = new MemoryStream(); var w = new BigEndian(s);
                w.U32(0x00030000); w.U32(0); w.I16(-upp); w.I16(upp); w.U32(1); w.U32(0); w.U32(0); w.U32(0); w.U32(0);
                tables["post"] = s.ToArray();
            }

            return Assemble(tables);
        }

        /// <summary>Covers the filled pixels with rectangles: runs in each row, merged downwards when identical.</summary>
        private static List<Rect> Rectangles(bool[,] pixels)
        {
            var result = new List<Rect>();
            if (pixels == null)
            {
                return result;
            }
            int w = pixels.GetLength(0), h = pixels.GetLength(1);
            var open = new Dictionary<long, int>(); // (x0, x1) -> index into result of the rect ending at the previous row
            for (int y = 0; y < h; y++)
            {
                var next = new Dictionary<long, int>();
                int x = 0;
                while (x < w)
                {
                    if (!pixels[x, y])
                    {
                        x++;
                        continue;
                    }
                    int start = x;
                    while (x < w && pixels[x, y])
                    {
                        x++;
                    }
                    long key = ((long)start << 32) | (uint)x;
                    if (open.TryGetValue(key, out int index))
                    {
                        Rect r = result[index];
                        r.Y1 = y + 1;
                        result[index] = r;
                        next[key] = index;
                    }
                    else
                    {
                        result.Add(new Rect { X0 = start, X1 = x, Y0 = y, Y1 = y + 1 });
                        next[key] = result.Count - 1;
                    }
                }
                open = next;
            }
            return result;
        }

        private static byte[] Cmap(List<Glyph> sorted)
        {
            // Format 4, one segment per run of consecutive codepoints with consecutive glyph ids.
            var starts = new List<int>();
            var ends = new List<int>();
            var deltas = new List<int>();
            for (int i = 0; i < sorted.Count; i++)
            {
                int code = sorted[i].Codepoint;
                int gid = i + 1;
                if (starts.Count > 0 && ends[ends.Count - 1] == code - 1 && (deltas[deltas.Count - 1] + code) % 65536 == gid)
                {
                    ends[ends.Count - 1] = code;
                    continue;
                }
                starts.Add(code);
                ends.Add(code);
                deltas.Add((gid - code + 65536) % 65536);
            }
            starts.Add(0xFFFF); ends.Add(0xFFFF); deltas.Add(1);
            int segCount = starts.Count;
            int searchRange = 2, entrySelector = 0;
            while (searchRange * 2 <= segCount * 2)
            {
                searchRange *= 2;
                entrySelector++;
            }
            var sub = new MemoryStream(); var w = new BigEndian(sub);
            int length = 16 + segCount * 8;
            w.U16(4); w.U16(length); w.U16(0);
            w.U16(segCount * 2); w.U16(searchRange); w.U16(entrySelector); w.U16(segCount * 2 - searchRange);
            foreach (int e in ends) w.U16(e);
            w.U16(0);
            foreach (int st in starts) w.U16(st);
            foreach (int d in deltas) w.U16(d);
            for (int i = 0; i < segCount; i++) w.U16(0);
            byte[] subtable = sub.ToArray();

            var s = new MemoryStream(); var h = new BigEndian(s);
            h.U16(0); h.U16(2);
            h.U16(0); h.U16(3); h.U32(4 + 8 * 2);
            h.U16(3); h.U16(1); h.U32(4 + 8 * 2);
            s.Write(subtable, 0, subtable.Length);
            return s.ToArray();
        }

        private static byte[] Name(string family)
        {
            var records = new List<KeyValuePair<int, string>>
            {
                new KeyValuePair<int, string>(1, family),
                new KeyValuePair<int, string>(2, "Regular"),
                new KeyValuePair<int, string>(3, family + " Regular"),
                new KeyValuePair<int, string>(4, family),
                new KeyValuePair<int, string>(6, family.Replace(" ", "")),
            };
            var strings = new MemoryStream();
            var s = new MemoryStream(); var w = new BigEndian(s);
            w.U16(0); w.U16(records.Count); w.U16(6 + 12 * records.Count);
            foreach (var r in records)
            {
                byte[] text = Encoding.BigEndianUnicode.GetBytes(r.Value);
                w.U16(3); w.U16(1); w.U16(0x409); w.U16(r.Key); w.U16(text.Length); w.U16((int)strings.Length);
                strings.Write(text, 0, text.Length);
            }
            strings.WriteTo(s);
            return s.ToArray();
        }

        private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
        {
            int count = tables.Count;
            int searchRange = 1, entrySelector = 0;
            while (searchRange * 2 <= count)
            {
                searchRange *= 2;
                entrySelector++;
            }
            searchRange *= 16;
            var s = new MemoryStream(); var w = new BigEndian(s);
            w.U32(0x00010000); w.U16(count); w.U16(searchRange); w.U16(entrySelector); w.U16(count * 16 - searchRange);
            int offset = 12 + 16 * count;
            int headOffset = 0;
            foreach (var t in tables)
            {
                s.Write(Encoding.ASCII.GetBytes(t.Key), 0, 4);
                w.U32(Checksum(t.Value)); w.U32((uint)offset); w.U32((uint)t.Value.Length);
                if (t.Key == "head")
                {
                    headOffset = offset;
                }
                offset += (t.Value.Length + 3) & ~3;
            }
            foreach (var t in tables)
            {
                s.Write(t.Value, 0, t.Value.Length);
                while (s.Length % 4 != 0)
                {
                    s.WriteByte(0);
                }
            }
            byte[] font = s.ToArray();
            uint adjust = 0xB1B0AFBA - Checksum(font);
            font[headOffset + 8] = (byte)(adjust >> 24);
            font[headOffset + 9] = (byte)(adjust >> 16);
            font[headOffset + 10] = (byte)(adjust >> 8);
            font[headOffset + 11] = (byte)adjust;
            return font;
        }

        private static uint Checksum(byte[] data)
        {
            uint sum = 0;
            for (int i = 0; i < data.Length; i += 4)
            {
                uint v = 0;
                for (int j = 0; j < 4; j++)
                {
                    v = (v << 8) | (i + j < data.Length ? data[i + j] : (byte)0);
                }
                sum = unchecked(sum + v);
            }
            return sum;
        }

        private class BigEndian
        {
            private readonly Stream s;
            public BigEndian(Stream s) { this.s = s; }
            public void U16(int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
            public void I16(int v) { U16(v & 0xFFFF); }
            public void U32(uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
            public void U32(int v) { U32((uint)v); }
        }
    }
}
