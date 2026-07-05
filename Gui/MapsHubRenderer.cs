using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    // Maps Hub (F7): modal window listing all saved maps — load / delete / new map.
    public class MapsHubRenderer
    {
        private readonly EditorController _c;
        private readonly GuiWindowHelper _win;
        private readonly GUI.WindowFunction _windowDelegate;

        private const float W = 520f;
        private const float H = 480f;
        private const int WINDOW_ID = 51602;
        private const int ROWS = 10;

        public bool Visible { get; private set; }
        public bool HasFocusedTextField => _win.HasFocusedTextField;
        public void UnfocusFields() => _win.Unfocus();

        public bool ContainsMouse()
            => Visible && _win.WindowRect.Contains(GuiWindowHelper.MouseGuiPosition());

        private List<MapFileInfo> _maps = new List<MapFileInfo>();
        private int _top = 0;
        private string _deleteConfirm = null;
        private float _deleteConfirmUntil = 0f;

        private GUIStyle _styleRow, _styleSmall;
        private bool _stylesReady;

        public MapsHubRenderer(EditorController controller)
        {
            _c = controller;
            _win = new GuiWindowHelper(new Rect(Screen.width / 2f - W / 2f, Screen.height / 2f - H / 2f, W, H))
            {
                InputBlocked = () => !_c.CursorFree,
            };
            _windowDelegate = new Action<int>(WindowFunction);
        }

        public void Toggle()
        {
            if (Visible) Close();
            else Open();
        }

        public void Open()
        {
            Visible = true;
            Refresh();
        }

        public void Close()
        {
            Visible = false;
            _deleteConfirm = null;
        }

        public void Refresh()
        {
            _maps = MapSerializer.ListMaps();
            _top = 0;
        }

        private void InitStyles()
        {
            if (_stylesReady) return;
            _styleRow = new GUIStyle { fontSize = 14, alignment = TextAnchor.MiddleLeft };
            _styleRow.normal.textColor = Color.white;
            _styleSmall = new GUIStyle { fontSize = 13 };
            _styleSmall.normal.textColor = new Color(0.92f, 0.92f, 0.92f);
            _stylesReady = true;
        }

        public void Draw()
        {
            _win.Visible = Visible;
            if (!Visible) return;
            InitStyles();
            _win.BeginFrame();

            GUI.backgroundColor = new Color(0.1f, 0.12f, 0.18f, 0.98f);
            _win.WindowRect = GUI.Window(WINDOW_ID, _win.WindowRect, _windowDelegate, "MAPS HUB");
            GUI.backgroundColor = Color.white;
        }

        private void WindowFunction(int id)
        {
            // Same fix as the main menu: paint the full body ourselves.
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.09f, 0.11f, 0.16f, 1f);
            GUI.Box(new Rect(0, 20, W, H - 20), "");
            GUI.Box(new Rect(0, 20, W, H - 20), "");
            GUI.backgroundColor = prevBg;

            GUI.skin.box.fontSize = 14;
            GUI.skin.box.normal.textColor = Color.white;

            GUI.backgroundColor = Color.red;
            if (_win.Button(new Rect(W - 40, 4, 32, 22), "X")) Close();
            GUI.backgroundColor = Color.white;

            float y = 32;
            if (_win.Button(new Rect(15, y, 120, 26), "New map"))
            {
                _c.NewMap();
                Close();
            }
            if (_win.Button(new Rect(140, y, 100, 26), "Refresh")) Refresh();
            GUI.Label(new Rect(255, y + 5, 250, 20), $"{_maps.Count} maps in the Maps folder", _styleSmall);
            y += 34;

            var listRect = new Rect(10, y, W - 20, ROWS * 40 + 4);
            GUI.Box(listRect, "");

            float scroll = _win.ScrollDeltaOver(listRect);
            if (scroll != 0)
                _top = Mathf.Clamp(_top + (scroll < 0 ? 2 : -2), 0, Mathf.Max(0, _maps.Count - ROWS));

            if (_maps.Count == 0)
                GUI.Label(new Rect(25, y + 12, 400, 22), "No saved maps yet", _styleRow);

            float ry = y + 4;
            for (int i = _top; i < _maps.Count && i < _top + ROWS; i++)
            {
                var m = _maps[i];
                string title = m.IsAutosave ? $"⟲ AUTOSAVE — {m.MapName}" : m.MapName;
                string sub = m.ObjectCount >= 0
                    ? $"{m.ObjectCount} objects — {m.LastWrite:dd/MM/yyyy HH:mm}"
                    : $"corrupted file — {m.LastWrite:dd/MM/yyyy HH:mm}";

                if (m.IsAutosave) GUI.color = new Color(1f, 0.85f, 0.5f);
                GUI.Label(new Rect(20, ry, 300, 20), Truncate(title, 38), _styleRow);
                GUI.color = Color.white;
                GUI.Label(new Rect(20, ry + 18, 300, 18), sub, _styleSmall);

                bool loadable = m.ObjectCount >= 0;
                if (loadable && _win.Button(new Rect(330, ry + 6, 80, 26), m.IsAutosave ? "RECOVER" : "LOAD"))
                {
                    _c.LoadMap(m.FileName);
                    Close();
                }

                bool confirming = _deleteConfirm == m.FileName && Time.unscaledTime < _deleteConfirmUntil;
                GUI.backgroundColor = confirming ? Color.red : new Color(0.7f, 0.35f, 0.3f);
                if (_win.Button(new Rect(420, ry + 6, 80, 26), confirming ? "SURE?" : "DELETE"))
                {
                    if (confirming)
                    {
                        try
                        {
                            MapSerializer.Delete(m.FileName);
                            _c.ShowToast($"Deleted: {m.MapName}");
                        }
                        catch (Exception ex)
                        {
                            _c.ShowToast($"Error deleting: {ex.Message}");
                        }
                        _deleteConfirm = null;
                        Refresh();
                    }
                    else
                    {
                        _deleteConfirm = m.FileName;
                        _deleteConfirmUntil = Time.unscaledTime + 3f;
                    }
                }
                GUI.backgroundColor = Color.white;
                ry += 40;
            }

            GUI.Label(new Rect(15, H - 26, W - 30, 20),
                "Loading a map replaces the current objects (save first if there are changes ●)", _styleSmall);
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
