using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    // Maps Hub (F7): manage local map files AND browse the online community library.
    // A Local ⇄ Online toggle switches the list; the upload row publishes the current
    // working map to the shared backend (editable or play-only).
    public class MapsHubRenderer
    {
        private readonly EditorController _c;
        private readonly GuiWindowHelper _win;
        private readonly GUI.WindowFunction _windowDelegate;

        private const float W = 640f;
        private const float H = 540f;
        private const int WINDOW_ID = 51602;
        private const int ROWS = 8;

        public bool Visible { get; private set; }
        public bool HasFocusedTextField => _win.HasFocusedTextField;
        public void UnfocusFields() => _win.Unfocus();

        public bool ContainsMouse()
            => Visible && _win.ContainsMouse();

        private List<MapFileInfo> _maps = new List<MapFileInfo>();
        private int _top = 0;
        private string _deleteConfirm = null;
        private float _deleteConfirmUntil = 0f;

        // Online browsing state
        private bool _online = false;
        private bool _uploadEditable = true;
        private int _onlineTop = 0;
        private string _onlineDeleteConfirm = null;
        private float _onlineDeleteUntil = 0f;

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
            if (_online) _c.OnlineMaps.RefreshList(force: true);
        }

        public void Close()
        {
            Visible = false;
            _deleteConfirm = null;
            _onlineDeleteConfirm = null;
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

            var prevMatrix = GUI.matrix;
            float scale = EditorConfig.UiScale;
            if (!Mathf.Approximately(scale, 1f))
                GUIUtility.ScaleAroundPivot(Vector2.one * scale, _win.WindowRect.position);

            GUI.backgroundColor = new Color(0.1f, 0.12f, 0.18f, 0.98f);
            _win.WindowRect = GUI.Window(WINDOW_ID, _win.WindowRect, _windowDelegate, "MAPS HUB");
            GUI.backgroundColor = Color.white;

            GUI.matrix = prevMatrix;
        }

        private void WindowFunction(int id)
        {
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

            // Local / Online switch
            float y = 32;
            if (_win.ToggleButton(new Rect(15, y, 100, 26), "Local", !_online))
                _online = false;
            if (_win.ToggleButton(new Rect(118, y, 100, 26), "Online", _online))
            {
                if (!_online) _c.OnlineMaps.RefreshList(force: true);
                _online = true;
            }

            if (_online) DrawOnlineHeader(y);
            else DrawLocalHeader(y);
            y += 34;

            // Upload row (always visible): publish the current working map.
            DrawUploadRow(y);
            y += 34;

            if (_online) DrawOnlineList(y);
            else DrawLocalList(y);
        }

        // ─────────────────────────────────────────────────────────────── local ──

        private void DrawLocalHeader(float y)
        {
            if (_win.Button(new Rect(230, y, 110, 26), "New map"))
            {
                _c.NewMap();
                Close();
            }
            if (_win.Button(new Rect(345, y, 90, 26), "Refresh")) Refresh();
            GUI.Label(new Rect(445, y + 5, 190, 20), $"{_maps.Count} local map(s)", _styleSmall);
        }

        private void DrawLocalList(float y)
        {
            var listRect = new Rect(10, y, W - 20, ROWS * 44 + 4);
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
                string title = m.FileName == MapSerializer.AUTOSAVE_PREV_NAME
                    ? $"⟲ AUTOSAVE (older) — {m.MapName}"
                    : m.IsAutosave ? $"⟲ AUTOSAVE — {m.MapName}" : m.MapName;
                string sub = m.ObjectCount >= 0
                    ? $"{m.ObjectCount} objects — {m.LastWrite:dd/MM/yyyy HH:mm}"
                    : $"corrupted file — {m.LastWrite:dd/MM/yyyy HH:mm}";

                if (m.IsAutosave) GUI.color = new Color(1f, 0.85f, 0.5f);
                GUI.Label(new Rect(20, ry, 400, 20), Truncate(title, 44), _styleRow);
                GUI.color = Color.white;
                GUI.Label(new Rect(20, ry + 18, 400, 18), sub, _styleSmall);

                bool loadable = m.ObjectCount >= 0;
                if (loadable && _win.Button(new Rect(450, ry + 8, 80, 26), m.IsAutosave ? "RECOVER" : "LOAD"))
                {
                    _c.LoadMap(m.FileName);
                    Close();
                }

                bool confirming = _deleteConfirm == m.FileName && Time.unscaledTime < _deleteConfirmUntil;
                GUI.backgroundColor = confirming ? Color.red : new Color(0.7f, 0.35f, 0.3f);
                if (_win.Button(new Rect(540, ry + 8, 80, 26), confirming ? "SURE?" : "DELETE"))
                {
                    if (confirming)
                    {
                        try { MapSerializer.Delete(m.FileName); _c.ShowToast($"Deleted: {m.MapName}"); }
                        catch (Exception ex) { _c.ShowToast($"Error deleting: {ex.Message}"); }
                        _deleteConfirm = null;
                        Refresh();
                    }
                    else { _deleteConfirm = m.FileName; _deleteConfirmUntil = Time.unscaledTime + 3f; }
                }
                GUI.backgroundColor = Color.white;
                ry += 44;
            }
        }

        // ────────────────────────────────────────────────────────────── online ──

        private void DrawOnlineHeader(float y)
        {
            if (_win.Button(new Rect(230, y, 90, 26), "Refresh"))
                _c.OnlineMaps.RefreshList(force: true);

            string status = !_c.OnlineMaps.Configured ? "not configured"
                : _c.OnlineMaps.Loading ? "loading…"
                : _c.OnlineMaps.Error != null ? $"error: {_c.OnlineMaps.Error}"
                : $"{_c.OnlineMaps.Maps.Count} community map(s)";
            GUI.Label(new Rect(330, y + 5, 300, 20), status, _styleSmall);
        }

        private void DrawUploadRow(float y)
        {
            GUI.Label(new Rect(15, y + 5, 140, 20), "Publish current map:", _styleSmall);
            if (_win.ToggleButton(new Rect(160, y, 150, 26),
                _uploadEditable ? "Others: can edit" : "Others: play only", _uploadEditable))
                _uploadEditable = !_uploadEditable;

            bool canUpload = _c.OnlineMaps.Configured && !_c.OnlineMaps.Busy;
            GUI.backgroundColor = canUpload ? new Color(0.3f, 0.7f, 0.4f) : new Color(0.4f, 0.4f, 0.4f);
            if (_win.Button(new Rect(315, y, 130, 26), _c.OnlineMaps.Busy ? "…" : "UPLOAD") && canUpload)
                _c.UploadCurrentMap(_uploadEditable);
            GUI.backgroundColor = Color.white;

            if (_c.ReadOnly)
                GUI.Label(new Rect(455, y + 5, 180, 20), "(this map is play-only)", _styleSmall);
        }

        private void DrawOnlineList(float y)
        {
            var maps = _c.OnlineMaps.Maps;
            var listRect = new Rect(10, y, W - 20, ROWS * 44 + 4);
            GUI.Box(listRect, "");

            float scroll = _win.ScrollDeltaOver(listRect);
            if (scroll != 0)
                _onlineTop = Mathf.Clamp(_onlineTop + (scroll < 0 ? 2 : -2), 0, Mathf.Max(0, maps.Count - ROWS));
            _onlineTop = Mathf.Clamp(_onlineTop, 0, Mathf.Max(0, maps.Count - ROWS));

            if (!_c.OnlineMaps.Configured)
                GUI.Label(new Rect(25, y + 12, 580, 22), "Online maps not configured (set Supabase URL/key).", _styleRow);
            else if (maps.Count == 0)
                GUI.Label(new Rect(25, y + 12, 500, 22),
                    _c.OnlineMaps.EverLoaded ? "No community maps yet — be the first to upload!" : "Loading…", _styleRow);

            float ry = y + 4;
            for (int i = _onlineTop; i < maps.Count && i < _onlineTop + ROWS; i++)
            {
                var m = maps[i];
                bool mine = _c.OwnsOnlineMap(m.MapId);

                if (mine) GUI.color = new Color(0.6f, 1f, 0.7f);
                GUI.Label(new Rect(20, ry, 420, 20), Truncate(m.Name ?? "Untitled", 40), _styleRow);
                GUI.color = Color.white;
                string tag = m.Editable ? "editable" : "play-only";
                GUI.Label(new Rect(20, ry + 18, 420, 18),
                    $"by {Truncate(m.AuthorName ?? "player", 20)} — {m.ObjectCount} objs — {tag} — ⬇{m.Downloads}",
                    _styleSmall);

                GUI.backgroundColor = new Color(0.3f, 0.55f, 0.85f);
                if (_win.Button(new Rect(450, ry + 8, 100, 26), "DOWNLOAD"))
                {
                    _c.DownloadAndLoadMap(m.MapId);
                    Close();
                }
                GUI.backgroundColor = Color.white;

                if (mine)
                {
                    bool confirming = _onlineDeleteConfirm == m.MapId && Time.unscaledTime < _onlineDeleteUntil;
                    GUI.backgroundColor = confirming ? Color.red : new Color(0.7f, 0.35f, 0.3f);
                    if (_win.Button(new Rect(555, ry + 8, 65, 26), confirming ? "SURE?" : "DEL"))
                    {
                        if (confirming) { _c.DeleteOnlineMap(m.MapId); _onlineDeleteConfirm = null; }
                        else { _onlineDeleteConfirm = m.MapId; _onlineDeleteUntil = Time.unscaledTime + 3f; }
                    }
                    GUI.backgroundColor = Color.white;
                }
                ry += 44;
            }

            GUI.Label(new Rect(15, H - 26, W - 30, 20),
                "Download replaces your current objects. Play-only maps load locked (Play only).", _styleSmall);
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
