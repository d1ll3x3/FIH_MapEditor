using UnityEngine;

namespace FIHMapEditor
{
    // The look of every mod window: one palette, flat rounded panels and our own text
    // styles. GUI.skin is shared with the other mods in the game, so it is never changed;
    // everything here is drawn with private styles over runtime textures.
    public static class GuiTheme
    {
        public static readonly Color Background = new Color(0.075f, 0.082f, 0.105f, 0.98f);
        public static readonly Color Panel = new Color(0.115f, 0.125f, 0.16f, 1f);
        public static readonly Color PanelRaised = new Color(0.165f, 0.177f, 0.222f, 1f);
        public static readonly Color PanelHover = new Color(0.215f, 0.23f, 0.285f, 1f);
        public static readonly Color Inset = new Color(0.05f, 0.055f, 0.073f, 1f);
        public static readonly Color Line = new Color(1f, 1f, 1f, 0.08f);
        public static readonly Color Accent = new Color(0.27f, 0.6f, 1f, 1f);
        public static readonly Color AccentSoft = new Color(0.27f, 0.6f, 1f, 0.2f);
        public static readonly Color Text = new Color(0.93f, 0.94f, 0.97f, 1f);
        public static readonly Color TextDim = new Color(0.6f, 0.63f, 0.71f, 1f);
        public static readonly Color Success = new Color(0.2f, 0.68f, 0.42f, 1f);
        public static readonly Color Danger = new Color(0.82f, 0.3f, 0.3f, 1f);

        public const float TITLE_H = 24f;

        private static Texture2D _rounded;
        private static GUIStyle _panel, _windowStyle, _buttonText, _buttonTextBold, _fieldText, _windowTitle;

        // Empty window style: the frame (background + title bar) is drawn by WindowFrame.
        public static GUIStyle WindowStyle => _windowStyle ??= new GUIStyle();

        public static GUIStyle ButtonText => _buttonText ??= MakeText(13, TextAnchor.MiddleCenter, false);
        public static GUIStyle ButtonTextBold => _buttonTextBold ??= MakeText(13, TextAnchor.MiddleCenter, true);
        public static GUIStyle FieldText
        {
            get
            {
                if (_fieldText == null)
                {
                    _fieldText = MakeText(13, TextAnchor.MiddleLeft, false);
                    _fieldText.padding = Offsets(7, 7, 0, 0);
                    _fieldText.clipping = TextClipping.Clip;
                }
                return _fieldText;
            }
        }

        public static GUIStyle MakeText(int size, TextAnchor anchor, bool bold)
        {
            var style = new GUIStyle { fontSize = size, alignment = anchor, clipping = TextClipping.Clip };
            if (bold) style.fontStyle = FontStyle.Bold;
            style.normal.textColor = Text;
            return style;
        }

        // The interop has no RectOffset(left, right, top, bottom) constructor.
        private static RectOffset Offsets(int left, int right, int top, int bottom)
            => new RectOffset { left = left, right = right, top = top, bottom = bottom };

        // A filled rounded rectangle in the given colour.
        public static void Fill(Rect rect, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = color;
            GUI.Box(rect, GUIContent.none, PanelStyle);
            GUI.backgroundColor = prev;
        }

        // A rounded rectangle with a 1px outline (a slightly larger panel behind it).
        public static void Outlined(Rect rect, Color fill, Color outline)
        {
            Fill(rect, outline);
            Fill(new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2), fill);
        }

        // Window background plus title bar, drawn first inside a window function. The
        // bar is the drag strip GuiWindowHelper already uses (top TITLE_H pixels).
        public static void WindowFrame(float width, float height, string title)
        {
            Outlined(new Rect(0, 0, width, height), Background, new Color(1f, 1f, 1f, 0.1f));
            Fill(new Rect(1, 1, width - 2, TITLE_H), Panel);
            GuiWindowHelper.FillRect(new Rect(1, TITLE_H, width - 2, 1), Line);
            _windowTitle ??= MakeText(12, TextAnchor.MiddleLeft, true);
            _windowTitle.normal.textColor = TextDim;
            GUI.Label(new Rect(12, 0, width - 24, TITLE_H), title, _windowTitle);
        }

        private static GUIStyle PanelStyle
        {
            get
            {
                // Scene loads unload unreferenced assets; recreate if that happened anyway.
                if (_panel == null || _rounded == null)
                {
                    _rounded = MakeRoundedTexture(32, 6);
                    _panel = new GUIStyle { border = Offsets(8, 8, 8, 8) };
                    _panel.normal.background = _rounded;
                }
                return _panel;
            }
        }

        // White rounded square with anti-aliased corners, nine-sliced by PanelStyle.
        private static Texture2D MakeRoundedTexture(int size, float radius)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Distance from the nearest corner's centre, only inside the corners.
                    float cx = Mathf.Clamp(x + 0.5f, radius, size - radius);
                    float cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    float alpha = Mathf.Clamp01(radius - d + 0.5f);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }
    }
}
