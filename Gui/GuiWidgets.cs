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
            GUI.Box(rect, text);
            return HitTest(rect);
        }

        // Button drawn highlighted when "on" (used for toggles and tab headers).
        public bool ToggleButton(Rect rect, string text, bool on)
        {
            var prev = GUI.backgroundColor;
            if (on) GUI.backgroundColor = new Color(0.2f, 0.75f, 0.35f, 1f);
            GUI.Box(rect, text);
            GUI.backgroundColor = prev;
            return HitTest(rect);
        }

        private bool HitTest(Rect rect)
        {
            if (InputBlocked() || _clickHandledThisFrame) return false;

            if (Input.GetMouseButtonDown(0))
            {
                Vector2 mouse = CompensatedMouse();
                Rect absRect = new Rect(WindowRect.x + rect.x, WindowRect.y + rect.y, rect.width, rect.height);

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
            GUI.color = active ? new Color(0.8f, 1f, 0.8f) : Color.white;
            GUI.Box(r, active ? value + "_" : value);
            GUI.color = Color.white;

            if (InputBlocked()) return value;

            var e = Event.current;
            if (Input.GetMouseButtonDown(0) && e.type == EventType.Repaint)
            {
                Rect abs = new Rect(WindowRect.x + r.x, WindowRect.y + r.y, r.width, r.height);
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

            Rect abs = new Rect(WindowRect.x + localRect.x, WindowRect.y + localRect.y, localRect.width, localRect.height);
            return abs.Contains(CompensatedMouse()) ? scroll : 0f;
        }

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
                var prevEmpty = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.05f, 0.05f, 0.07f, 1f);
                GUI.Box(r, "");
                GUI.backgroundColor = prevEmpty;
                if (_activeScrollbar == id) _activeScrollbar = null;
                return 0;
            }

            top = Mathf.Clamp(top, 0, maxTop);
            // Thumb height mirrors the visible fraction, floored so it stays grabbable
            // on a catalog of ~900 entries (6/900 of the track would be sub-pixel).
            float thumbHeight = Mathf.Max(24f, r.height * pageSize / total);
            float travel = r.height - thumbHeight;
            float thumbY = r.y + (maxTop > 0 ? (float)top / maxTop * travel : 0f);

            // The panel behind us is already a GUI.Box, so an ambient-coloured track is
            // invisible against it: sink the track and light up the thumb. Everything
            // readable in this UI is opaque over the near-black window (0.13,0.13,0.16).
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.05f, 0.05f, 0.07f, 1f);
            GUI.Box(r, "");
            var thumb = new Rect(r.x, thumbY, r.width, thumbHeight);
            GUI.backgroundColor = _activeScrollbar == id
                ? new Color(0.2f, 0.75f, 0.35f, 1f)   // same green the toggles use while held
                : new Color(0.62f, 0.66f, 0.76f, 1f);
            GUI.Box(thumb, "");
            GUI.backgroundColor = prev;

            if (InputBlocked()) { _activeScrollbar = null; return top; }
            if (Event.current.type != EventType.Repaint) return top;

            Vector2 m = CompensatedMouse();
            var absTrack = new Rect(WindowRect.x + r.x, WindowRect.y + r.y, r.width, r.height);
            var absThumb = new Rect(WindowRect.x + thumb.x, WindowRect.y + thumb.y, thumb.width, thumb.height);

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
