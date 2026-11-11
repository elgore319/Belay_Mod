using UnityEngine;

namespace Belay.UI
{
    /// <summary>
    /// The look of the Belay menu, matched to White Knuckle's own menus: the game's pixel font, black panels with light
    /// grey pixel frames, white text, and red when something is pointed at or switched on. It's a separate GUISkin that
    /// only Belay's OnGUI uses, so other mods' IMGUI windows keep their own look.
    /// </summary>
    internal static class BelayTheme
    {
        // Colors taken from the game's menu canvases (text, frames and button highlight tints).
        public static readonly Color Text = Hex(0xE6E6E6);
        public static readonly Color Dim = Hex(0x8E8E8E);
        public static readonly Color Red = Hex(0xFF1C1C);
        public static readonly Color Frame = Hex(0xC1C4C2);
        public static readonly Color Line = Hex(0x5B5B5B);
        public static readonly Color Panel = Hex(0x141414);
        public static readonly Color Black = Hex(0x000000);
        public static readonly Color Background = new Color(0.04f, 0.04f, 0.04f, 0.97f);

        public const int BodySize = 17;
        public const int TitleSize = 24;
        public const int TabSize = 16;

        private static GUISkin skin;
        private static Font skinFont;
        private static bool failed;

        /// <summary>The tab bar style (the selected tab is shown inverted, like the game's menus).</summary>
        public static GUIStyle Tab { get; private set; }

        /// <summary>A small grey label for hints.</summary>
        public static GUIStyle Hint { get; private set; }

        /// <summary>The Belay skin, rebuilt once the game font becomes available. Null if it couldn't be built.</summary>
        public static GUISkin Skin
        {
            get
            {
                Font font = GameFont.Font;
                if (!failed && (skin == null || (font != null && skinFont != font)))
                {
                    try
                    {
                        Build(font);
                    }
                    catch (System.Exception e)
                    {
                        Plugin.Log.LogWarning("[Theme] Couldn't build the Belay menu skin: " + e.Message);
                        skin = null;
                        failed = true;
                    }
                }
                return skin;
            }
        }

