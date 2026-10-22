using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    // IMGUI interactive controls (GUI.Button/Toggle/TextField) don't fire events in this
    // IL2CPP build, and GUI.DragWindow is equally broken. Every widget here is a GUI.Box
    // visual + a manual hit-test against legacy Input, the pattern proven in the trainer
    // and TAS tool. One GuiWindowHelper per window keeps the rect, drag state, the
    // click-consumed-this-frame flag and the focused text field.
    public class GuiWindowHelper
    {
        public Rect WindowRect;
        public bool Visible;

        // Input from a window is blocked while a higher-priority (modal) window is open.
        public System.Func<bool> InputBlocked = () => false;

        private bool _dragging = false;
        private Vector2 _dragOffset;
        private bool _clickHandledThisFrame = false;
        private string _activeField = null;
        private readonly HashSet<string> _fieldsDrawn = new HashSet<string>();
        // Scrollbar being dragged, by id (several can share one window), plus where
        // inside the thumb the grab started so it doesn't jump under the cursor.
        private string _activeScrollbar = null;
        private float _scrollGrabOffset;

        public bool HasFocusedTextField => _activeField != null;

        // Where the rects passed to the widgets start inside the window: set it while
        // drawing inside a GUI.BeginGroup at that offset (the editor's content panel sits
        // right of its sidebar) so the manual hit tests line up with what is drawn.
        public Vector2 ContentOffset;

        private Rect ToScreen(Rect r)
            => new Rect(WindowRect.x + ContentOffset.x + r.x, WindowRect.y + ContentOffset.y + r.y, r.width, r.height);

        public GuiWindowHelper(Rect initialRect)
        {
            WindowRect = initialRect;
        }

        public static Vector2 MouseGuiPosition()
        {
            Vector2 m = Input.mousePosition;
            m.y = Screen.height - m.y;
            return m;
        }

        // The mouse, translated back into this window's UNSCALED logical space. Drawing
        // applies GUIUtility.ScaleAroundPivot(EditorConfig.UiScale, WindowRect.position)
        // around the window (see EditorMenuRenderer/MapsHubRenderer.Draw) — every hit
        // test here must compare against the INVERSE of that same transform, or clicks
        // drift off their visual target the moment the UI scale isn't 1.
        public Vector2 CompensatedMouse()
        {
            Vector2 raw = MouseGuiPosition();
            float scale = EditorConfig.UiScale;
            if (Mathf.Approximately(scale, 1f)) return raw;
            Vector2 pivot = WindowRect.position;
            return pivot + (raw - pivot) / scale;
        }

        // True when the (possibly scaled) mouse is over this window's footprint.
        public bool ContainsMouse() => WindowRect.Contains(CompensatedMouse());

        // Call once at the start of the window's Draw() (outside the window function).
        public void BeginFrame()
        {
            if (Event.current.type == EventType.Repaint)
            {
                _clickHandledThisFrame = false;

                // A field that stopped being drawn (tab switch, window closed) can never
                // unfocus itself, and a stale focus silently swallows hotkeys like the
                // cursor-toggle key. Drop focus when its TextField is no longer rendered.
                if (_activeField != null && !_fieldsDrawn.Contains(_activeField))
                    _activeField = null;
                _fieldsDrawn.Clear();
            }

            HandleDrag();
        }

        private void HandleDrag()
        {
            if (InputBlocked()) { _dragging = false; return; }
            if (Event.current.type != EventType.Repaint) return;

            Vector2 m = CompensatedMouse();

            if (Input.GetMouseButtonDown(0) && !_dragging)
            {
                // Title bar = top strip of the window, minus the X button corner
                var titleRect = new Rect(WindowRect.x, WindowRect.y, WindowRect.width - 44, 24);
                if (titleRect.Contains(m))
                {
                    _dragging = true;
                    _dragOffset = m - new Vector2(WindowRect.x, WindowRect.y);
                }
            }

            if (_dragging)
            {
                if (!Input.GetMouseButton(0))
                    _dragging = false;
                else
                {
                    WindowRect.x = Mathf.Clamp(m.x - _dragOffset.x, -WindowRect.width + 60, Screen.width - 60);
                    WindowRect.y = Mathf.Clamp(m.y - _dragOffset.y, 0, Screen.height - 40);
                }
            }
        }

        // Rect is in window-local coordinates.
        public bool Button(Rect rect, string text)
        {
            DrawButton(rect, text, false);
            return HitTest(rect);
        }

        // Button drawn in the accent colour when "on" (toggles, tabs, modes).
        public bool ToggleButton(Rect rect, string text, bool on)
        {
            DrawButton(rect, text, on);
            return HitTest(rect);
        }

        // Callers colour a button by setting GUI.backgroundColor first (green PLAY, red
        // delete...); white means the theme's own colour.
        private void DrawButton(Rect rect, string text, bool on)
        {
            Color tint = GUI.backgroundColor;
            bool tinted = tint.r < 0.99f || tint.g < 0.99f || tint.b < 0.99f;
            Color fill = on ? GuiTheme.Accent
                : tinted ? Color.Lerp(GuiTheme.PanelRaised, new Color(tint.r, tint.g, tint.b, 1f), 0.7f)
                : GuiTheme.PanelRaised;
            if (IsHovered(rect))
                fill = Input.GetMouseButton(0) ? Color.Lerp(fill, Color.black, 0.18f) : Color.Lerp(fill, Color.white, 0.09f);
            GuiTheme.Fill(rect, fill);
            GUI.Label(rect, text, on ? GuiTheme.ButtonTextBold : GuiTheme.ButtonText);
        }

        // Mouse over a window-local rect (and this window may take input).
        public bool IsHovered(Rect rect) => !InputBlocked() && ToScreen(rect).Contains(CompensatedMouse());

        // A click on an area drawn by the caller (sidebar rows, list rows).
        public bool Clicked(Rect rect) => HitTest(rect);

        // Solid colour rectangle. Unlike a tinted GUI.Box it shows up on this skin.
        public static void FillRect(Rect rect, Color color)
        {
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = prev;
        }

        private bool HitTest(Rect rect)
        {
            if (InputBlocked() || _clickHandledThisFrame) return false;

            if (Input.GetMouseButtonDown(0))
            {
                Vector2 mouse = CompensatedMouse();
                Rect absRect = ToScreen(rect);

                if (absRect.Contains(mouse))
                {
                    if (Event.current.type == EventType.Repaint)
                    {
                        _clickHandledThisFrame = true;
                        return true;
                    }
                }
            }
            return false;
        }

        // Minimal text field that works under IL2CPP. Click to focus, type to edit,
        // Enter/Escape to unfocus.
        public string TextField(Rect r, string id, string value)
        {
            _fieldsDrawn.Add(id);
            bool active = _activeField == id;
            GuiTheme.Outlined(r, GuiTheme.Inset, active ? GuiTheme.Accent : new Color(1f, 1f, 1f, 0.12f));
            GUI.Label(r, active ? value + "_" : value, GuiTheme.FieldText);

            if (InputBlocked()) return value;

            var e = Event.current;
            if (Input.GetMouseButtonDown(0) && e.type == EventType.Repaint)
            {
                Rect abs = ToScreen(r);
                Vector2 m = CompensatedMouse();
                if (abs.Contains(m) && !_clickHandledThisFrame)
                {
                    _activeField = id;
                    _clickHandledThisFrame = true;
                }
                else if (active && !abs.Contains(m))
                    _activeField = null;
            }

            if (active && e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Backspace)
                {
                    if (value.Length > 0) value = value.Substring(0, value.Length - 1);
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.Escape)
                {
                    _activeField = null;
                    e.Use();
                }
                else if (e.character != 0 && e.character >= 32)
                {
                    value += e.character;
                    e.Use();
                }
            }
            return value;
        }

        public void Unfocus() => _activeField = null;

        // Scroll wheel delta while the mouse is over the given window-local rect.
        // Legacy Input — IMGUI ScrollWheel events never fire in this game.
        public float ScrollDeltaOver(Rect localRect)
        {
            if (InputBlocked()) return 0f;
            float scroll = Input.mouseScrollDelta.y;
            if (scroll == 0f) return 0f;

            Rect abs = ToScreen(localRect);
            return abs.Contains(CompensatedMouse()) ? scroll : 0f;
        }

        // Horizontal slider in window-local coordinates, value printed on the track.
        // Press anywhere on it and drag; same Repaint-gated press/hold/release handling
        // as VScrollbar. Returns the (possibly changed) value snapped to `step`.
        public float HSlider(Rect r, string id, float value, float min, float max, float step, string format)
        {
            value = Mathf.Clamp(value, min, max);
            float fill = max > min ? (value - min) / (max - min) : 0f;

            GuiTheme.Fill(r, GuiTheme.Inset);
            var fillColor = _activeSlider == id ? GuiTheme.Success : GuiTheme.Accent;
            var inner = new Rect(r.x + 2f, r.y + 2f, r.width - 4f, r.height - 4f);
            if (fill > 0f) GuiTheme.Fill(new Rect(inner.x, inner.y, Mathf.Max(8f, inner.width * fill), inner.height), fillColor);
            // Handle at the current value, so the position reads even at 0 or a full bar.
            GuiTheme.Fill(new Rect(inner.x + inner.width * fill - 3f, r.y + 1f, 6f, r.height - 2f), Color.white);
            if (_sliderLabel == null)
            {
                // GUIStyle's copy constructor is not exposed by the interop; build it bare.
                _sliderLabel = new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _sliderLabel.normal.textColor = Color.white;
            }
            GUI.Label(r, value.ToString(format), _sliderLabel);

            if (InputBlocked()) { if (_activeSlider == id) _activeSlider = null; return value; }
            if (Event.current.type != EventType.Repaint) return value;

            Vector2 m = CompensatedMouse();
            var abs = ToScreen(r);
            if (Input.GetMouseButtonDown(0) && _activeSlider == null && !_clickHandledThisFrame && abs.Contains(m))
            {
                _clickHandledThisFrame = true;
                _activeSlider = id;
            }
            if (_activeSlider == id)
            {
                if (!Input.GetMouseButton(0)) _activeSlider = null;
                else
                {
                    value = min + Mathf.Clamp01((m.x - abs.x) / abs.width) * (max - min);
                    if (step > 0f) value = Mathf.Round(value / step) * step;
                }
            }
            return Mathf.Clamp(value, min, max);
        }

        // True while the given slider is being dragged; edits that are costly to apply
        // (a network respawn) wait for the release.
        public bool IsSliderHeld(string id) => _activeSlider == id;

        private string _activeSlider;
        private static GUIStyle _sliderLabel;

        // Draggable vertical scrollbar over an item list, in window-local coordinates.
        // Returns the (possibly changed) index of the first visible item. HitTest can't
        // serve here — it only reports the frame the button goes down — so this follows
        // HandleDrag: Repaint-gated, CompensatedMouse, explicit press/hold/release state.
        //
        // `step` keeps the result aligned to whole rows (pass the column count for a grid).
        public int VScrollbar(Rect r, string id, int top, int total, int pageSize, int step)
        {
            int maxTop = Mathf.Max(0, total - pageSize);
            if (maxTop == 0)
            {
                // Nothing to scroll: empty groove, no thumb, layout unchanged.
                GuiTheme.Fill(r, GuiTheme.Inset);
                if (_activeScrollbar == id) _activeScrollbar = null;
                return 0;
            }

            top = Mathf.Clamp(top, 0, maxTop);
            // Thumb height mirrors the visible fraction, floored so it stays grabbable
            // on a catalog of ~900 entries (6/900 of the track would be sub-pixel).
            float thumbHeight = Mathf.Max(24f, r.height * pageSize / total);
            float travel = r.height - thumbHeight;
            float thumbY = r.y + (maxTop > 0 ? (float)top / maxTop * travel : 0f);

            // Sunken track, lit thumb: readable over any panel.
            GuiTheme.Fill(r, GuiTheme.Inset);
            var thumb = new Rect(r.x, thumbY, r.width, thumbHeight);
            GuiTheme.Fill(new Rect(thumb.x + 2f, thumb.y + 2f, thumb.width - 4f, thumb.height - 4f),
                _activeScrollbar == id ? GuiTheme.Accent
                    : IsHovered(thumb) ? GuiTheme.PanelHover : new Color(0.32f, 0.35f, 0.43f, 1f));

            if (InputBlocked()) { _activeScrollbar = null; return top; }
            if (Event.current.type != EventType.Repaint) return top;

            Vector2 m = CompensatedMouse();
            var absTrack = ToScreen(r);
            var absThumb = ToScreen(thumb);

            if (Input.GetMouseButtonDown(0) && _activeScrollbar == null
                && !_clickHandledThisFrame && absTrack.Contains(m))
            {
                // Consume the click either way: the grid sits right next to the bar and
                // must not treat a scroll press as PLACE/STAMP.
                _clickHandledThisFrame = true;
                if (absThumb.Contains(m))
                {
                    _activeScrollbar = id;
                    _scrollGrabOffset = m.y - absThumb.y;
                }
                else
                {
                    // Bare track: page towards the click, like a standard scrollbar.
                    top += m.y < absThumb.y ? -pageSize : pageSize;
                }
            }

            if (_activeScrollbar == id)
            {
                if (!Input.GetMouseButton(0)) _activeScrollbar = null;
                else if (travel > 0f)
                {
                    float local = m.y - _scrollGrabOffset - absTrack.y;
                    top = Mathf.RoundToInt(Mathf.Clamp01(local / travel) * maxTop);
                }
            }

            if (step > 1) top = top / step * step;   // land on a row boundary, never mid-row
            return Mathf.Clamp(top, 0, maxTop);
        }
    }
}
