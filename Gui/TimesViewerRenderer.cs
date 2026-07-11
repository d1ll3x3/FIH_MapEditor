using System;
using UnityEngine;

namespace FIHMapEditor
{
    // Standalone leaderboard window for PLAY-ONLY maps: the editor (and its TIMES tab)
    // is locked on those, but players still need to see the board. Opened with the
    // editor key from the plain game (Off mode); also hosts the "discard map" action
    // that used to live on the double-F6 shortcut.
    public class TimesViewerRenderer
    {
        private readonly EditorController _c;
        private readonly GuiWindowHelper _win;
        private readonly GUI.WindowFunction _windowDelegate;

        private const float W = 540f;
        private const float H = 570f;
        private const int WINDOW_ID = 51604;
        private const int ROWS = 13;

        public bool Visible { get; private set; }

        private int _top;
        private float _discardConfirmUntil;
        private string _fetchedFor;

        private GUIStyle _styleTitle, _styleRow, _styleSmall;
        private bool _stylesReady;

        public TimesViewerRenderer(EditorController controller)
        {
            _c = controller;
            _win = new GuiWindowHelper(new Rect(Screen.width / 2f - W / 2f, Screen.height / 2f - H / 2f, W, H));
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
            _top = 0;
            _discardConfirmUntil = 0f;
            if (_c.Leaderboard.Configured && !string.IsNullOrEmpty(_c.MapId) && _fetchedFor != _c.MapId)
            {
                _fetchedFor = _c.MapId;
                _c.Leaderboard.FetchBoard(_c.MapId);
            }
        }

        public void Close()
        {
            Visible = false;
            _discardConfirmUntil = 0f;
        }

        private void InitStyles()
        {
            if (_stylesReady) return;
            _styleTitle = new GUIStyle { fontSize = 16, fontStyle = FontStyle.Bold };
            _styleTitle.normal.textColor = new Color(0.5f, 0.85f, 1f);
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
            _win.WindowRect = GUI.Window(WINDOW_ID, _win.WindowRect, _windowDelegate, "LEADERBOARD");
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

            float y = 30;
            GUI.Label(new Rect(15, y, W - 120, 22), $"\"{Truncate(_c.MapName, 30)}\"", _styleTitle);
            y += 24;
            string author = string.IsNullOrEmpty(_c.AuthorName) ? "?" : _c.AuthorName;
            GUI.Label(new Rect(15, y, W - 170, 20), $"by {Truncate(author, 24)} — play-only map", _styleSmall);
            if (_c.Leaderboard.Configured && _win.Button(new Rect(W - 115, y - 2, 100, 24), "Refresh"))
            {
                _top = 0;
                _c.Leaderboard.FetchBoard(_c.MapId, force: true);
            }
            y += 28;

            if (!_c.Leaderboard.Configured)
            {
                GUI.Label(new Rect(15, y + 6, W - 30, 40), "Global leaderboard not configured.", _styleSmall);
            }
            else
            {
                var entries = _c.Leaderboard.GetEntries(_c.MapId);
                long self = _c.SteamIdentity().steamId;

                GUI.Label(new Rect(20, y, 45, 20), "#", _styleSmall);
                GUI.Label(new Rect(65, y, 300, 20), "Player", _styleSmall);
                GUI.Label(new Rect(390, y, 130, 20), "Time", _styleSmall);
                y += 22;

                var listRect = new Rect(10, y, W - 20, ROWS * 26 + 4);
                GUI.Box(listRect, "");

                float scroll = _win.ScrollDeltaOver(listRect);
                if (scroll != 0)
                    _top = Mathf.Clamp(_top + (scroll < 0 ? 3 : -3), 0, Mathf.Max(0, entries.Count - ROWS));
                _top = Mathf.Clamp(_top, 0, Mathf.Max(0, entries.Count - ROWS));

                if (_c.Leaderboard.IsLoading(_c.MapId) && entries.Count == 0)
                    GUI.Label(new Rect(25, y + 10, 300, 22), "Loading…", _styleRow);
                else if (entries.Count == 0)
                    GUI.Label(new Rect(25, y + 10, W - 50, 22),
                        _c.Leaderboard.GetError(_c.MapId) != null
                            ? $"Couldn't load times: {_c.Leaderboard.GetError(_c.MapId)}"
                            : "No times yet — be the first!", _styleRow);

                float ry = y + 4;
                for (int i = _top; i < entries.Count && i < _top + ROWS; i++)
                {
                    var e = entries[i];
                    if (self != 0 && e.SteamId == self)
                    {
                        GUI.backgroundColor = new Color(0.2f, 0.75f, 0.35f);
                        GUI.Box(new Rect(14, ry - 1, W - 28, 24), "");
                        GUI.backgroundColor = Color.white;
                    }
                    GUI.Label(new Rect(20, ry, 45, 22), $"#{i + 1}", _styleRow);
                    GUI.Label(new Rect(65, ry, 315, 22), Truncate(e.PlayerName ?? "player", 26), _styleRow);
                    GUI.Label(new Rect(390, ry, 140, 22), PlayModeController.FormatTime(e.TimeSeconds), _styleRow);
                    ry += 26;
                }
                y += ROWS * 26 + 12;
            }

            // Footer: play hint + the discard action (was the double-F6 shortcut).
            float fy = H - 66;
            GUI.Label(new Rect(15, fy, W - 30, 20),
                $"{EditorConfig.Settings.TogglePlayKey}: play the map  |  {EditorConfig.Settings.ToggleEditorKey}: close this window",
                _styleSmall);
            fy += 26;
            bool confirming = Time.unscaledTime < _discardConfirmUntil;
            GUI.backgroundColor = confirming ? Color.red : new Color(0.7f, 0.35f, 0.3f);
            if (_win.Button(new Rect(15, fy, 260, 26),
                confirming ? "SURE? Map will be discarded" : "Discard map (open empty editor)"))
            {
                if (confirming) { _c.DiscardPlayOnlyMap(); Close(); }
                else _discardConfirmUntil = Time.unscaledTime + 3f;
            }
            GUI.backgroundColor = Color.white;
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