        private static void Build(Font font)
        {
            // Called inside OnGUI, so GUI.skin is Unity's default skin here: start from a copy of it.
            GUISkin s = Object.Instantiate(GUI.skin);
            s.name = "Belay";
            s.hideFlags = HideFlags.HideAndDontSave;
            if (font != null)
            {
                s.font = font;
            }
            s.settings.cursorColor = Red;
            s.settings.selectionColor = new Color(Red.r, Red.g, Red.b, 0.45f);
            s.settings.doubleClickSelectsWord = true;

            // Window: light grey pixel frame, black title bar with a red marker, near-black body.
            s.window = Style(font, TitleSize, Text);
            s.window.normal.background = WindowTexture();
            s.window.onNormal.background = s.window.normal.background;
            s.window.border = new RectOffset(10, 10, 44, 10);
            s.window.padding = new RectOffset(14, 14, 54, 14);
            s.window.contentOffset = new Vector2(4f, -46f);
            s.window.alignment = TextAnchor.UpperLeft;
            s.window.wordWrap = false;

            s.label = Style(font, BodySize, Text);
            s.label.wordWrap = true;
            s.label.padding = new RectOffset(2, 2, 3, 3);

            s.box = Style(font, BodySize, Text);
            s.box.normal.background = Framed(Panel, Hex(0x3A3A3A), 1);
            s.box.border = new RectOffset(2, 2, 2, 2);
            s.box.padding = new RectOffset(8, 8, 6, 6);
            s.box.margin = new RectOffset(2, 2, 4, 4);

            s.button = Button(font, BodySize);

            Tab = Button(font, TabSize);
            Tab.name = "BelayTab";
            Tab.normal.background = Framed(Black, Line, 1);
            Tab.margin = new RectOffset(1, 1, 2, 2);
            Tab.padding = new RectOffset(4, 4, 5, 5);
            Tab.clipping = TextClipping.Clip;

            s.toggle = Style(font, BodySize, Text);
            s.toggle.wordWrap = false;
            s.toggle.padding = new RectOffset(28, 4, 3, 3);
            s.toggle.margin = new RectOffset(2, 2, 3, 3);
            s.toggle.alignment = TextAnchor.MiddleLeft;
            s.toggle.border = new RectOffset(23, 0, 13, 13);
            s.toggle.normal.background = Checkbox(Dim, false);
            s.toggle.hover.background = Checkbox(Red, false);
            s.toggle.active.background = Checkbox(Red, true);
            s.toggle.onNormal.background = Checkbox(Text, true);
            s.toggle.onHover.background = Checkbox(Red, true);
            s.toggle.onActive.background = Checkbox(Red, false);
            s.toggle.hover.textColor = Red;
            s.toggle.onNormal.textColor = Text;
            s.toggle.onHover.textColor = Red;
            s.toggle.active.textColor = Red;
            s.toggle.onActive.textColor = Red;

            s.textField = Style(font, BodySize, Text);
            s.textField.normal.background = Framed(Hex(0x050505), Line, 1);
            s.textField.hover.background = Framed(Hex(0x050505), Dim, 1);
            s.textField.focused.background = Framed(Hex(0x050505), Red, 1);
            s.textField.onNormal.background = s.textField.normal.background;
            s.textField.onFocused.background = s.textField.focused.background;
            s.textField.border = new RectOffset(2, 2, 2, 2);
            s.textField.padding = new RectOffset(7, 7, 5, 5);
            s.textField.margin = new RectOffset(2, 2, 3, 3);
            s.textField.wordWrap = false;
            s.textField.clipping = TextClipping.Clip;
            s.textField.focused.textColor = Color.white;
            s.textArea = new GUIStyle(s.textField) { name = "textarea", wordWrap = true };

            // Slider: a thin grey rail with a white block, red while held or pointed at.
            s.horizontalSlider = new GUIStyle { name = "horizontalslider" };
            s.horizontalSlider.normal.background = Rail(Line);
            s.horizontalSlider.border = new RectOffset(2, 2, 0, 0);
            s.horizontalSlider.fixedHeight = 18f;
            s.horizontalSlider.margin = new RectOffset(6, 6, 4, 8);
            s.horizontalSliderThumb = new GUIStyle { name = "horizontalsliderthumb" };
            s.horizontalSliderThumb.normal.background = Solid(Text);
            s.horizontalSliderThumb.hover.background = Solid(Red);
            s.horizontalSliderThumb.active.background = Solid(Red);
            s.horizontalSliderThumb.fixedWidth = 10f;
            s.horizontalSliderThumb.fixedHeight = 18f;

            // Scrollbars: a dark slot with a grey bar.
            s.verticalScrollbar = new GUIStyle { name = "verticalscrollbar" };
            s.verticalScrollbar.normal.background = Solid(Hex(0x101010));
            s.verticalScrollbar.fixedWidth = 10f;
            s.verticalScrollbar.margin = new RectOffset(4, 0, 0, 0);
            s.verticalScrollbarThumb = new GUIStyle { name = "verticalscrollbarthumb" };
            s.verticalScrollbarThumb.normal.background = Solid(Line);
            s.verticalScrollbarThumb.hover.background = Solid(Dim);
            s.verticalScrollbarThumb.active.background = Solid(Red);
            s.verticalScrollbarThumb.fixedWidth = 10f;
            s.verticalScrollbarUpButton = new GUIStyle { name = "verticalscrollbarupbutton", fixedWidth = 10f, fixedHeight = 0f };
            s.verticalScrollbarDownButton = new GUIStyle { name = "verticalscrollbardownbutton", fixedWidth = 10f, fixedHeight = 0f };
            s.horizontalScrollbar = new GUIStyle { name = "horizontalscrollbar" };
            s.horizontalScrollbar.normal.background = Solid(Hex(0x101010));
            s.horizontalScrollbar.fixedHeight = 10f;
            s.horizontalScrollbar.margin = new RectOffset(0, 0, 4, 0);
            s.horizontalScrollbarThumb = new GUIStyle { name = "horizontalscrollbarthumb" };
            s.horizontalScrollbarThumb.normal.background = Solid(Line);
            s.horizontalScrollbarThumb.hover.background = Solid(Dim);
            s.horizontalScrollbarThumb.active.background = Solid(Red);
            s.horizontalScrollbarThumb.fixedHeight = 10f;
            s.horizontalScrollbarLeftButton = new GUIStyle { name = "horizontalscrollbarleftbutton", fixedWidth = 0f, fixedHeight = 10f };
            s.horizontalScrollbarRightButton = new GUIStyle { name = "horizontalscrollbarrightbutton", fixedWidth = 0f, fixedHeight = 10f };
            s.scrollView = new GUIStyle { name = "scrollview" };

            Hint = new GUIStyle(s.label) { name = "BelayHint", fontSize = BodySize - 2 };
            Hint.normal.textColor = Dim;

            // GUILayout.Toolbar looks for buttonleft/mid/right; point them all at the button so the default ones aren't used.
            s.customStyles = new[]
            {
                new GUIStyle(s.button) { name = "buttonleft" },
                new GUIStyle(s.button) { name = "buttonmid" },
                new GUIStyle(s.button) { name = "buttonright" },
                new GUIStyle(Tab) { name = "BelayTableft" },
                new GUIStyle(Tab) { name = "BelayTabmid" },
                new GUIStyle(Tab) { name = "BelayTabright" },
                Tab,
                Hint,
            };

            skin = s;
            skinFont = font;
        }

