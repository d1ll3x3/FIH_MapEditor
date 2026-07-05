using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FIHMapEditor
{
    public enum EditorMode { Off, Editor, Play }

    // Central orchestrator: mode state machine, scene tracking, hotkeys, input context
    // switching, map lifecycle (working map, autosave, re-apply after scene reload) and
    // every edit operation the GUI exposes.
    public class EditorController
    {
        public EditorMode Mode { get; private set; } = EditorMode.Off;
        public bool CursorFree { get; private set; }
        public bool InGameScene { get; private set; }

        // Working map state (the in-memory truth while editing)
        public string MapName = "Untitled";
        public string CurrentFileName;              // null until saved / loaded
        public MapBaseMode BaseMode { get; private set; } = MapBaseMode.Overlay;
        public SpawnPointData Spawn;
        public GoalZoneData Goal;
        public bool Dirty { get; private set; }
        public float LastAutosaveTime { get; private set; } = -1f;
        public string LoadReport = "";

        // Edit options driven by the menu
        public int MoveSpeedMultiplier = 1;         // 1 / 4 / 10
        public float RotateStepDegrees = 45f;       // used by menu buttons and [ ] hotkeys
        public float ScaleStep = 0.5f;              // used by menu buttons and +/- hotkeys
        public bool UnlockOriginals = false;
        public CatalogEntry StampEntry;             // active stamp-mode catalog entry
        public bool MousePlaceEnabled = false;      // master switch: world clicks place stamps

        // Systems
        public GameObjectFinder Finder { get; private set; }
        public InputHandler Input { get; private set; }
        public FlyController Fly { get; private set; }
        public ObjectCatalog Catalog { get; private set; }
        public PlacedObjectManager PlacedManager { get; private set; }
        public SelectionSystem SelectionSys { get; private set; }
        public HighlightRenderer Highlight { get; private set; }
        public PlayModeController PlayMode { get; private set; }
        public BlankCanvasController BlankCanvas { get; private set; }

        // GUI
        private EditorMenuRenderer _menu;
        private MapsHubRenderer _mapsHub;
        private HudRenderer _hud;

        private readonly LineBox _goalBox = new LineBox("FIH_Line_Goal");
        private readonly LineBox _spawnBox = new LineBox("FIH_Line_Spawn");

        // Scene / lifecycle tracking
        private string _lastSceneName = "";
        private MapFile _workingSnapshot;           // refreshed on every mutation; survives scene reloads
        private bool _pendingReapply = false;
        private float _reapplyRetryAt = 0f;

        // Focus gating (Steam overlay etc.)
        private bool _wasFocused = true;
        private float _inputGraceUntil = 0f;
        private const float FOCUS_INPUT_GRACE = 0.3f;

        private float _autosaveDirtySince = -1f;

        // Toasts
        private string _toastMessage = "";
        private float _toastUntil = 0f;
        public string ToastMessage => Time.unscaledTime < _toastUntil ? _toastMessage : "";

        private bool _devicesDisabled = false;

        public void Initialize()
        {
            EditorConfig.Load();

            Finder = new GameObjectFinder();
            Input = new InputHandler();
            Fly = new FlyController(Finder, Input);
            Catalog = new ObjectCatalog(Finder);
            PlacedManager = new PlacedObjectManager();
            SelectionSys = new SelectionSystem(Finder, PlacedManager, Catalog);
            Highlight = new HighlightRenderer();
            PlayMode = new PlayModeController(Finder);
            BlankCanvas = new BlankCanvasController(Finder);

            _menu = new EditorMenuRenderer(this);
            _mapsHub = new MapsHubRenderer(this);
            _hud = new HudRenderer(this);

            MapEditorPlugin.Logger.LogInfo("EditorController initialized");
        }

        public void ShowToast(string message, float seconds = 3f)
        {
            _toastMessage = message;
            _toastUntil = Time.unscaledTime + seconds;
        }

        public bool MapsHubOpen => _mapsHub.Visible;

        public void ToggleMapsHub()
        {
            _mapsHub.Toggle();
            if (_mapsHub.Visible && !CursorFree) SetCursorFree(true);
        }

        // ────────────────────────────────────────────────────────── update loop ──

        public void Update()
        {
            try
            {
                TrackScene();
                if (!InGameScene) return;

                HandlePendingReapply();

                // Focus gate: no hotkeys while the window is unfocused + short grace after.
                bool focused = Application.isFocused;
                if (focused && !_wasFocused)
                {
                    _inputGraceUntil = Time.unscaledTime + FOCUS_INPUT_GRACE;
                    Input.ResetEdges();
                }
                _wasFocused = focused;
                bool acceptInput = focused && Time.unscaledTime >= _inputGraceUntil;

                    // While the KEYS tab is capturing a bind, every key belongs to the capture.
                if (acceptInput && !_menu.IsCapturingBind)
                    HandleGlobalHotkeys();

                switch (Mode)
                {
                    case EditorMode.Editor:
                        UpdateEditorMode(acceptInput);
                        break;
                    case EditorMode.Play:
                        PlayMode.Update();
                        if (acceptInput && Input.WasKeyPressed("restart", EditorConfig.Settings.RestartRunKey))
                            PlayMode.RestartRun();
                        break;
                }

                UpdateAutosave();
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"Error in EditorController.Update: {ex}");
            }
        }

        public void OnGUI()
        {
            try
            {
                if (!InGameScene) return;

                if (Mode == EditorMode.Editor && CursorFree)
                {
                    // The game re-locks the cursor aggressively; force it free every pass.
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }

                _hud.Draw();
                if (Mode == EditorMode.Editor)
                {
                    _menu.Draw();
                    _mapsHub.Draw();
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"Error in EditorController.OnGUI: {ex}");
            }
        }

        private void TrackScene()
        {
            string sceneName = SceneManager.GetActiveScene().name ?? "";
            if (sceneName != _lastSceneName)
            {
                bool wasInGame = InGameScene;
                _lastSceneName = sceneName;
                InGameScene = sceneName.StartsWith("Scene_Game");

                if (wasInGame)
                {
                    // Level unloaded (restart or back to menu): clones and disabled-component
                    // lists are gone. Snapshot already holds the map; re-apply when back.
                    OnLeftGameScene();
                }
                if (InGameScene && HasWorkingContent())
                {
                    _pendingReapply = true;
                    _reapplyRetryAt = Time.time + 2f;
                }
                MapEditorPlugin.Logger.LogInfo($"[SCENE] Active scene: {sceneName} (game={InGameScene})");
            }
        }

        private void OnLeftGameScene()
        {
            if (CursorFree) SetCursorFree(false);
            Mode = EditorMode.Off;
            PlayMode.Exit();
            Finder.ClearCache();
            PlacedManager.OnSceneChanged();
            BlankCanvas.OnSceneChanged();
            Catalog.Clear();
            SelectionSys.Deselect();
            Highlight.Hide();
            _goalBox.Hide();
            _spawnBox.Hide();
        }

        private bool HasWorkingContent()
        {
            return _workingSnapshot != null &&
                   (_workingSnapshot.Objects.Count > 0 || _workingSnapshot.Spawn != null
                    || _workingSnapshot.Goal != null || _workingSnapshot.BaseMode == MapBaseMode.Blank);
        }

        private void HandlePendingReapply()
        {
            if (!_pendingReapply || Time.time < _reapplyRetryAt) return;

            if (Finder.FindPlayer() == null || Finder.FindCamera() == null)
            {
                _reapplyRetryAt = Time.time + 1f;
                return;
            }

            _pendingReapply = false;
            if (_workingSnapshot != null)
            {
                ApplyMapFile(_workingSnapshot, resetDirty: false);
                ShowToast($"Map \"{MapName}\" re-applied after scene reload");
            }
        }

        private void HandleGlobalHotkeys()
        {
            var s = EditorConfig.Settings;

            if (Input.WasKeyPressed("toggleEditor", s.ToggleEditorKey))
            {
                if (Mode == EditorMode.Editor) SetMode(EditorMode.Off);
                else if (Mode == EditorMode.Off) SetMode(EditorMode.Editor);
                // In Play mode F6 is ignored; use P to go back to the editor first.
            }

            if (Input.WasKeyPressed("togglePlay", s.TogglePlayKey))
            {
                if (Mode == EditorMode.Editor && !AnyTextFieldFocused()) SetMode(EditorMode.Play);
                else if (Mode == EditorMode.Play) SetMode(EditorMode.Editor);
            }

            if (Mode == EditorMode.Editor && Input.WasKeyPressed("mapsHub", s.MapsHubKey))
                ToggleMapsHub();
        }

        private void UpdateEditorMode(bool acceptInput)
        {
            var flyCheatFinderGuard = Finder.GetFlyCheat();
            if (flyCheatFinderGuard != null && flyCheatFinderGuard.enabled)
                flyCheatFinderGuard.enabled = false; // editor owns the player body

            if (acceptInput && Input.WasKeyPressed("toggleCursor", EditorConfig.Settings.ToggleCursorKey)
                && !AnyTextFieldFocused())
            {
                SetCursorFree(!CursorFree);
            }

            if (acceptInput && (!CursorFree || !AnyTextFieldFocused()))
                Fly.Move();

            if (acceptInput && CursorFree)
            {
                HandleWorldClick();
                if (!AnyTextFieldFocused() && !_menu.IsCapturingBind)
                    HandleEditKeys();
            }

            Highlight.UpdateHighlight(SelectionSys.Current);
            UpdateEditorMarkers();
        }

        private void UpdateEditorMarkers()
        {
            if (Goal?.Center != null)
                _goalBox.ShowBox(new Bounds(VecUtil.ToVector3(Goal.Center),
                                            VecUtil.ToVector3(Goal.Size, Vector3.one * 3f)),
                                 new Color(0.3f, 1f, 0.5f, 0.9f));
            else
                _goalBox.Hide();

            if (Spawn?.Pos != null)
                _spawnBox.ShowBox(new Bounds(VecUtil.ToVector3(Spawn.Pos) + Vector3.up * 1f,
                                             new Vector3(1f, 2f, 1f)),
                                  new Color(0.4f, 0.6f, 1f, 0.9f));
            else
                _spawnBox.Hide();
        }

        private bool AnyTextFieldFocused() => _menu.HasFocusedTextField || _mapsHub.HasFocusedTextField;

        private void HandleWorldClick()
        {
            if (!UnityEngine.Input.GetMouseButtonDown(0)) return;
            // Ask the windows directly whether the mouse is on them — clicks on the menu
            // must never reach the world.
            if (_menu.ContainsMouse() || _mapsHub.ContainsMouse()) return;

            bool hit = SelectionSys.PickAtMouse(UnlockOriginals, out Vector3 hitPoint);

            // Stamp placement only when the master mouse-place switch is on.
            if (MousePlaceEnabled && StampEntry != null && hit)
            {
                PlaceEntryAt(StampEntry, hitPoint);
            }
        }

        private void HandleEditKeys()
        {
            var sel = SelectionSys.Current;

            if (Input.IsCtrlHeld() && Input.WasKeyPressed("dup", KeyCode.D))
            {
                DuplicateSelected();
                return;
            }

            if (!sel.IsValid) return;

            float step = EditorConfig.Settings.NudgeStep * MoveSpeedMultiplier;

            // Camera-relative nudge snapped to world axes so the arrows always mean
            // "away from me / towards me / left / right" as seen on screen.
            Vector3 fwd = CameraAxisForward();
            Vector3 right = Quaternion.Euler(0, 90, 0) * fwd;

            if (Input.WasKeyPressed("nUp", KeyCode.UpArrow)) MoveSelected(fwd * step);
            if (Input.WasKeyPressed("nDown", KeyCode.DownArrow)) MoveSelected(-fwd * step);
            if (Input.WasKeyPressed("nLeft", KeyCode.LeftArrow)) MoveSelected(-right * step);
            if (Input.WasKeyPressed("nRight", KeyCode.RightArrow)) MoveSelected(right * step);
            if (Input.WasKeyPressed("nPgUp", KeyCode.PageUp)) MoveSelected(Vector3.up * step);
            if (Input.WasKeyPressed("nPgDn", KeyCode.PageDown)) MoveSelected(Vector3.down * step);

            if (Input.WasKeyPressed("rotL", KeyCode.LeftBracket)) RotateSelectedY(-RotateStepDegrees);
            if (Input.WasKeyPressed("rotR", KeyCode.RightBracket)) RotateSelectedY(RotateStepDegrees);

            if (Input.WasKeyPressed("scUp", KeyCode.KeypadPlus) || Input.WasKeyPressed("scUp2", KeyCode.Equals))
                ScaleSelected(ScaleStep);
            if (Input.WasKeyPressed("scDn", KeyCode.KeypadMinus) || Input.WasKeyPressed("scDn2", KeyCode.Minus))
                ScaleSelected(-ScaleStep);

            if (Input.WasKeyPressed("del", KeyCode.Delete)) DeleteSelected();
        }

        private Vector3 CameraAxisForward()
        {
            var cam = Finder.FindCameraTransform();
            Vector3 fwd = cam != null ? cam.forward : Vector3.forward;
            fwd.y = 0;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            fwd.Normalize();
            // Snap to the nearest world axis
            return Mathf.Abs(fwd.x) > Mathf.Abs(fwd.z)
                ? new Vector3(Mathf.Sign(fwd.x), 0, 0)
                : new Vector3(0, 0, Mathf.Sign(fwd.z));
        }

        // ─────────────────────────────────────────────────────── mode switching ──

        public void SetMode(EditorMode newMode)
        {
            if (newMode == Mode) return;
            var old = Mode;
            Mode = newMode;

            try
            {
                // Leave old mode
                if (old == EditorMode.Editor)
                {
                    if (CursorFree) SetCursorFree(false);
                    _mapsHub.Close();
                    Fly.Exit();
                    Highlight.Hide();
                    _goalBox.Hide();
                    _spawnBox.Hide();
                }
                else if (old == EditorMode.Play)
                {
                    PlayMode.Exit();
                }

                // Enter new mode
                if (newMode == EditorMode.Editor)
                {
                    if (!Catalog.HasScanned) Catalog.Scan();
                    if (_workingSnapshot == null) NewMap(silent: true);
                    Fly.Enter();
                    var k = EditorConfig.Settings;
                    ShowToast($"EDITOR — {k.ToggleCursorKey}: free cursor, {k.MapsHubKey}: maps, {k.TogglePlayKey}: Play mode, {k.ToggleEditorKey}: close");
                }
                else if (newMode == EditorMode.Play)
                {
                    RefreshSnapshot();
                    PlayMode.Enter(Spawn, Goal, BaseMode == MapBaseMode.Blank, MapName);
                    ShowToast($"PLAY — {EditorConfig.Settings.RestartRunKey}: restart run, {EditorConfig.Settings.TogglePlayKey}: back to editor");
                }

                MapEditorPlugin.Logger.LogInfo($"[MODE] {old} → {newMode}");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[MODE] Error switching mode: {ex}");
            }
        }

        public void SetCursorFree(bool free)
        {
            CursorFree = free;
            try
            {
                if (free)
                {
                    if (!_devicesDisabled)
                    {
                        // Kill the game's input (camera look, pause menu) while we use the
                        // mouse; our own UI reads legacy Input, which keeps working.
                        if (UnityEngine.InputSystem.Keyboard.current != null)
                            UnityEngine.InputSystem.InputSystem.DisableDevice(UnityEngine.InputSystem.Keyboard.current);
                        if (UnityEngine.InputSystem.Mouse.current != null)
                            UnityEngine.InputSystem.InputSystem.DisableDevice(UnityEngine.InputSystem.Mouse.current);
                        _devicesDisabled = true;
                    }
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                else
                {
                    if (_devicesDisabled)
                    {
                        if (UnityEngine.InputSystem.Keyboard.current != null)
                            UnityEngine.InputSystem.InputSystem.EnableDevice(UnityEngine.InputSystem.Keyboard.current);
                        if (UnityEngine.InputSystem.Mouse.current != null)
                            UnityEngine.InputSystem.InputSystem.EnableDevice(UnityEngine.InputSystem.Mouse.current);
                        _devicesDisabled = false;
                    }
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                    Input.ResetEdges();
                    _menu.UnfocusFields();
                    _mapsHub.UnfocusFields();
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[CURSOR] Error toggling cursor: {ex.Message}");
            }
            MapEditorPlugin.Logger.LogInfo($"[CURSOR] free={free}");
        }

        // ───────────────────────────────────────────────────────── map lifecycle ──

        public void SetDirty()
        {
            Dirty = true;
            if (_autosaveDirtySince < 0) _autosaveDirtySince = Time.unscaledTime;
            RefreshSnapshot();
        }

        private void RefreshSnapshot()
        {
            _workingSnapshot = BuildMapFile();
        }

        public MapFile BuildMapFile()
        {
            return new MapFile
            {
                FormatVersion = MapFile.CURRENT_FORMAT_VERSION,
                Name = MapName,
                BaseMode = BaseMode,
                GameScene = _lastSceneName,
                Spawn = Spawn,
                Goal = Goal,
                Objects = PlacedManager.Snapshot(),
            };
        }

        private void UpdateAutosave()
        {
            if (!Dirty || _autosaveDirtySince < 0) return;
            if (Time.unscaledTime - _autosaveDirtySince < EditorConfig.Settings.AutosaveIntervalSeconds) return;

            try
            {
                RefreshSnapshot();
                MapSerializer.Save(_workingSnapshot, MapSerializer.AUTOSAVE_NAME);
                LastAutosaveTime = Time.unscaledTime;
                _autosaveDirtySince = Time.unscaledTime; // keep autosaving while still dirty
                MapEditorPlugin.Logger.LogInfo("[MAP] Autosaved.");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[MAP] Autosave error: {ex.Message}");
            }
        }

        public void NewMap(bool silent = false)
        {
            PlacedManager.WipeAll();
            SelectionSys.Deselect();
            BlankCanvas.Restore();
            MapName = "Untitled";
            CurrentFileName = null;
            BaseMode = MapBaseMode.Overlay;
            Spawn = null;
            Goal = null;
            Dirty = false;
            _autosaveDirtySince = -1f;
            LoadReport = "";
            RefreshSnapshot();
            if (!silent) ShowToast("New empty map");
        }

        public bool SaveOverwrite()
        {
            if (string.IsNullOrEmpty(CurrentFileName)) return false;
            try
            {
                RefreshSnapshot();
                MapSerializer.Save(_workingSnapshot, CurrentFileName);
                Dirty = false;
                _autosaveDirtySince = -1f;
                ShowToast($"Saved \"{MapName}\" ({PlacedManager.Count} objects)");
                return true;
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[MAP] Save error: {ex}");
                ShowToast($"ERROR saving: {ex.Message}");
                return false;
            }
        }

        public bool SaveAsNew(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = MapName;
            MapName = name.Trim();
            string fileName = MapSerializer.SanitizeFileName(MapName);

            // Never silently overwrite a different existing map: suffix until unique.
            if (MapSerializer.Exists(fileName) && fileName != CurrentFileName)
            {
                int i = 2;
                while (MapSerializer.Exists($"{fileName}_{i}")) i++;
                fileName = $"{fileName}_{i}";
            }

            CurrentFileName = fileName;
            return SaveOverwrite();
        }

        public void LoadMap(string fileName)
        {
            try
            {
                var map = MapSerializer.Load(fileName);
                ApplyMapFile(map, resetDirty: true);
                CurrentFileName = fileName == MapSerializer.AUTOSAVE_NAME ? null : fileName;
                ShowToast($"Loaded \"{MapName}\" — {LoadReport}");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[MAP] Load error: {ex}");
                ShowToast($"ERROR loading \"{fileName}\": {ex.Message}");
            }
        }

        private void ApplyMapFile(MapFile map, bool resetDirty)
        {
            if (!Catalog.HasScanned) Catalog.Scan();

            PlacedManager.WipeAll();
            SelectionSys.Deselect();

            MapName = map.Name ?? "Untitled";
            Spawn = map.Spawn;
            Goal = map.Goal;
            BaseMode = map.BaseMode;

            if (BaseMode == MapBaseMode.Blank) BlankCanvas.Apply();
            else BlankCanvas.Restore();

            int loaded = 0, skipped = 0;
            foreach (var obj in map.Objects)
            {
                var source = Catalog.ResolveSource(obj.Source, obj.SourceName);
                if (source == null)
                {
                    skipped++;
                    MapEditorPlugin.Logger.LogWarning($"[MAP] Source not found for '{obj.SourceName}' ({obj.Source})");
                    continue;
                }
                var placed = PlacedManager.Spawn(source, obj.Source, obj.SourceName,
                    VecUtil.ToVector3(obj.Pos),
                    Quaternion.Euler(VecUtil.ToVector3(obj.Rot)),
                    VecUtil.ToVector3(obj.Scale, Vector3.one),
                    obj.Tint);
                if (placed != null) loaded++;
                else skipped++;
            }

            LoadReport = skipped == 0 ? $"{loaded} objects" : $"{loaded} objects, {skipped} skipped";
            if (resetDirty)
            {
                Dirty = false;
                _autosaveDirtySince = -1f;
            }
            RefreshSnapshot();
            MapEditorPlugin.Logger.LogInfo($"[MAP] Applied '{MapName}': {LoadReport}");
        }

        // ──────────────────────────────────────────────────────── edit operations ──

        public void PlaceEntry(CatalogEntry entry)
        {
            var cam = Finder.FindCameraTransform();
            Vector3 pos = cam != null ? cam.position + cam.forward * 8f : Vector3.zero;
            PlaceEntryAt(entry, pos, floatingPlacement: true);
        }

        public void PlaceEntryAt(CatalogEntry entry, Vector3 point, bool floatingPlacement = false)
        {
            var source = Catalog.GetLiveSource(entry);
            if (source == null)
            {
                ShowToast($"Source object \"{entry.DisplayName}\" not found");
                return;
            }

            var placed = PlacedManager.Spawn(source, entry.SourcePath, entry.DisplayName,
                point, source.transform.rotation, source.transform.localScale, TintColor.None);
            if (placed == null)
            {
                ShowToast($"Failed to clone \"{entry.DisplayName}\"");
                return;
            }

            if (!floatingPlacement)
            {
                // Stamp: sit the object's bounds bottom on the clicked point.
                var bounds = ObjectCatalog.ComputeBounds(placed.Root);
                if (bounds.size != Vector3.zero)
                    placed.Root.transform.position += Vector3.up * (point.y - bounds.min.y);
            }

            SelectionSys.Select(placed);
            SetDirty();
        }

        public void DuplicateSelected()
        {
            var sel = SelectionSys.Current;
            if (!sel.IsValid) return;

            string sourcePath;
            string sourceName;
            GameObject source;
            Vector3 scale;
            TintColor tint = TintColor.None;

            if (sel.IsPlaced)
            {
                // Clone the clone: same source identity, current transform.
                source = sel.Placed.Root;
                sourcePath = sel.Placed.SourcePath;
                sourceName = sel.Placed.SourceName;
                scale = sel.Placed.Root.transform.localScale;
                tint = sel.Placed.Tint;
            }
            else
            {
                source = sel.Raw;
                sourcePath = ObjectCatalog.BuildPath(sel.Raw.transform);
                sourceName = sel.Raw.name;
                scale = sel.Raw.transform.localScale;
            }

            var t = sel.Target.transform;
            Vector3 offset = Vector3.up * 0.5f + CameraAxisForward() * 1.5f;
            var placed = PlacedManager.Spawn(source, sourcePath, sourceName,
                t.position + offset, t.rotation, scale, tint);
            if (placed != null)
            {
                SelectionSys.Select(placed);
                SetDirty();
                ShowToast($"Duplicated: {placed.Root.name}");
            }
        }

        // Only placed clones can be mutated; originals are select/duplicate-only.
        private bool RequirePlaced()
        {
            var sel = SelectionSys.Current;
            if (!sel.IsValid) return false;
            if (!sel.IsPlaced)
            {
                ShowToast("Original level object: use Duplicate to create an editable copy");
                return false;
            }
            return true;
        }

        public void MoveSelected(Vector3 delta)
        {
            if (!RequirePlaced()) return;
            SelectionSys.Current.Placed.Root.transform.position += delta;
            SetDirty();
        }

        public void RotateSelectedY(float degrees)
        {
            if (!RequirePlaced()) return;
            SelectionSys.Current.Placed.Root.transform.Rotate(0f, degrees, 0f, Space.World);
            SetDirty();
        }

        public void RotateSelected(Vector3 axis, float degrees)
        {
            if (!RequirePlaced()) return;
            SelectionSys.Current.Placed.Root.transform.Rotate(axis, degrees, Space.World);
            SetDirty();
        }

        public void ScaleSelected(float delta)
        {
            if (!RequirePlaced()) return;
            var t = SelectionSys.Current.Placed.Root.transform;
            var s = t.localScale + Vector3.one * delta;
            s.x = Mathf.Max(0.05f, s.x);
            s.y = Mathf.Max(0.05f, s.y);
            s.z = Mathf.Max(0.05f, s.z);
            t.localScale = s;
            SetDirty();
        }

        // Absolute scale: multiplier over the object's original scale, so 1 = original
        // size and 2 = twice as big regardless of previous tweaking.
        public void SetScaleFactorSelected(float factor)
        {
            if (!RequirePlaced()) return;
            factor = Mathf.Max(0.05f, factor);
            var placed = SelectionSys.Current.Placed;
            placed.Root.transform.localScale = placed.OriginalScale * factor;
            SetDirty();
        }

        public void ResetSelected()
        {
            if (!RequirePlaced()) return;
            var placed = SelectionSys.Current.Placed;
            var t = placed.Root.transform;
            t.rotation = Quaternion.identity;
            t.localScale = placed.OriginalScale;
            SetDirty();
        }

        public void AlignSelected()
        {
            if (!RequirePlaced()) return;
            var t = SelectionSys.Current.Placed.Root.transform;
            var p = t.position;
            t.position = new Vector3(Mathf.Round(p.x * 2f) / 2f, Mathf.Round(p.y * 2f) / 2f, Mathf.Round(p.z * 2f) / 2f);
            var e = t.eulerAngles;
            t.eulerAngles = new Vector3(Mathf.Round(e.x / 15f) * 15f, Mathf.Round(e.y / 15f) * 15f, Mathf.Round(e.z / 15f) * 15f);
            SetDirty();
        }

        public void DropSelectedToFloor()
        {
            if (!RequirePlaced()) return;
            var root = SelectionSys.Current.Placed.Root;
            var bounds = ObjectCatalog.ComputeBounds(root);
            if (bounds.size == Vector3.zero) return;

            try
            {
                // Cast down from just below our own bounds so we don't hit ourselves.
                Vector3 origin = new Vector3(bounds.center.x, bounds.min.y - 0.01f, bounds.center.z);
                var hits = Physics.RaycastAll(new Ray(origin, Vector3.down), 500f);
                float bestDist = float.MaxValue;
                bool found = false;
                float floorY = 0f;
                foreach (var hit in hits)
                {
                    if (hit.collider == null) continue;
                    if (hit.collider.transform.IsChildOf(root.transform)) continue;
                    if (hit.distance < bestDist)
                    {
                        bestDist = hit.distance;
                        floorY = hit.point.y;
                        found = true;
                    }
                }
                if (!found)
                {
                    ShowToast("No floor below the object");
                    return;
                }
                root.transform.position += Vector3.up * (floorY - bounds.min.y);
                SetDirty();
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[EDIT] DropToFloor error: {ex.Message}");
            }
        }

        public void MultiClone(int count, Vector3 direction, bool stairs)
        {
            var sel = SelectionSys.Current;
            if (!RequirePlaced()) return;
            var placed = sel.Placed;
            var bounds = ObjectCatalog.ComputeBounds(placed.Root);
            if (bounds.size == Vector3.zero) return;

            float stepAlong = Mathf.Abs(Vector3.Dot(bounds.size, direction));
            if (stepAlong < 0.05f) stepAlong = 1f;
            float stepUp = stairs ? bounds.size.y : 0f;

            var t = placed.Root.transform;
            PlacedObject last = null;
            for (int i = 1; i <= count; i++)
            {
                Vector3 pos = t.position + direction * stepAlong * i + Vector3.up * stepUp * i;
                last = PlacedManager.Spawn(placed.Root, placed.SourcePath, placed.SourceName,
                    pos, t.rotation, t.localScale, placed.Tint);
            }
            if (last != null)
            {
                SelectionSys.Select(last);
                SetDirty();
                ShowToast($"Multi-clone: {count} copies{(stairs ? " as stairs" : "")}");
            }
        }

        public void DeleteSelected()
        {
            if (!RequirePlaced()) return;
            var name = SelectionSys.Current.Placed.Root.name;
            PlacedManager.Delete(SelectionSys.Current.Placed);
            SelectionSys.Deselect();
            SetDirty();
            ShowToast($"Deleted: {name}");
        }

        public void WipeCustomObjects()
        {
            int n = PlacedManager.Count;
            PlacedManager.WipeAll();
            SelectionSys.Deselect();
            SetDirty();
            ShowToast($"Deleted {n} custom objects");
        }

        public void TpPlayerOntoSelected()
        {
            var sel = SelectionSys.Current;
            if (!sel.IsValid) return;
            var bounds = ObjectCatalog.ComputeBounds(sel.Target);
            if (bounds.size == Vector3.zero) return;

            Vector3 pos = bounds.center + Vector3.up * (bounds.extents.y + 1.5f);
            var rb = Finder.GetCachedPlayerRigidbody();
            var t = Finder.FindPlayerTransform();
            if (rb != null) rb.position = pos;
            if (t != null) t.position = pos;
        }

        public void BringSelectedHere()
        {
            if (!RequirePlaced()) return;
            var cam = Finder.FindCameraTransform();
            if (cam == null) return;
            SelectionSys.Current.Placed.Root.transform.position = cam.position + cam.forward * 6f;
            SetDirty();
        }

        // ─────────────────────────────────────────────────────── spawn/goal/base ──

        public void SetSpawnHere()
        {
            var t = Finder.FindPlayerTransform();
            var cam = Finder.FindCameraTransform();
            if (t == null) return;
            Spawn = new SpawnPointData
            {
                Pos = VecUtil.ToArray(t.position),
                Yaw = cam != null ? cam.eulerAngles.y : t.eulerAngles.y,
            };
            SetDirty();
            ShowToast("Map spawn placed here");
        }

        public void ClearSpawn()
        {
            Spawn = null;
            SetDirty();
        }

        public void SetGoalHere()
        {
            var t = Finder.FindPlayerTransform();
            if (t == null) return;
            var size = Goal?.Size ?? new float[] { 4f, 4f, 4f };
            Goal = new GoalZoneData
            {
                Center = VecUtil.ToArray(t.position + Vector3.up * 1f),
                Size = size,
            };
            SetDirty();
            ShowToast("Goal placed here");
        }

        public void ClearGoal()
        {
            Goal = null;
            SetDirty();
        }

        public void AdjustGoalSize(float delta)
        {
            if (Goal == null) return;
            var size = VecUtil.ToVector3(Goal.Size, Vector3.one * 4f) + Vector3.one * delta;
            size.x = Mathf.Max(1f, size.x);
            size.y = Mathf.Max(1f, size.y);
            size.z = Mathf.Max(1f, size.z);
            Goal.Size = VecUtil.ToArray(size);
            SetDirty();
        }

        public void SetBaseMode(MapBaseMode mode)
        {
            if (mode == BaseMode) return;
            BaseMode = mode;

            if (mode == MapBaseMode.Blank)
            {
                BlankCanvas.Apply();
                if (PlacedManager.Count == 0)
                    CreateStartingPlatform();
                ShowToast("Blank canvas: original level hidden");
            }
            else
            {
                BlankCanvas.Restore();
                ShowToast("Overlay mode: original level visible");
            }
            SetDirty();
        }

        private void CreateStartingPlatform()
        {
            // Prefer cloning a real platform from the catalog (guaranteed valid URP material).
            CatalogEntry best = null;
            foreach (var e in Catalog.Entries)
            {
                if (e.Category != "Platforms" && e.Category != "Large") continue;
                if (e.BoundsSize.y > 4f) continue;
                if (e.BoundsSize.x < 3f || e.BoundsSize.z < 3f) continue;
                if (best == null || e.BoundsSize.x * e.BoundsSize.z > best.BoundsSize.x * best.BoundsSize.z)
                    best = e;
            }
            if (best == null && Catalog.Entries.Count > 0)
            {
                // Fallback: flattest object available
                foreach (var e in Catalog.Entries)
                    if (best == null || e.BoundsSize.y < best.BoundsSize.y) best = e;
            }
            if (best == null)
            {
                ShowToast("Catalog empty: could not create starting platform");
                return;
            }

            var playerT = Finder.FindPlayerTransform();
            Vector3 basePos = playerT != null ? playerT.position : Vector3.zero;
            PlaceEntryAt(best, basePos + Vector3.down * 2f, floatingPlacement: true);

            var placed = SelectionSys.Current.Placed;
            if (placed != null)
            {
                var bounds = ObjectCatalog.ComputeBounds(placed.Root);
                Vector3 top = bounds.center + Vector3.up * (bounds.extents.y + 1.5f);
                var rb = Finder.GetCachedPlayerRigidbody();
                var t = Finder.FindPlayerTransform();
                if (rb != null) rb.position = top;
                if (t != null) t.position = top;

                Spawn = new SpawnPointData { Pos = VecUtil.ToArray(top), Yaw = 0f };
                SetDirty();
            }
        }
    }
}
