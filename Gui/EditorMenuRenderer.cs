using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    // Main editor window: CATALOG / SELECT / TOOLS / LIST / MAP tabs + footer.
    public class EditorMenuRenderer
    {
        private readonly EditorController _c;
        private readonly GuiWindowHelper _win;
        private readonly GUI.WindowFunction _windowDelegate;

        private const float W = 680f;
        private const float H = 700f;
        private const int WINDOW_ID = 51601;

        private enum Tab { Catalog, Select, Tools, List, Map, Keys }
        private Tab _tab = Tab.Catalog;

        // KEYS tab: id of the action currently listening for a key, or null.
        private string _listeningBind = null;
        public bool IsCapturingBind => _listeningBind != null;

        // Catalog state
        private string _filter = "";
        private string _category = "";        // empty = all
        private bool _solidOnly = false;      // hide collider-less decoration (SM meshes...)
        private bool _favsOnly = false;       // show starred entries only
        private int _catalogTop = 0;
        private const int CATALOG_ROWS = 15;

        // List state
        private string _listFilter = "";
        private int _listTop = 0;
        private const int LIST_ROWS = 16;

        // Multi-clone state
        private int _mcCount = 5;
        private Vector3 _mcDir = new Vector3(1, 0, 0);
        private string _mcDirLabel = "+X";
        private bool _mcStairs = false;

        // Select tab state: numeric text boxes for rotation degrees and scale
        private string _rotField = "45";
        private string _scaleField = "0.5";

        // Map tab state
        private string _nameField = null;     // lazily initialised from controller

        // Wipe / revert confirmations
        private float _wipeConfirmUntil = 0f;
        private float _revertEditsConfirmUntil = 0f;

        private GUIStyle _styleTitle, _styleSmall, _styleRow;
        private bool _stylesReady;

        public bool HasFocusedTextField => _win.HasFocusedTextField;

        public void UnfocusFields()
        {
            _win.Unfocus();
            _listeningBind = null;
        }

        // The menu is always on screen in editor mode; world clicks on it are ignored.
        public bool ContainsMouse()
            => _c.Mode == EditorMode.Editor && _win.WindowRect.Contains(GuiWindowHelper.MouseGuiPosition());

        public EditorMenuRenderer(EditorController controller)
        {
            _c = controller;
            _win = new GuiWindowHelper(new Rect(Screen.width - W - 20, 40, W, H))
            {
                // The Maps hub is modal on top of this window.
                InputBlocked = () => _c.MapsHubOpen || !_c.CursorFree,
            };
            _windowDelegate = new Action<int>(WindowFunction);
        }

        private void InitStyles()
        {
            if (_stylesReady) return;
            _styleTitle = new GUIStyle { fontSize = 16, fontStyle = FontStyle.Bold };
            _styleTitle.normal.textColor = new Color(0.5f, 0.85f, 1f);
            _styleSmall = new GUIStyle { fontSize = 13 };
            _styleSmall.normal.textColor = new Color(0.92f, 0.92f, 0.92f);
            _styleRow = new GUIStyle { fontSize = 14, alignment = TextAnchor.MiddleLeft };
            _styleRow.normal.textColor = Color.white;
            _stylesReady = true;
        }

        public void Draw()
        {
            InitStyles();
            _win.Visible = true;
            _win.BeginFrame();

            GUI.backgroundColor = new Color(0.13f, 0.13f, 0.16f, 0.97f);
            _win.WindowRect = GUI.Window(WINDOW_ID, _win.WindowRect, _windowDelegate, "FIH CUSTOM MAP EDITOR");
            GUI.backgroundColor = Color.white;
        }

        private void WindowFunction(int id)
        {
            // The stock window style doesn't reliably paint the full body in this build;
            // fill the client area ourselves so content never sits on a transparent hole.
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.11f, 0.11f, 0.14f, 1f);
            GUI.Box(new Rect(0, 20, W, H - 20), "");
            GUI.Box(new Rect(0, 20, W, H - 20), "");
            GUI.backgroundColor = prevBg;

            // Bigger, brighter text on every Box-based widget (buttons, fields).
            GUI.skin.box.fontSize = 14;
            GUI.skin.box.normal.textColor = Color.white;

            DrawHeaderRow();
            DrawTabs();

            switch (_tab)
            {
                case Tab.Catalog: DrawCatalogTab(); break;
                case Tab.Select: DrawSelectTab(); break;
                case Tab.Tools: DrawToolsTab(); break;
                case Tab.List: DrawListTab(); break;
                case Tab.Map: DrawMapTab(); break;
                case Tab.Keys: DrawKeysTab(); break;
            }

            DrawFooter();
        }

        // Always-visible row: mouse-place master toggle + return-to-camera button.
        private void DrawHeaderRow()
        {
            if (!_c.CursorFree)
            {
                GUI.Label(new Rect(20, 30, W - 40, 22),
                    $"Press {EditorConfig.Settings.ToggleCursorKey} to free the cursor and use the menu", _styleTitle);
                return;
            }

            bool on = _c.MousePlaceEnabled;
            if (_win.ToggleButton(new Rect(15, 28, 320, 24),
                on ? "Mouse placement: ON" : "Mouse placement: OFF", on))
                _c.MousePlaceEnabled = !on;

            if (_win.Button(new Rect(345, 28, 320, 24),
                $"Back to camera ({EditorConfig.Settings.ToggleCursorKey})"))
                _c.SetCursorFree(false);
        }

        private void DrawTabs()
        {
            float y = 58;
            string[] names = { "CATALOG", "SELECT", "TOOLS", "LIST", "MAP", "KEYS" };
            float w = (W - 30) / names.Length;
            for (int i = 0; i < names.Length; i++)
            {
                if (_win.ToggleButton(new Rect(15 + i * w, y, w - 4, 26), names[i], (int)_tab == i))
                    _tab = (Tab)i;
            }
        }

        private float ContentY => 96;

        // ─────────────────────────────────────────────────────────── CATALOG ──

        private void DrawCatalogTab()
        {
            float y = ContentY;

            GUI.Label(new Rect(15, y + 3, 45, 20), "Filter:", _styleSmall);
            _filter = _win.TextField(new Rect(60, y, 160, 22), "filter", _filter);
            if (_win.Button(new Rect(228, y, 80, 22), "Rescan"))
            {
                _c.Catalog.Scan();
                _catalogTop = 0;
                _c.ShowToast($"Catalog: {_c.Catalog.Entries.Count} unique objects");
            }
            // Hide pure decoration (SM meshes, VFX...) that has no collision to stand on.
            if (_win.ToggleButton(new Rect(314, y, 120, 22), "Solid only", _solidOnly))
            {
                _solidOnly = !_solidOnly;
                _catalogTop = 0;
            }
            if (_c.StampEntry != null)
            {
                string state = _c.MousePlaceEnabled ? "" : " (mouse OFF)";
                GUI.backgroundColor = _c.MousePlaceEnabled ? new Color(1f, 0.5f, 0.2f) : new Color(0.45f, 0.45f, 0.45f);
                if (_win.Button(new Rect(444, y, W - 459, 22), $"STAMP: {Truncate(_c.StampEntry.DisplayName, 11)}{state} [X]"))
                    _c.StampEntry = null;
                GUI.backgroundColor = Color.white;
            }
            else
            {
                GUI.Label(new Rect(444, y + 3, W - 459, 20), "Stamp: click places copies", _styleSmall);
            }
            y += 28;

            // Category filter — wraps onto extra rows so no category is ever dropped.
            float cx = 15;
            if (_win.ToggleButton(new Rect(cx, y, 70, 22), "★ Favs", _favsOnly))
                { _favsOnly = !_favsOnly; _catalogTop = 0; }
            cx += 74;
            if (_win.ToggleButton(new Rect(cx, y, 60, 22), "All", _category == "" && !_favsOnly))
                { _category = ""; _favsOnly = false; _catalogTop = 0; }
            cx += 64;
            foreach (var cat in _c.Catalog.Categories)
            {
                float cw = 22 + cat.Length * 9;
                if (cx + cw > W - 15)
                {
                    cx = 15;
                    y += 26;
                }
                if (_win.ToggleButton(new Rect(cx, y, cw, 22), cat, _category == cat))
                    { _category = cat; _catalogTop = 0; }
                cx += cw + 4;
            }
            y += 28;

            var entries = FilteredEntries();
            var listRect = new Rect(10, y, W - 20, CATALOG_ROWS * 26 + 4);
            GUI.Box(listRect, "");

            float scroll = _win.ScrollDeltaOver(listRect);
            if (scroll != 0)
                _catalogTop = Mathf.Clamp(_catalogTop + (scroll < 0 ? 3 : -3), 0, Mathf.Max(0, entries.Count - CATALOG_ROWS));

            if (!_c.Catalog.HasScanned)
            {
                GUI.Label(new Rect(25, y + 10, 400, 22), "Catalog not scanned yet — press Rescan", _styleRow);
            }
            else if (entries.Count == 0)
            {
                GUI.Label(new Rect(25, y + 10, 400, 22), "No results for this filter", _styleRow);
            }

            float ry = y + 4;
            for (int i = _catalogTop; i < entries.Count && i < _catalogTop + CATALOG_ROWS; i++)
            {
                var e = entries[i];
                bool fav = IsFavorite(e.DisplayName);
                GUI.color = fav ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 1f, 1f, 0.35f);
                if (_win.Button(new Rect(16, ry + 1, 26, 22), "★"))
                    ToggleFavorite(e.DisplayName);
                GUI.color = Color.white;

                string size = $"{e.BoundsSize.x:0.#}×{e.BoundsSize.y:0.#}×{e.BoundsSize.z:0.#}";
                GUI.Label(new Rect(48, ry, 422, 24), $"{Truncate(e.DisplayName, 30)}  ({size})", _styleRow);
                if (_win.Button(new Rect(480, ry + 1, 85, 22), "PLACE"))
                    _c.PlaceEntry(e);
                bool isStamp = _c.StampEntry == e;
                if (_win.ToggleButton(new Rect(570, ry + 1, 85, 22), "STAMP", isStamp))
                {
                    _c.StampEntry = isStamp ? null : e;
                    // Picking a stamp means you want to place: flip the master switch on.
                    if (_c.StampEntry != null) _c.MousePlaceEnabled = true;
                }
                ry += 26;
            }

            y += CATALOG_ROWS * 26 + 10;
            GUI.Label(new Rect(15, y, W - 30, 20),
                $"{entries.Count} objects  |  mouse wheel: scroll  |  PLACE: in front of you  |  STAMP: click in the world",
                _styleSmall);
        }

        private List<CatalogEntry> FilteredEntries()
        {
            // Favorites always float to the top; alphabetical order within each group.
            var favs = new List<CatalogEntry>();
            var rest = new List<CatalogEntry>();
            string f = _filter.Trim().ToLowerInvariant();
            foreach (var e in _c.Catalog.Entries)
            {
                bool fav = IsFavorite(e.DisplayName);
                if (_favsOnly && !fav) continue;
                if (_category != "" && e.Category != _category) continue;
                if (_solidOnly && !e.HasCollider) continue;
                if (f != "" && !e.DisplayName.ToLowerInvariant().Contains(f)) continue;
                (fav ? favs : rest).Add(e);
            }
            favs.AddRange(rest);
            return favs;
        }

        private static bool IsFavorite(string name)
            => EditorConfig.Settings.FavoriteObjects.Contains(name);

        private static void ToggleFavorite(string name)
        {
            var favs = EditorConfig.Settings.FavoriteObjects;
            if (!favs.Remove(name)) favs.Add(name);
            EditorConfig.Save();
        }

        // ─────────────────────────────────────────────────────────── SELECT ──

        private void DrawSelectTab()
        {
            float y = ContentY;
            var sel = _c.SelectionSys.Current;

            string selName = sel.IsValid ? sel.DisplayName : "(nothing selected — click an object)";
            GUI.Label(new Rect(15, y, 325, 22), $"Selected: {Truncate(selName, 26)}", _styleTitle);
            if (_win.ToggleButton(new Rect(345, y, 150, 22), $"Unlock: {(_c.UnlockOriginals ? "ON" : "OFF")}", _c.UnlockOriginals))
                _c.UnlockOriginals = !_c.UnlockOriginals;
            // OFF = clicks ignore triggers and renderless colliders (kill zones, invisible
            // walls) so you always pick what you actually see.
            if (_win.ToggleButton(new Rect(500, y, 165, 22), $"Pick invisible: {(_c.PickInvisible ? "ON" : "OFF")}", _c.PickInvisible))
                _c.PickInvisible = !_c.PickInvisible;
            y += 28;

            // Gizmo mode (mouse dragging on the axes; 1/2/3 hotkeys)
            GUI.Label(new Rect(15, y + 3, 55, 20), "Gizmo:", _styleSmall);
            (string, GizmoMode)[] gizmoModes = { ("Move (1)", GizmoMode.Move), ("Rotate (2)", GizmoMode.Rotate), ("Scale (3)", GizmoMode.Scale) };
            for (int i = 0; i < gizmoModes.Length; i++)
            {
                if (_win.ToggleButton(new Rect(75 + i * 104, y, 100, 22), gizmoModes[i].Item1, _c.Gizmo.Mode == gizmoModes[i].Item2))
                    _c.Gizmo.Mode = gizmoModes[i].Item2;
            }
            // X-ray: wireframes on trigger zones / invisible walls (magenta = trigger,
            // white = solid). Pairs naturally with "Pick invisible".
            if (_win.ToggleButton(new Rect(500, y, 165, 22), $"Show invisible: {(_c.Xray.Enabled ? "ON" : "OFF")}", _c.Xray.Enabled))
            {
                _c.Xray.SetEnabled(!_c.Xray.Enabled);
                if (_c.Xray.Enabled)
                    _c.ShowToast($"X-ray: {_c.Xray.Count} invisible objects (magenta = trigger, white = solid). " +
                                 (_c.PickInvisible ? "" : "Turn 'Pick invisible' ON to click them."));
            }
            y += 28;

            // Move speed
            GUI.Label(new Rect(15, y + 3, 100, 20), "Move Speed:", _styleSmall);
            int[] speeds = { 1, 4, 10 };
            for (int i = 0; i < speeds.Length; i++)
            {
                if (_win.ToggleButton(new Rect(115 + i * 64, y, 60, 22), $"x{speeds[i]}", _c.MoveSpeedMultiplier == speeds[i]))
                    _c.MoveSpeedMultiplier = speeds[i];
            }
            GUI.Label(new Rect(320, y + 3, 280, 20), $"step: {EditorConfig.Settings.NudgeStep * _c.MoveSpeedMultiplier:0.##} m", _styleSmall);
            y += 28;

            // Move buttons
            float step = EditorConfig.Settings.NudgeStep * _c.MoveSpeedMultiplier;
            string[] moveLabels = { "-X", "+X", "-Y", "+Y", "-Z", "+Z" };
            Vector3[] moveDirs = { Vector3.left, Vector3.right, Vector3.down, Vector3.up, Vector3.back, Vector3.forward };
            GUI.Label(new Rect(15, y + 3, 60, 20), "Move:", _styleSmall);
            for (int i = 0; i < 6; i++)
            {
                if (_win.Button(new Rect(75 + i * 68, y, 64, 24), moveLabels[i]))
                    _c.MoveSelected(moveDirs[i] * step);
            }
            y += 30;

            // Rotation: type the degrees in the box, buttons rotate by that amount.
            GUI.Label(new Rect(15, y + 4, 70, 20), "Rotate (°):", _styleSmall);
            _rotField = _win.TextField(new Rect(85, y, 60, 22), "rotstep", _rotField);
            float deg = ParseFloat(_rotField, 45f);
            _c.RotateStepDegrees = deg;
            if (_win.Button(new Rect(152, y, 55, 24), "Y -")) _c.RotateSelectedY(-deg);
            if (_win.Button(new Rect(211, y, 55, 24), "Y +")) _c.RotateSelectedY(deg);
            if (_win.Button(new Rect(270, y, 55, 24), "X -")) _c.RotateSelected(Vector3.right, -deg);
            if (_win.Button(new Rect(329, y, 55, 24), "X +")) _c.RotateSelected(Vector3.right, deg);
            if (_win.Button(new Rect(388, y, 55, 24), "Z -")) _c.RotateSelected(Vector3.forward, -deg);
            if (_win.Button(new Rect(447, y, 55, 24), "Z +")) _c.RotateSelected(Vector3.forward, deg);
            if (_win.Button(new Rect(510, y, 95, 24), "Reset All")) _c.ResetSelected();
            y += 30;

            // Scale: the box value is both the +/- step and the absolute factor for Set.
            GUI.Label(new Rect(15, y + 4, 70, 20), "Scale:", _styleSmall);
            _scaleField = _win.TextField(new Rect(85, y, 60, 22), "scalestep", _scaleField);
            float scaleVal = ParseFloat(_scaleField, 0.5f);
            _c.ScaleStep = scaleVal;
            if (_win.Button(new Rect(152, y, 55, 24), "-")) _c.ScaleSelected(-scaleVal);
            if (_win.Button(new Rect(211, y, 55, 24), "+")) _c.ScaleSelected(scaleVal);
            if (_win.Button(new Rect(270, y, 114, 24), "Set size = value")) _c.SetScaleFactorSelected(scaleVal);
            if (sel.IsPlaced && sel.Placed.Root != null)
            {
                var ls = sel.Placed.Root.transform.localScale;
                GUI.Label(new Rect(392, y + 4, 210, 20), $"current: {ls.x:0.##} × {ls.y:0.##} × {ls.z:0.##}", _styleSmall);
            }
            y += 30;

            // Per-axis scale (local axes), stepping by the same box value.
            GUI.Label(new Rect(15, y + 4, 70, 20), "Scale axis:", _styleSmall);
            string[] axisLabels = { "X -", "X +", "Y -", "Y +", "Z -", "Z +" };
            for (int i = 0; i < 6; i++)
            {
                if (_win.Button(new Rect(85 + i * 60, y, 56, 24), axisLabels[i]))
                    _c.ScaleSelectedAxis(i / 2, (i % 2 == 0 ? -1f : 1f) * scaleVal);
            }
            y += 30;

            // Actions row
            if (_win.Button(new Rect(15, y, 140, 24), "Duplicate (Ctrl+D)")) _c.DuplicateSelected();
            if (_win.Button(new Rect(160, y, 100, 24), "Delete (Del)")) _c.DeleteSelected();
            if (_win.Button(new Rect(265, y, 115, 24), "Drop to Floor")) _c.DropSelectedToFloor();
            if (_win.Button(new Rect(385, y, 105, 24), "Align (grid)")) _c.AlignSelected();
            y += 30;

            if (_win.Button(new Rect(15, y, 160, 24), "TP Player Onto It")) _c.TpPlayerOntoSelected();
            if (_win.Button(new Rect(180, y, 160, 24), "Bring Object Here")) _c.BringSelectedHere();
            y += 34;

            // Multi-clone
            GUI.Label(new Rect(15, y, 200, 20), "Multi-Clone Array:", _styleTitle);
            y += 24;
            GUI.Label(new Rect(15, y + 3, 55, 20), "Copies:", _styleSmall);
            int[] counts = { 3, 5, 10 };
            for (int i = 0; i < counts.Length; i++)
            {
                if (_win.ToggleButton(new Rect(70 + i * 48, y, 44, 22), counts[i].ToString(), _mcCount == counts[i]))
                    _mcCount = counts[i];
            }
            GUI.Label(new Rect(225, y + 3, 30, 20), "Dir:", _styleSmall);
            string[] dirLabels = { "+X", "-X", "+Z", "-Z" };
            Vector3[] dirs = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
            for (int i = 0; i < 4; i++)
            {
                if (_win.ToggleButton(new Rect(255 + i * 48, y, 44, 22), dirLabels[i], _mcDirLabel == dirLabels[i]))
                    { _mcDir = dirs[i]; _mcDirLabel = dirLabels[i]; }
            }
            if (_win.ToggleButton(new Rect(452, y, 78, 22), "Stairs+Y", _mcStairs))
                _mcStairs = !_mcStairs;
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.4f);
            if (_win.Button(new Rect(535, y, 70, 22), "GO"))
                _c.MultiClone(_mcCount, _mcDir, _mcStairs);
            GUI.backgroundColor = Color.white;
            y += 32;

            GUI.Label(new Rect(15, y, W - 30, 60),
                "Keys (free cursor): arrows = move  |  PgUp/PgDn = up/down  |  1/2/3 = gizmo mode\n" +
                "[ ] = rotate Y by the ° box  |  +/- = scale by the box  |  Ctrl+D = duplicate  |  Del = delete  |  Ctrl+Z = undo",
                _styleSmall);
        }

        // ──────────────────────────────────────────────────────────── TOOLS ──

        private void DrawToolsTab()
        {
            float y = ContentY;

            GUI.Label(new Rect(15, y, 300, 22), "Map spawn and goal:", _styleTitle);
            y += 26;
            if (_win.Button(new Rect(15, y, 150, 26), "Set Spawn Here")) _c.SetSpawnHere();
            GUI.Label(new Rect(172, y + 5, 180, 20),
                _c.Spawn?.Pos != null ? $"spawn: {FmtVec(_c.Spawn.Pos)}" : "no spawn (uses the game's)", _styleSmall);
            if (_c.Spawn != null && _win.Button(new Rect(360, y, 80, 26), "Remove")) _c.ClearSpawn();
            y += 32;

            if (_win.Button(new Rect(15, y, 150, 26), "Set Goal Here")) _c.SetGoalHere();
            GUI.Label(new Rect(172, y + 5, 180, 20),
                _c.Goal?.Center != null ? $"goal: {FmtVec(_c.Goal.Center)}" : "no goal (no timer)", _styleSmall);
            if (_c.Goal != null)
            {
                if (_win.Button(new Rect(360, y, 80, 26), "Remove")) _c.ClearGoal();
                if (_win.Button(new Rect(445, y, 60, 26), "Size -")) _c.AdjustGoalSize(-1f);
                if (_win.Button(new Rect(510, y, 60, 26), "Size +")) _c.AdjustGoalSize(1f);
            }
            y += 34;

            GUI.Label(new Rect(15, y, 400, 22), "Checkpoints & reset triggers:", _styleTitle);
            y += 26;
            if (_win.Button(new Rect(15, y, 190, 26), "Add Checkpoint Here")) _c.AddCheckpointHere();
            GUI.Label(new Rect(212, y + 5, 130, 20), $"{_c.Checkpoints.Count} placed", _styleSmall);
            if (_win.Button(new Rect(340, y, 190, 26), "Add Reset Trigger Here")) _c.AddResetZoneHere();
            GUI.Label(new Rect(537, y + 5, 130, 20), $"{_c.ResetZones.Count} placed", _styleSmall);
            y += 30;
            GUI.Label(new Rect(15, y, W - 30, 20),
                "Click a ring/red box to select it; edit with the gizmo; Del removes it.", _styleSmall);
            y += 26;

            GUI.Label(new Rect(15, y, 300, 22), "Map base:", _styleTitle);
            y += 26;
            if (_win.ToggleButton(new Rect(15, y, 200, 26), "Original level (Overlay)", _c.BaseMode == MapBaseMode.Overlay))
                _c.SetBaseMode(MapBaseMode.Overlay);
            GUI.backgroundColor = _c.BaseMode == MapBaseMode.Blank ? Color.red : new Color(0.8f, 0.4f, 0.3f);
            if (_win.Button(new Rect(220, y, 200, 26),
                _c.BaseMode == MapBaseMode.Blank ? "Level wiped ✓" : "Wipe Level (clean space)"))
                _c.WipeLevelGeometry();
            GUI.backgroundColor = Color.white;
            y += 40;

            GUI.Label(new Rect(15, y, 300, 22), "Actions:", _styleTitle);
            y += 26;
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.4f);
            if (_win.Button(new Rect(15, y, 200, 30), $"► Enter PLAY mode ({EditorConfig.Settings.TogglePlayKey})"))
                _c.SetMode(EditorMode.Play);
            GUI.backgroundColor = Color.white;

            bool confirming = Time.unscaledTime < _wipeConfirmUntil;
            GUI.backgroundColor = confirming ? Color.red : new Color(0.8f, 0.4f, 0.3f);
            if (_win.Button(new Rect(230, y, 200, 30), confirming ? "SURE? (click again)" : "Wipe Custom Objects"))
            {
                if (confirming) { _c.WipeCustomObjects(); _wipeConfirmUntil = 0f; }
                else _wipeConfirmUntil = Time.unscaledTime + 3f;
            }
            GUI.backgroundColor = Color.white;

            if (_c.LevelEdits.Count > 0)
            {
                bool confirmingEdits = Time.unscaledTime < _revertEditsConfirmUntil;
                GUI.backgroundColor = confirmingEdits ? Color.red : new Color(0.8f, 0.6f, 0.3f);
                if (_win.Button(new Rect(445, y, 200, 30),
                    confirmingEdits ? "SURE? (click again)" : $"Revert Level Edits ({_c.LevelEdits.Count})"))
                {
                    if (confirmingEdits) { _c.RestoreAllLevelEdits(); _revertEditsConfirmUntil = 0f; }
                    else _revertEditsConfirmUntil = Time.unscaledTime + 3f;
                }
                GUI.backgroundColor = Color.white;
            }
            y += 44;

            GUI.Label(new Rect(15, y, W - 30, 96),
                "Play mode: if a spawn is set you appear there; the timer starts when you\n" +
                "move and stops at the goal. R = retry from your last coin; Shift+R = full restart.\n" +
                "Touching a checkpoint ring makes it your respawn; entering a reset\n" +
                "trigger (invisible in play) sends you back to it without resetting the timer.\n" +
                "Wipe Level: hides every game asset for a truly clean space (revertible).",
                _styleSmall);
        }

        // ───────────────────────────────────────────────────────────── LIST ──

        private void DrawListTab()
        {
            float y = ContentY;

            // Filtered views of both lists: our clones and edited level objects.
            string f = _listFilter.Trim().ToLowerInvariant();
            var placed = new List<PlacedObject>();
            foreach (var p in _c.PlacedManager.Placed)
            {
                if (p.Root == null) continue;
                if (f != "" && !p.Root.name.ToLowerInvariant().Contains(f)) continue;
                placed.Add(p);
            }
            var edits = new List<LevelEditRecord>();
            foreach (var r in _c.LevelEdits.Records)
            {
                if (r.Target == null) continue;
                if (f != "" && !r.Name.ToLowerInvariant().Contains(f)) continue;
                edits.Add(r);
            }

            GUI.Label(new Rect(15, y + 3, 300, 22),
                $"Placed: {placed.Count}   Level edits: {edits.Count}", _styleTitle);
            GUI.Label(new Rect(320, y + 6, 45, 20), "Filter:", _styleSmall);
            _listFilter = _win.TextField(new Rect(370, y + 2, 220, 22), "listfilter", _listFilter);
            if (_listFilter != "" && _win.Button(new Rect(596, y + 2, 44, 22), "X"))
                _listFilter = "";
            y += 30;

            var listRect = new Rect(10, y, W - 20, LIST_ROWS * 26 + 4);
            GUI.Box(listRect, "");

            int total = placed.Count + edits.Count;
            float scroll = _win.ScrollDeltaOver(listRect);
            if (scroll != 0)
                _listTop = Mathf.Clamp(_listTop + (scroll < 0 ? 3 : -3), 0, Mathf.Max(0, total - LIST_ROWS));
            _listTop = Mathf.Clamp(_listTop, 0, Mathf.Max(0, total - LIST_ROWS));

            float ry = y + 4;
            for (int i = _listTop; i < total && i < _listTop + LIST_ROWS; i++)
            {
                if (i < placed.Count) DrawPlacedRow(placed[i], ry);
                else DrawLevelEditRow(edits[i - placed.Count], ry);
                ry += 26;
            }
        }

        private void DrawPlacedRow(PlacedObject p, float ry)
        {
            bool isSelected = _c.SelectionSys.Current.Placed == p;
            var pos = p.Root.transform.position;

            if (isSelected) GUI.backgroundColor = new Color(0.2f, 0.75f, 0.35f);
            if (_win.Button(new Rect(15, ry, 470, 24),
                $"#{p.Id:000}  {Truncate(p.Root.name, 30)}  ({pos.x:0.#}, {pos.y:0.#}, {pos.z:0.#})"))
            {
                _c.SelectionSys.Select(p);
            }
            GUI.backgroundColor = Color.white;

            if (_win.Button(new Rect(490, ry, 85, 24), "TP to it"))
            {
                _c.SelectionSys.Select(p);
                _c.TpPlayerOntoSelected();
            }
            GUI.backgroundColor = new Color(0.8f, 0.35f, 0.3f);
            if (_win.Button(new Rect(580, ry, 60, 24), "X"))
            {
                _c.SelectionSys.Select(p);
                _c.DeleteSelected();
            }
            GUI.backgroundColor = Color.white;
        }

        private void DrawLevelEditRow(LevelEditRecord r, float ry)
        {
            bool isSelected = _c.SelectionSys.Current.Raw == r.Target;
            string state = r.Hidden
                ? (r.TransformEdited ? "hidden+moved" : "hidden")
                : "moved";

            if (isSelected) GUI.backgroundColor = new Color(0.2f, 0.75f, 0.35f);
            else GUI.backgroundColor = new Color(0.9f, 0.75f, 0.4f);
            if (_win.Button(new Rect(15, ry, 470, 24), $"[LVL] {Truncate(r.Name, 30)}  ({state})"))
            {
                _c.SelectionSys.SelectRaw(r.Target);
            }
            GUI.backgroundColor = Color.white;

            if (_win.Button(new Rect(490, ry, 145, 24), "Revert"))
                _c.RevertLevelEdit(r);
        }

        // ────────────────────────────────────────────────────────────── MAP ──

        private void DrawMapTab()
        {
            float y = ContentY;
            _nameField ??= _c.MapName;

            GUI.Label(new Rect(15, y + 3, 60, 20), "Name:", _styleSmall);
            _nameField = _win.TextField(new Rect(80, y, 300, 24), "mapname", _nameField);
            y += 32;

            bool canOverwrite = !string.IsNullOrEmpty(_c.CurrentFileName);
            GUI.backgroundColor = canOverwrite ? new Color(0.3f, 0.8f, 0.4f) : new Color(0.4f, 0.4f, 0.4f);
            if (_win.Button(new Rect(15, y, 180, 30), canOverwrite ? "SAVE (overwrite)" : "SAVE (saves as new)"))
            {
                _c.MapName = _nameField.Trim().Length > 0 ? _nameField.Trim() : _c.MapName;
                if (canOverwrite) _c.SaveOverwrite();
                else _c.SaveAsNew(_nameField);
                _nameField = _c.MapName;
            }
            GUI.backgroundColor = new Color(0.3f, 0.6f, 0.9f);
            if (_win.Button(new Rect(205, y, 180, 30), "SAVE AS NEW"))
            {
                _c.SaveAsNew(_nameField);
                _nameField = _c.MapName;
            }
            GUI.backgroundColor = Color.white;
            y += 40;

            if (_win.Button(new Rect(15, y, 220, 26), $"Open Maps Hub ({EditorConfig.Settings.MapsHubKey})"))
                _c.ToggleMapsHub();
            y += 36;

            string file = _c.CurrentFileName != null ? _c.CurrentFileName + MapSerializer.EXTENSION : "(not saved yet)";
            GUI.Label(new Rect(15, y, W - 30, 20), $"File: {file}", _styleSmall);
            y += 22;
            GUI.Label(new Rect(15, y, W - 30, 20), $"Folder: BepInEx\\plugins\\FIHMapEditor\\Maps", _styleSmall);
            y += 22;
            string autosave = _c.LastAutosaveTime > 0
                ? $"autosaved {Time.unscaledTime - _c.LastAutosaveTime:0}s ago"
                : "no autosave yet";
            GUI.Label(new Rect(15, y, W - 30, 20),
                $"{(_c.Dirty ? "● unsaved changes" : "✓ no pending changes")}  |  {autosave}", _styleSmall);
            y += 30;

            GUI.Label(new Rect(15, y, W - 30, 60),
                "Maps are .fihmap.json files — share them by copying the file.\n" +
                "To play someone else's map: copy it into the Maps folder and load it\n" +
                "from the Maps Hub.", _styleSmall);
        }

        // ───────────────────────────────────────────────────────────── KEYS ──

        private static readonly (string id, string label)[] BindRows =
        {
            ("editor",  "Open / close editor"),
            ("cursor",  "Free cursor / camera"),
            ("hub",     "Maps Hub"),
            ("play",    "Editor ↔ Play mode"),
            ("restart", "Restart run (in Play)"),
            ("undo",    "Undo (Ctrl + key)"),
            ("save",    "Save map (Ctrl + key)"),
        };

        // Ctrl-combo binds can share letters with plain binds without conflicting.
        private static bool IsCtrlBind(string id) => id == "undo" || id == "save";

        private static KeyCode GetBind(string id) => id switch
        {
            "editor" => EditorConfig.Settings.ToggleEditorKey,
            "cursor" => EditorConfig.Settings.ToggleCursorKey,
            "hub" => EditorConfig.Settings.MapsHubKey,
            "play" => EditorConfig.Settings.TogglePlayKey,
            "restart" => EditorConfig.Settings.RestartRunKey,
            "undo" => EditorConfig.Settings.UndoKey,
            "save" => EditorConfig.Settings.SaveKey,
            _ => KeyCode.None,
        };

        private static void SetBind(string id, KeyCode key)
        {
            var s = EditorConfig.Settings;
            switch (id)
            {
                case "editor": s.ToggleEditorKey = key; break;
                case "cursor": s.ToggleCursorKey = key; break;
                case "hub": s.MapsHubKey = key; break;
                case "play": s.TogglePlayKey = key; break;
                case "restart": s.RestartRunKey = key; break;
                case "undo": s.UndoKey = key; break;
                case "save": s.SaveKey = key; break;
            }
            EditorConfig.Save();
        }

        private void DrawKeysTab()
        {
            float y = ContentY;

            GUI.Label(new Rect(15, y, 400, 22), "Configurable keys:", _styleTitle);
            y += 28;

            HandleBindCapture();

            foreach (var (bindId, label) in BindRows)
            {
                GUI.Label(new Rect(20, y + 4, 230, 22), label, _styleRow);

                bool listening = _listeningBind == bindId;
                if (listening) GUI.backgroundColor = new Color(0.3f, 0.8f, 0.4f);
                string text = listening ? "[ press a key — Esc cancels ]"
                    : IsCtrlBind(bindId) ? $"[ Ctrl + {GetBind(bindId)} ]"
                    : $"[ {GetBind(bindId)} ]";
                if (_win.Button(new Rect(260, y, 280, 26), text))
                    _listeningBind = listening ? null : bindId;
                GUI.backgroundColor = Color.white;

                y += 32;
            }

            y += 10;
            if (_win.Button(new Rect(15, y, 180, 28), "Reset Defaults"))
            {
                EditorConfig.ResetToDefaults();
                _listeningBind = null;
                _c.ShowToast("Keys reset to defaults");
            }
            y += 40;

            GUI.Label(new Rect(15, y, W - 30, 60),
                "Click a key to change it, then press the new one. Saved instantly.\n" +
                "Edit keys (arrows, PgUp/PgDn, [ ], +/-, Del, Ctrl+D) are fixed.",
                _styleSmall);
        }

        private void HandleBindCapture()
        {
            if (_listeningBind == null) return;

            var e = Event.current;
            if (e.type != EventType.KeyDown || e.keyCode == KeyCode.None) return;

            if (e.keyCode == KeyCode.Escape)
            {
                _listeningBind = null;
                e.Use();
                return;
            }

            // Ctrl+D is the fixed duplicate shortcut: keep it off-limits for Ctrl-combos.
            if (IsCtrlBind(_listeningBind) && e.keyCode == KeyCode.D)
            {
                _c.ShowToast("Ctrl+D is reserved for Duplicate");
                e.Use();
                return;
            }

            // Refuse keys already used by another action of the same kind — a Ctrl-combo
            // and a plain key can share the same letter without conflict.
            foreach (var (otherId, otherLabel) in BindRows)
            {
                if (otherId != _listeningBind && GetBind(otherId) == e.keyCode
                    && IsCtrlBind(otherId) == IsCtrlBind(_listeningBind))
                {
                    _c.ShowToast($"'{e.keyCode}' is already bound to: {otherLabel}");
                    e.Use();
                    return;
                }
            }

            SetBind(_listeningBind, e.keyCode);
            _c.ShowToast($"Bound: {e.keyCode}");
            _listeningBind = null;
            e.Use();
        }

        // ──────────────────────────────────────────────────────────── footer ──

        private void DrawFooter()
        {
            float y = H - 30;
            GUI.Box(new Rect(0, y - 4, W, 34), "");
            string dirty = _c.Dirty ? " ●" : "";
            GUI.Label(new Rect(15, y, W - 30, 22),
                $"Map: \"{Truncate(_c.MapName, 24)}\"{dirty}   Objects: {_c.PlacedManager.Count}   Base: {_c.BaseMode}" +
                (_c.LoadReport != "" ? $"   [{_c.LoadReport}]" : ""),
                _styleRow);
        }

        private static string FmtVec(float[] v) => v == null ? "?" : $"({v[0]:0.#}, {v[1]:0.#}, {v[2]:0.#})";

        // Accepts both "1.5" and "1,5"; falls back when the box is empty or mid-edit.
        private static float ParseFloat(string s, float fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            return float.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