        private static GUIStyle Style(Font font, int size, Color text)
        {
            var style = new GUIStyle { font = font, fontSize = size, richText = false };
            style.normal.textColor = text;
            style.hover.textColor = text;
            style.active.textColor = text;
            style.focused.textColor = text;
            style.onNormal.textColor = text;
            style.onHover.textColor = text;
            style.onActive.textColor = text;
            style.onFocused.textColor = text;
            return style;
        }

        /// <summary>Black with a grey frame; red frame and text when pointed at, red fill when pressed, inverted when on.</summary>
        private static GUIStyle Button(Font font, int size)
        {
            GUIStyle b = Style(font, size, Text);
            b.name = "button";
            b.alignment = TextAnchor.MiddleCenter;
            b.wordWrap = false;
            b.border = new RectOffset(2, 2, 2, 2);
            b.padding = new RectOffset(10, 10, 5, 5);
            b.margin = new RectOffset(3, 3, 3, 3);
            b.normal.background = Framed(Panel, Line, 1);
            b.hover.background = Framed(Panel, Red, 1);
            b.hover.textColor = Red;
            b.active.background = Framed(Red, Red, 1);
            b.active.textColor = Black;
            b.focused.background = b.normal.background;
            b.onNormal.background = Framed(Text, Text, 1);
            b.onNormal.textColor = Black;
            b.onHover.background = Framed(Text, Red, 1);
            b.onHover.textColor = Red;
            b.onActive.background = b.active.background;
            b.onActive.textColor = Black;
            b.onFocused.background = b.onNormal.background;
            b.onFocused.textColor = Black;
            return b;
        }

        // ---------------------------------------------------------------- Pixel textures

        private static Texture2D NewTexture(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            return t;
        }

        private static Texture2D Solid(Color c)
        {
            Texture2D t = NewTexture(2, 2);
            t.SetPixels(new[] { c, c, c, c });
            t.Apply();
            return t;
        }

        /// <summary>A fill with a pixel frame of the given width, for 9-slicing (border = width + 1).</summary>
        private static Texture2D Framed(Color fill, Color frame, int width)
        {
            const int size = 8;
            Texture2D t = NewTexture(size, size);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool edge = x < width || y < width || x >= size - width || y >= size - width;
                    px[y * size + x] = edge ? frame : fill;
                }
            }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        /// <summary>The window: 2 px light frame, a black 40 px title bar with a red marker, a dark body.</summary>
        private static Texture2D WindowTexture()
        {
            const int w = 24, h = 64, frame = 2, bar = 40;
            Texture2D t = NewTexture(w, h);
            var px = new Color[w * h];
            for (int row = 0; row < h; row++)
            {
                int y = h - 1 - row; // texture rows go up; row counts down from the top
                for (int x = 0; x < w; x++)
                {
                    Color c;
                    if (x < frame || x >= w - frame || row < frame || row >= h - frame)
                    {
                        c = Frame;
                    }
                    else if (row < bar)
                    {
                        c = x >= 4 && x < 8 && row >= 8 && row < bar - 6 ? Red : Black;
                    }
                    else if (row < bar + frame)
                    {
                        c = Frame;
                    }
                    else
                    {
                        c = Background;
                    }
                    px[y * w + x] = c;
                }
            }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        /// <summary>A checkbox at the left of a 24x26 cell; the rest is transparent and stretches behind the text.</summary>
        private static Texture2D Checkbox(Color frame, bool on)
        {
            const int w = 24, h = 26, box = 18, top = 4, left = 1;
            Texture2D t = NewTexture(w, h);
            var px = new Color[w * h];
            for (int row = 0; row < h; row++)
            {
                int y = h - 1 - row;
                for (int x = 0; x < w; x++)
                {
                    int bx = x - left, by = row - top;
                    Color c = Color.clear;
                    if (bx >= 0 && bx < box && by >= 0 && by < box)
                    {
                        bool edge = bx < 2 || by < 2 || bx >= box - 2 || by >= box - 2;
                        bool inner = bx >= 5 && bx < box - 5 && by >= 5 && by < box - 5;
                        c = edge ? frame : (on && inner ? (frame == Text ? Red : frame) : Hex(0x050505));
                    }
                    px[y * w + x] = c;
                }
            }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        /// <summary>A 2 px rail across the middle of an 18 px tall slider.</summary>
        private static Texture2D Rail(Color c)
        {
            const int w = 8, h = 18;
            Texture2D t = NewTexture(w, h);
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    px[y * w + x] = y == 8 || y == 9 ? c : Color.clear;
                }
            }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        private static Color Hex(int rgb)
        {
            return new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
        }
    }
}
