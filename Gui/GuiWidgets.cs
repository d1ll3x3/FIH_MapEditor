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
    }
}
