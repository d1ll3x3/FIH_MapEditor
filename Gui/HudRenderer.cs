using System;
using UnityEngine;

namespace FIHMapEditor
{
    // Always-on overlay: mode banner, play-mode timer, toasts and the idle hint.
    public class HudRenderer
    {
        private readonly EditorController _c;

        private GUIStyle _styleBanner, _styleTimer, _styleToast, _styleHint;
        private bool _stylesReady;

        // FindGameObjectsWithTag is not free; refresh the multiplayer check sparsely.
        private int _cachedPlayerCount = 1;
        private float _nextPlayerCountCheck = 0f;

        // One-click-per-frame guard for the upload-prompt buttons (same pattern as
        // GuiWindowHelper, but the HUD isn't a window — no WindowRect to hang it off).
        private bool _clickHandledThisFrame;

        public HudRenderer(EditorController controller)
        {
            _c = controller;
        }

        private void InitStyles()
        {
            if (_stylesReady) return;

            _styleBanner = new GUIStyle { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _styleBanner.normal.textColor = new Color(0.4f, 0.95f, 1f);

            _styleTimer = new GUIStyle { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _styleTimer.normal.textColor = Color.white;

            _styleToast = new GUIStyle { fontSize = 13, alignment = TextAnchor.MiddleCenter };
            _styleToast.normal.textColor = new Color(1f, 0.95f, 0.6f);

            _styleHint = new GUIStyle { fontSize = 12, alignment = TextAnchor.MiddleRight };
            _styleHint.normal.textColor = new Color(1f, 1f, 1f, 0.45f);

            _stylesReady = true;
        }

        public void Draw()
        {
            InitStyles();
            if (Event.current.type == EventType.Repaint) _clickHandledThisFrame = false;

            // The HUD isn't a draggable window, so its scale pivot is fixed (top-center)
            // rather than WindowRect-relative — CompensatedMouse() below mirrors that
            // same pivot for the upload prompt's buttons.
            var prevMatrix = GUI.matrix;
            float scale = EditorConfig.UiScale;
            if (!Mathf.Approximately(scale, 1f))
                GUIUtility.ScaleAroundPivot(Vector2.one * scale, new Vector2(Screen.width / 2f, 0f));

            switch (_c.Mode)
            {
                case EditorMode.Off:
                    GUI.Label(new Rect(Screen.width - 430, Screen.height - 28, 420, 22),
                        _c.ReadOnly
                            ? $"Play-only map — {EditorConfig.Settings.TogglePlayKey}: play  |  {EditorConfig.Settings.ToggleEditorKey}×2: discard map"
                            : $"{EditorConfig.Settings.ToggleEditorKey}: FIH Map Editor",
                        _styleHint);
                    break;

                case EditorMode.Editor:
                    DrawEditorBanner();
                    DrawInteractPrompt();
                    break;

                case EditorMode.Play:
                    DrawPlayHud();
                    DrawInteractPrompt();
                    break;
            }

            DrawToast();

            GUI.matrix = prevMatrix;
        }

        // "[E] Launch cannon" while standing next to one of our cannons.
        private void DrawInteractPrompt()
        {
            string prompt = _c.Mechanics?.InteractPrompt;
            if (string.IsNullOrEmpty(prompt)) return;
            var rect = new Rect(Screen.width / 2f - 120, Screen.height * 0.62f, 240, 26);
            GUI.Box(rect, "");
            GUI.Box(rect, "");
            GUI.Label(rect, prompt, _styleToast);
        }

        // Top banner sized to its text so the background never gets cut short.
        private void DrawTopBanner(string text)
        {
            float w;
            try { w = _styleBanner.CalcSize(new GUIContent(text)).x + 36f; }
            catch { w = text.Length * 9f + 36f; }
            var rect = new Rect(Screen.width / 2f - w / 2f, 8, w, 30);
            GUI.Box(rect, "");
            GUI.Box(rect, "");   // second pass: the box texture is translucent
            GUI.Label(rect, text, _styleBanner);
        }

        private void DrawEditorBanner()
        {
            string cursor = _c.CursorFree
                ? $"[{EditorConfig.Settings.ToggleCursorKey}] back to camera"
                : $"[{EditorConfig.Settings.ToggleCursorKey}] free cursor";
            string ro = _c.ReadOnly ? "  🔒 PLAY-ONLY" : "";
            DrawTopBanner(
                $"EDITOR — \"{_c.MapName}\" ({_c.PlacedManager.Count} objs){ro}  |  {cursor}  |  " +
                $"[{EditorConfig.Settings.TogglePlayKey}] Play  [{EditorConfig.Settings.MapsHubKey}] Maps  " +
                $"[{EditorConfig.Settings.ToggleEditorKey}] close");

            if (Time.unscaledTime >= _nextPlayerCountCheck)
            {
                _cachedPlayerCount = _c.Finder.CountPlayers();
                _nextPlayerCountCheck = Time.unscaledTime + 2f;
            }
            if (_cachedPlayerCount > 1 && _c.Multiplayer != null)
            {
                var warnRect = new Rect(Screen.width / 2f - 220, 42, 440, 22);
                // Green once we're actually syncing with someone; neutral while looking.
                GUI.color = _c.Multiplayer.PeerCount > 0
                    ? new Color(0.4f, 1f, 0.55f)
                    : new Color(1f, 0.9f, 0.6f);
                GUI.Label(warnRect, $"Co-edit — {_c.Multiplayer.StatusLine}", _styleToast);
                GUI.color = Color.white;
            }
        }

        private void DrawPlayHud()
        {
            var pm = _c.PlayMode;

            DrawTopBanner(
                $"PLAY — \"{_c.MapName}\"  |  [{EditorConfig.Settings.RestartRunKey}] retry (last coin)  " +
                $"[Shift+{EditorConfig.Settings.RestartRunKey}] full restart  [{EditorConfig.Settings.TogglePlayKey}] editor");

            if (pm.Timer != TimerState.Idle)
            {
                var timerRect = new Rect(Screen.width / 2f - 130, 42, 260, 40);
                GUI.Box(timerRect, "");

                string text = pm.Timer switch
                {
                    TimerState.Armed => "00:00.000",
                    TimerState.Running => PlayModeController.FormatTime(pm.ElapsedSeconds),
                    TimerState.Finished => PlayModeController.FormatTime(pm.ElapsedSeconds),
                    _ => "",
                };
                if (pm.Timer == TimerState.Finished)
                    _styleTimer.normal.textColor = pm.NewBest ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.9f, 0.4f);
                else
                    _styleTimer.normal.textColor = Color.white;
                GUI.Label(timerRect, text, _styleTimer);

                string sub = "";
                if (pm.Timer == TimerState.Finished)
                    sub = pm.NewBest ? "GOAL! ★ new best — R for another run" : "GOAL! — R for another run";
                else if (pm.BestTime.HasValue)
                    sub = $"best: {PlayModeController.FormatTime(pm.BestTime.Value)}";
                if (pm.CheckpointCount > 0 && pm.Timer != TimerState.Finished)
                {
                    string cp = pm.ActiveCheckpoint >= 0 ? (pm.ActiveCheckpoint + 1).ToString() : "—";
                    sub += (sub == "" ? "" : "  |  ") + $"checkpoint: {cp}/{pm.CheckpointCount}";
                }
                if (sub != "")
                    GUI.Label(new Rect(Screen.width / 2f - 200, 86, 400, 20), sub, _styleToast);
            }

            if (pm.UploadPrompt == UploadPromptState.Offered)
                DrawUploadPrompt(pm);
        }

        // Offered after every finished run, whether or not it's a new best — see
        // PlayModeController.SaveBestTime. Uses HudButton (below) since these are the
        // HUD's only clickable elements; EditorController frees the cursor while shown.
        private void DrawUploadPrompt(PlayModeController pm)
        {
            bool overwrite = !pm.PendingUploadIsNewBest;
            string time = PlayModeController.FormatTime(pm.PendingUploadSeconds);

            float w = 480, h = overwrite ? 100 : 76;
            var rect = new Rect(Screen.width / 2f - w / 2f, 130, w, h);

            GUI.backgroundColor = overwrite ? new Color(0.45f, 0.18f, 0.14f, 0.96f) : new Color(0.12f, 0.32f, 0.18f, 0.96f);
            GUI.Box(rect, "");
            GUI.Box(rect, "");
            GUI.backgroundColor = Color.white;

            string line1 = overwrite
                ? $"Not a new best (best: {PlayModeController.FormatTime(pm.BestTime ?? pm.PendingUploadSeconds)}). Upload {time} anyway?"
                : $"New best time! Upload {time} to the leaderboard?";
            GUI.Label(new Rect(rect.x + 15, rect.y + 8, w - 30, 22), line1, _styleToast);
            if (overwrite)
                GUI.Label(new Rect(rect.x + 15, rect.y + 30, w - 30, 20),
                    "This will overwrite your time on the leaderboard.", _styleHint);

            float by = rect.y + h - 34;
            GUI.backgroundColor = overwrite ? new Color(0.75f, 0.3f, 0.25f) : new Color(0.25f, 0.7f, 0.4f);
            if (HudButton(new Rect(rect.x + w / 2f - 155, by, 150, 26), overwrite ? "Yes, upload" : "Upload"))
                pm.ConfirmUpload();
            GUI.backgroundColor = new Color(0.4f, 0.4f, 0.4f);
            if (HudButton(new Rect(rect.x + w / 2f + 5, by, 150, 26), overwrite ? "Cancel" : "Skip"))
                pm.DismissUploadPrompt();
            GUI.backgroundColor = Color.white;
        }

        // Mirrors the GUI.matrix pivot used in Draw() so clicks land correctly at any
        // UI scale — the HUD has no WindowRect to hang the compensation off (unlike
        // GuiWindowHelper), so it's inlined here for this one interactive element.
        private Vector2 CompensatedMouse()
        {
            Vector2 m = Input.mousePosition;
            m.y = Screen.height - m.y;
            float scale = EditorConfig.UiScale;
            if (Mathf.Approximately(scale, 1f)) return m;
            Vector2 pivot = new Vector2(Screen.width / 2f, 0f);
            return pivot + (m - pivot) / scale;
        }

        private bool HudButton(Rect rect, string text)
        {
            GUI.Box(rect, text);
            if (_clickHandledThisFrame) return false;
            if (Input.GetMouseButtonDown(0) && Event.current.type == EventType.Repaint
                && rect.Contains(CompensatedMouse()))
            {
                _clickHandledThisFrame = true;
                return true;
            }
            return false;
        }

        private void DrawToast()
        {
            string toast = _c.ToastMessage;
            if (string.IsNullOrEmpty(toast)) return;

            float w = Mathf.Max(300, toast.Length * 8f);
            var rect = new Rect(Screen.width / 2f - w / 2f, Screen.height - 70, w, 26);
            GUI.Box(rect, "");
            GUI.Label(rect, toast, _styleToast);
        }
    }
}
