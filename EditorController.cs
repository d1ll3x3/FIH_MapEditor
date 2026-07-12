using System;
using System.Collections.Generic;
using Steamworks;
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
        public string MapId;                        // stable leaderboard key (see MapFile.MapId)
        public bool Editable = true;                // false = play-only when others load it
        public string AuthorName;                   // stamped on upload
        public long AuthorSteamId;
        public string CurrentFileName;              // null until saved / loaded
        public MapBaseMode BaseMode { get; private set; } = MapBaseMode.Overlay;
        public SpawnPointData Spawn;
        public GoalZoneData Goal;
        public List<CheckpointData> Checkpoints = new List<CheckpointData>();
        public List<ResetZoneData> ResetZones = new List<ResetZoneData>();
        public BallData Ball;                       // soccer: one kickoff/centre point
        public List<SoccerGoalData> SoccerGoals = new List<SoccerGoalData>();
        public ScoreboardData Scoreboard;           // soccer: placeable 3D score display
        public bool Dirty { get; private set; }
        public float LastAutosaveTime { get; private set; } = -1f;
        public string LoadReport = "";

        // Edit options driven by the menu
        public int MoveSpeedMultiplier = 1;         // 1 / 4 / 10
        public float RotateStepDegrees = 45f;       // used by menu buttons and [ ] hotkeys
        public float ScaleStep = 0.5f;              // used by menu buttons and +/- hotkeys
        public bool UnlockOriginals = false;
        public bool PickInvisible = false;          // clicks also hit triggers / invisible colliders
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
        public GizmoController Gizmo { get; private set; }
        public InvisibleVisualizer Xray { get; private set; }
        public PlayModeController PlayMode { get; private set; }
        public BlankCanvasController BlankCanvas { get; private set; }
        public LevelEditManager LevelEdits { get; private set; }
        public UndoSystem Undo { get; private set; }
        public MechanicsController Mechanics { get; private set; }
        public MultiplayerSync Multiplayer { get; private set; }
        public LeaderboardService Leaderboard { get; private set; }
        public OnlineMapService OnlineMaps { get; private set; }

        // Play-only map: the editor is locked when a non-editable online map is loaded.
        public bool ReadOnly { get; private set; }

        // Actions posted from network callback threads to run on the Unity main thread.
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _mainThread
            = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        public void RunOnMainThread(Action a) { if (a != null) _mainThread.Enqueue(a); }

        // In-flight undo capture: filled when a transform edit / marker drag begins,
        // pushed onto the stack when it ends (only if something actually changed).
        private GameObject _pendingUndoGo;
        private bool _pendingUndoRaw;
        private Vector3 _pendingUndoPos;
        private Quaternion _pendingUndoRot;
        private Vector3 _pendingUndoScale;
        private Action _pendingMarkerUndo;

        // GUI
        private EditorMenuRenderer _menu;
        private MapsHubRenderer _mapsHub;
        private HudRenderer _hud;
        private TimesViewerRenderer _timesViewer;   // play-only maps: leaderboard + discard

        private readonly LineBox _goalBox = new LineBox("FIH_Line_Goal");
        private readonly LineBox _spawnBox = new LineBox("FIH_Line_Spawn");

        // Empty proxy transforms the gizmo grabs when a marker (goal/spawn/checkpoint/
        // reset) is selected; data flows proxy→marker while dragging, marker→proxy otherwise.
        private GameObject _goalProxy;
        private GameObject _spawnProxy;
        private GameObject _checkpointProxy;
        private GameObject _resetProxy;
        private GameObject _ballProxy;
        private GameObject _soccerGoalProxy;
        private GameObject _scoreboardProxy;
        private readonly ScoreboardRenderer _scoreboardRenderer = new ScoreboardRenderer();
        private GameObject _cannonTargetProxy;
        private GameObject _cannonLaunchProxy;
        private GameObject _multiProxy;   // group gizmo handle at the selection centroid

        // Multi-drag state: each member's transform at drag start, relative to the centroid.
        private class MultiDragStart
        {
            public GameObject Go;
            public bool Raw;
            public Vector3 Pos;
            public Quaternion Rot;
            public Vector3 Scale;
        }
        private readonly List<MultiDragStart> _multiDragStarts = new List<MultiDragStart>();
        private Vector3 _multiDragCentroid;

        // Editor-mode visuals for the marker lists (rings + red boxes), pooled.
        private readonly List<LineBox> _checkpointBoxes = new List<LineBox>();
        private readonly List<LineBox> _resetBoxes = new List<LineBox>();
        private readonly List<LineBox> _cannonTargetBoxes = new List<LineBox>();
        private readonly List<LineBox> _cannonLines = new List<LineBox>();
        private readonly LineBox _ballRing = new LineBox("FIH_Line_Ball");
        private readonly List<LineBox> _soccerGoalBoxes = new List<LineBox>();
        private readonly List<LineBox> _cannonLaunchBoxes = new List<LineBox>();
        private readonly List<LineBox> _multiBoxes = new List<LineBox>();

        // Objects whose source couldn't be resolved (asset not loaded yet, level object
        // despawned, remote object we can't find locally). NEVER dropped: they stay in
        // every snapshot/save and in the sync state, and are retried periodically —
        // otherwise a transient resolve failure at load/scene-reload would silently
        // bake the loss into the next save, and in co-edit our diff would broadcast a
        // phantom DELETE for an object a peer still has.
        private readonly List<MapObjectData> _unresolvedObjects = new List<MapObjectData>();
        private float _unresolvedRetryAt;

        // The game loads assets/objects lazily, so one scan at editor-open misses
        // things — but a periodic rescan forever stutters the editor for nothing (logs
        // show the catalog stabilizes within the first minute). So: a short WARM-UP of
        // extra scans after the first editor open, then never again automatically.
        // The CATALOG tab re-checks cheaply on open (collider-count change) and the
        // manual Rescan button always works.
        private static readonly float[] WARMUP_SCAN_DELAYS = { 10f, 25f, 60f };
        private int _warmupScansDone;
        private float _warmupBaseTime = -1f;   // Time.time of the first editor open this scene

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
            Gizmo = new GizmoController();
            Xray = new InvisibleVisualizer();
            PlayMode = new PlayModeController(Finder);
            BlankCanvas = new BlankCanvasController(Finder);
            LevelEdits = new LevelEditManager();
            Undo = new UndoSystem();
            Mechanics = new MechanicsController(Finder, PlacedManager, Input, Fly);
            Multiplayer = new MultiplayerSync(this);
            Leaderboard = new LeaderboardService();
            OnlineMaps = new OnlineMapService();

            // Every finished run offers to upload (HudRenderer draws the prompt); veto
            // immediately if there's nothing sensible to upload to.
            PlayMode.OnRunFinished = () =>
            {
                if (!CanUploadTimes) PlayMode.DismissUploadPrompt();
            };
            PlayMode.OnUploadConfirmed = seconds =>
            {
                var (sid, name) = SteamIdentity();
                Leaderboard.SubmitTime(MapId, name, sid, seconds);
                Leaderboard.FetchBoard(MapId, force: true);
            };

            // Soccer ball online: lowest modded SteamID among the players actually in
            // Play on this map simulates; the rest follow. LocalSteamId feeds the
            // instant yield-to-lower-id conflict resolution.
            PlayMode.BallAuthority = () => Multiplayer.IsBallAuthority;
            PlayMode.LocalSteamId = () => Multiplayer.SelfId;
            PlayMode.BallSend = (pos, vel, a, b, kick) => Multiplayer.SendBall(pos, vel, a, b, kick);

            // Clicking level geometry with Unlock OFF explains itself instead of
            // silently doing nothing ("I can't select the grass").
            SelectionSys.OnPickHint = msg => ShowToast(msg);

            _menu = new EditorMenuRenderer(this);
            _mapsHub = new MapsHubRenderer(this);
            _hud = new HudRenderer(this);
            _timesViewer = new TimesViewerRenderer(this);

            MapEditorPlugin.Logger.LogInfo("EditorController initialized");
        }

        // Whether a finished run is even worth offering to upload.
        public bool CanUploadTimes
            => Leaderboard.Configured && EditorConfig.Settings.SubmitTimesOnline && SteamIdentity().steamId != 0;

        // Local Steam identity for leaderboard uploads/highlighting. Falls back to
        // (0, "player") when Steam is unavailable — a 0 id is never uploaded.
        public (long steamId, string name) SteamIdentity()
        {
            try
            {
                long sid = (long)SteamUser.GetSteamID().m_SteamID;
                string name = SteamFriends.GetPersonaName();
                return (sid, string.IsNullOrEmpty(name) ? "player" : name);
            }
            catch
            {
                return (0, "player");
            }
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
            // In the editor the hub rides the normal free-cursor mechanism; outside it
            // (play-only browsing from Off mode) UpdatePlayPromptCursor frees the
            // cursor while the hub is visible.
            if (Mode == EditorMode.Editor && _mapsHub.Visible && !CursorFree) SetCursorFree(true);
        }

        // ────────────────────────────────────────────────────────── update loop ──

        public void Update()
        {
            try
            {
                // Run any work network callbacks handed back to the main thread.
                while (_mainThread.TryDequeue(out var action))
                {
                    try { action(); }
                    catch (Exception ex) { MapEditorPlugin.Logger.LogError($"[MAIN] posted action error: {ex}"); }
                }

                TrackScene();
                if (!InGameScene) return;

                HandlePendingReapply();
                Multiplayer.Update();

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
                        Mechanics.Update(Mode, acceptInput, AnyTextFieldFocused());
                        // The placed 3D scoreboard shows the live match score in play.
                        if (Scoreboard?.Pos != null)
                            _scoreboardRenderer.Draw(Scoreboard, PlayMode.ScoreA, PlayMode.ScoreB);
                        else
                            _scoreboardRenderer.Hide();
                        if (acceptInput && Input.WasKeyPressed("restart", EditorConfig.Settings.RestartRunKey))
                        {
                            // Keyboard Shift ONLY: a held gamepad face button (used by
                            // game actions) must never turn a checkpoint retry into an
                            // accidental full restart ("it sent me back to the start").
                            MapEditorPlugin.Logger.LogInfo("[PLAY] Retry via keyboard");
                            if (Input.IsShiftKeyHeld()) PlayMode.RestartRun();   // full restart
                            else PlayMode.QuickRestart();                        // last coin
                        }
                        // Gamepad: X/Square = retry from last coin, LB+X / L1+Square = full
                        // restart (the trainer's combo pattern).
                        if (acceptInput)
                        {
                            var gp = UnityEngine.InputSystem.Gamepad.current;
                            if (gp != null && gp[UnityEngine.InputSystem.LowLevel.GamepadButton.West].wasPressedThisFrame)
                            {
                                MapEditorPlugin.Logger.LogInfo("[PLAY] Retry via gamepad");
                                if (gp[UnityEngine.InputSystem.LowLevel.GamepadButton.LeftShoulder].isPressed)
                                    PlayMode.RestartRun();
                                else
                                    PlayMode.QuickRestart();
                            }
                        }
                        break;
                }

                UpdatePlayPromptCursor();

                // Keep level edits pinned against game systems that keep resetting them
                // (network sync, pooled visibility). Never fight the user's own drag.
                if (Mode != EditorMode.Off)
                {
                    Transform dragging = Gizmo.IsDragging && SelectionSys.Current.IsRaw
                        ? SelectionSys.Current.Raw.transform
                        : null;
                    LevelEdits.EnforceEdits(dragging);
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
                else if (Mode == EditorMode.Play && PlayMode.UploadPrompt == UploadPromptState.Offered)
                {
                    // The post-run upload prompt needs a visible, clickable cursor too.
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                else if (Mode == EditorMode.Off && (_timesViewer.Visible || _mapsHub.Visible))
                {
                    // Same for the play-only leaderboard window and the maps hub.
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }

                _hud.Draw();
                if (Mode == EditorMode.Editor)
                {
                    _menu.Draw();
                    _mapsHub.Draw();
                }
                else if (Mode == EditorMode.Off)
                {
                    _timesViewer.Draw();
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
            Mechanics.ResetState();
            Multiplayer.OnSceneLeft();
            PlayMode.Exit();
            _timesViewer?.Close();
            UpdatePlayPromptCursor(); // release devices if the upload prompt / viewer had them
            Finder.ClearCache();
            PlayerRepair.Reset();
            GroundRegistrar.Reset();
            _warmupBaseTime = -1f;
            _warmupScansDone = 0;
            PlacedManager.OnSceneChanged();
            BlankCanvas.OnSceneChanged();
            LevelEdits.OnSceneChanged();
            Xray.OnSceneChanged();
            Undo.Clear();
            ClearPendingUndo();
            Catalog.Clear();
            SelectionSys.Deselect();
            Highlight.Hide();
            HideMarkerVisuals();
        }

        private bool HasWorkingContent()
        {
            return _workingSnapshot != null &&
                   (_workingSnapshot.Objects.Count > 0 || _workingSnapshot.Spawn != null
                    || _workingSnapshot.Goal != null || _workingSnapshot.BaseMode == MapBaseMode.Blank
                    || (_workingSnapshot.LevelEdits?.Count ?? 0) > 0
                    || (_workingSnapshot.Checkpoints?.Count ?? 0) > 0
                    || (_workingSnapshot.ResetZones?.Count ?? 0) > 0
                    || _workingSnapshot.Ball != null
                    || (_workingSnapshot.SoccerGoals?.Count ?? 0) > 0
                    || _workingSnapshot.Scoreboard != null);
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
                else if (Mode == EditorMode.Off)
                {
                    // Play-only: the editor key opens the leaderboard window instead —
                    // players can see the times without editor powers, and the window
                    // hosts the "discard map" action (confirmed button).
                    if (ReadOnly) _timesViewer.Toggle();
                    else SetMode(EditorMode.Editor);
                }
                // In Play mode F6 is ignored; use P to leave play first.
            }

            if (Input.WasKeyPressed("togglePlay", s.TogglePlayKey))
            {
                if (Mode == EditorMode.Editor && !AnyTextFieldFocused()) SetMode(EditorMode.Play);
                else if (Mode == EditorMode.Play)
                {
                    // Play-only: P exits to the plain game (Off), never to the editor.
                    if (ReadOnly) { SetMode(EditorMode.Off); ShowToast($"Left play mode — {s.TogglePlayKey}: play again"); }
                    else SetMode(EditorMode.Editor);
                }
                else if (Mode == EditorMode.Off && ReadOnly) SetMode(EditorMode.Play);
            }

            // The Maps hub also works from the plain game (Off): on play-only maps it's
            // the only way to switch to another map without editor powers.
            if ((Mode == EditorMode.Editor || Mode == EditorMode.Off)
                && Input.WasKeyPressed("mapsHub", s.MapsHubKey))
                ToggleMapsHub();

            // Ctrl-combos: work in editor mode whether the cursor is free or locked.
            if (Mode == EditorMode.Editor && !AnyTextFieldFocused() && Input.IsCtrlHeld())
            {
                if (Input.WasKeyPressed("undo", s.UndoKey)) UndoLast();
                if (Input.WasKeyPressed("save", s.SaveKey)) QuickSave();
            }
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

            // While a cannon holds (or test-launches) the player, fly must not move it.
            if (acceptInput && (!CursorFree || !AnyTextFieldFocused()) && !Mechanics.IsControllingPlayer)
                Fly.Move();

            Mechanics.Update(Mode, acceptInput, AnyTextFieldFocused());

            if (acceptInput && CursorFree)
            {
                HandleWorldClick();
                if (!AnyTextFieldFocused() && !_menu.IsCapturingBind)
                    HandleEditKeys();
            }
            else if (Gizmo.IsDragging)
            {
                Gizmo.CancelDrag();
                ClearPendingUndo();
            }

            SelectionSys.PruneMulti();
            if (SelectionSys.IsMulti && !Gizmo.IsDragging)
            {
                // Park the group handle on the centroid between drags.
                var p = MultiProxy().transform;
                p.position = MultiCentroid();
                p.rotation = Quaternion.identity;
                p.localScale = Vector3.one;
            }

            Highlight.UpdateHighlight(SelectionSys.Current);
            UpdateMultiHighlights();
            UpdateMarkerProxies();
            Gizmo.UpdateVisual(GizmoTarget(), Camera.main);
            Xray.Update(Camera.main);
            UpdateEditorMarkers();

            if (_unresolvedObjects.Count > 0 && Catalog.HasScanned && Time.time >= _unresolvedRetryAt)
            {
                _unresolvedRetryAt = Time.time + 5f;
                RetryUnresolvedObjects();
            }

            if (Catalog.HasScanned && _warmupBaseTime > 0
                && _warmupScansDone < WARMUP_SCAN_DELAYS.Length
                && Time.time >= _warmupBaseTime + WARMUP_SCAN_DELAYS[_warmupScansDone])
            {
                _warmupScansDone++;
                int before = Catalog.Entries.Count;
                Catalog.Scan();
                int added = Catalog.Entries.Count - before;
                if (added > 0)
                    ShowToast($"Catalog: {added} new object(s) discovered ({Catalog.Entries.Count} total)");
            }
        }

        // Try to spawn pending objects whose source may have appeared since (assets load
        // lazily; level objects respawn). Successes leave the pending list; the rest
        // stay for the next pass — and stay in every save either way.
        private void RetryUnresolvedObjects()
        {
            int restored = 0;
            for (int i = _unresolvedObjects.Count - 1; i >= 0; i--)
            {
                var data = _unresolvedObjects[i];
                // A remote upsert may have materialized it meanwhile — just drop the dupe.
                if (!string.IsNullOrEmpty(data.Uid) && PlacedManager.FindByUid(data.Uid) != null)
                {
                    _unresolvedObjects.RemoveAt(i);
                    continue;
                }
                var src = Catalog.ResolveSource(data.Source, data.SourceName);
                if (src == null) continue;
                var placed = PlacedManager.Spawn(src, data.Source, data.SourceName,
                    VecUtil.ToVector3(data.Pos),
                    Quaternion.Euler(VecUtil.ToVector3(data.Rot)),
                    VecUtil.ToVector3(data.Scale, Vector3.one),
                    data.Tint, restore: data);
                if (placed != null)
                {
                    _unresolvedObjects.RemoveAt(i);
                    restored++;
                }
            }
            if (restored > 0)
            {
                SetDirty();
                ShowToast($"Recovered {restored} pending object(s) — source finished loading");
                MapEditorPlugin.Logger.LogInfo($"[MAP] Recovered {restored} pending object(s); {_unresolvedObjects.Count} still pending.");
            }
        }

        // Dim cyan boxes on every group member except the primary (which has the
        // pulsing highlight already).
        private void UpdateMultiHighlights()
        {
            var members = SelectionSys.Multi;
            while (_multiBoxes.Count < members.Count)
                _multiBoxes.Add(new LineBox($"FIH_Line_Multi{_multiBoxes.Count}"));
            for (int i = 0; i < _multiBoxes.Count; i++)
            {
                if (i >= members.Count || members[i].Target == null
                    || members[i].Target == SelectionSys.Current.Target)
                {
                    _multiBoxes[i].Hide();
                    continue;
                }
                var bounds = ObjectCatalog.ComputeBounds(members[i].Target);
                if (bounds.size == Vector3.zero) { _multiBoxes[i].Hide(); continue; }
                _multiBoxes[i].ShowBox(bounds, new Color(0.2f, 0.8f, 0.8f, 0.5f));
            }
        }

        // Transform the gizmo should attach to for the current selection.
        private Transform GizmoTarget()
        {
            if (SelectionSys.IsMulti) return MultiProxy().transform;

            var sel = SelectionSys.Current;
            if (sel.IsMarker)
            {
                switch (sel.Marker)
                {
                    case "goal": return Goal != null ? GoalProxy().transform : null;
                    case "spawn": return Spawn != null ? SpawnProxy().transform : null;
                    case "checkpoint":
                        return sel.MarkerIndex < Checkpoints.Count ? CheckpointProxy().transform : null;
                    case "reset":
                        return sel.MarkerIndex < ResetZones.Count ? ResetProxy().transform : null;
                    case "ball":
                        return Ball != null ? BallProxy().transform : null;
                    case "soccergoal":
                        return sel.MarkerIndex < SoccerGoals.Count ? SoccerGoalProxy().transform : null;
                    case "scoreboard":
                        return Scoreboard != null ? ScoreboardProxy().transform : null;
                    case "cannontarget":
                        // MarkerIndex holds the PlacedObject.Id (stable), not a list index.
                        var pc = PlacedManager.FindById(sel.MarkerIndex);
                        return pc?.CannonTarget != null ? CannonTargetProxy().transform : null;
                    case "cannonlaunch":
                        var lc = PlacedManager.FindById(sel.MarkerIndex);
                        return lc != null && lc.Mechanic == MechanicType.Cannon ? CannonLaunchProxy().transform : null;
                    default: return null;
                }
            }
            return sel.Target?.transform;
        }

        private GameObject GoalProxy()
        {
            if (_goalProxy == null)
            {
                _goalProxy = new GameObject("FIH_GoalProxy");
                UnityEngine.Object.DontDestroyOnLoad(_goalProxy);
            }
            return _goalProxy;
        }

        private GameObject SpawnProxy()
        {
            if (_spawnProxy == null)
            {
                _spawnProxy = new GameObject("FIH_SpawnProxy");
                UnityEngine.Object.DontDestroyOnLoad(_spawnProxy);
            }
            return _spawnProxy;
        }

        private GameObject CheckpointProxy()
        {
            if (_checkpointProxy == null)
            {
                _checkpointProxy = new GameObject("FIH_CheckpointProxy");
                UnityEngine.Object.DontDestroyOnLoad(_checkpointProxy);
            }
            return _checkpointProxy;
        }

        private GameObject ResetProxy()
        {
            if (_resetProxy == null)
            {
                _resetProxy = new GameObject("FIH_ResetProxy");
                UnityEngine.Object.DontDestroyOnLoad(_resetProxy);
            }
            return _resetProxy;
        }

        private GameObject BallProxy()
        {
            if (_ballProxy == null)
            {
                _ballProxy = new GameObject("FIH_BallProxy");
                UnityEngine.Object.DontDestroyOnLoad(_ballProxy);
            }
            return _ballProxy;
        }

        private GameObject SoccerGoalProxy()
        {
            if (_soccerGoalProxy == null)
            {
                _soccerGoalProxy = new GameObject("FIH_SoccerGoalProxy");
                UnityEngine.Object.DontDestroyOnLoad(_soccerGoalProxy);
            }
            return _soccerGoalProxy;
        }

        private GameObject ScoreboardProxy()
        {
            if (_scoreboardProxy == null)
            {
                _scoreboardProxy = new GameObject("FIH_ScoreboardProxy");
                UnityEngine.Object.DontDestroyOnLoad(_scoreboardProxy);
            }
            return _scoreboardProxy;
        }

        private GameObject CannonTargetProxy()
        {
            if (_cannonTargetProxy == null)
            {
                _cannonTargetProxy = new GameObject("FIH_CannonTargetProxy");
                UnityEngine.Object.DontDestroyOnLoad(_cannonTargetProxy);
            }
            return _cannonTargetProxy;
        }

        private GameObject CannonLaunchProxy()
        {
            if (_cannonLaunchProxy == null)
            {
                _cannonLaunchProxy = new GameObject("FIH_CannonLaunchProxy");
                UnityEngine.Object.DontDestroyOnLoad(_cannonLaunchProxy);
            }
            return _cannonLaunchProxy;
        }

        private GameObject MultiProxy()
        {
            if (_multiProxy == null)
            {
                _multiProxy = new GameObject("FIH_MultiProxy");
                UnityEngine.Object.DontDestroyOnLoad(_multiProxy);
            }
            return _multiProxy;
        }

        private Vector3 MultiCentroid()
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var m in SelectionSys.Multi)
            {
                if (m.Target == null) continue;
                sum += m.Target.transform.position;
                n++;
            }
            return n > 0 ? sum / n : Vector3.zero;
        }

        private void UpdateMarkerProxies()
        {
            var sel = SelectionSys.Current;

            if (Goal?.Center != null)
            {
                var p = GoalProxy().transform;
                if (Gizmo.IsDragging && sel.Marker == "goal")
                {
                    // Live sync while dragging so the box visual follows the gizmo.
                    Goal.Center = VecUtil.ToArray(p.position);
                    Goal.Rot = VecUtil.ToArray(p.eulerAngles);
                    var s = p.localScale;
                    Goal.Size = VecUtil.ToArray(new Vector3(
                        Mathf.Max(1f, s.x), Mathf.Max(1f, s.y), Mathf.Max(1f, s.z)));
                }
                else
                {
                    p.position = VecUtil.ToVector3(Goal.Center);
                    p.localScale = VecUtil.ToVector3(Goal.Size, Vector3.one * 4f);
                    p.rotation = VecUtil.ToRotation(Goal.Rot);
                }
            }

            if (Spawn?.Pos != null)
            {
                var p = SpawnProxy().transform;
                if (Gizmo.IsDragging && sel.Marker == "spawn")
                {
                    Spawn.Pos = VecUtil.ToArray(p.position);
                    Spawn.Yaw = p.eulerAngles.y;
                }
                else
                {
                    p.position = VecUtil.ToVector3(Spawn.Pos);
                    p.rotation = Quaternion.Euler(0f, Spawn.Yaw, 0f);
                    p.localScale = Vector3.one;
                }
            }

            if (sel.Marker == "checkpoint" && sel.MarkerIndex < Checkpoints.Count)
            {
                var cp = Checkpoints[sel.MarkerIndex];
                var p = CheckpointProxy().transform;
                if (cp.Size != null)
                {
                    // Box checkpoint: full goal-style transform (move/rotate/scale).
                    if (Gizmo.IsDragging)
                    {
                        cp.Pos = VecUtil.ToArray(p.position);
                        cp.Rot = VecUtil.ToArray(p.eulerAngles);
                        cp.Yaw = p.eulerAngles.y;   // respawn facing follows the box
                        var s = p.localScale;
                        cp.Size = VecUtil.ToArray(new Vector3(
                            Mathf.Max(1f, s.x), Mathf.Max(1f, s.y), Mathf.Max(1f, s.z)));
                    }
                    else
                    {
                        p.position = VecUtil.ToVector3(cp.Pos);
                        p.rotation = VecUtil.ToRotation(cp.Rot);
                        p.localScale = VecUtil.ToVector3(cp.Size, Vector3.one * 4f);
                    }
                }
                else if (Gizmo.IsDragging)
                {
                    cp.Pos = VecUtil.ToArray(p.position);
                    cp.Yaw = p.eulerAngles.y;
                    // Uniform radius from whatever axis got stretched furthest.
                    var s = p.localScale;
                    cp.Radius = Mathf.Clamp(Mathf.Max(s.x, s.y, s.z), 0.5f, 20f);
                }
                else
                {
                    p.position = VecUtil.ToVector3(cp.Pos);
                    p.rotation = Quaternion.Euler(0f, cp.Yaw, 0f);
                    p.localScale = Vector3.one * Mathf.Max(0.5f, cp.Radius);
                }
            }

            if (sel.Marker == "reset" && sel.MarkerIndex < ResetZones.Count)
            {
                var zone = ResetZones[sel.MarkerIndex];
                var p = ResetProxy().transform;
                if (Gizmo.IsDragging)
                {
                    zone.Center = VecUtil.ToArray(p.position);
                    zone.Rot = VecUtil.ToArray(p.eulerAngles);
                    var s = p.localScale;
                    zone.Size = VecUtil.ToArray(new Vector3(
                        Mathf.Max(1f, s.x), Mathf.Max(1f, s.y), Mathf.Max(1f, s.z)));
                }
                else
                {
                    p.position = VecUtil.ToVector3(zone.Center);
                    p.localScale = VecUtil.ToVector3(zone.Size, Vector3.one * 4f);
                    p.rotation = VecUtil.ToRotation(zone.Rot);
                }
            }

            if (sel.Marker == "ball" && Ball != null)
            {
                var p = BallProxy().transform;
                if (Gizmo.IsDragging)
                {
                    Ball.Center = VecUtil.ToArray(p.position);
                    // Uniform scale → radius (half the sphere diameter).
                    Ball.Radius = Mathf.Clamp(Mathf.Max(p.localScale.x, p.localScale.y, p.localScale.z) * 0.5f, 0.1f, 5f);
                }
                else
                {
                    p.position = VecUtil.ToVector3(Ball.Center);
                    p.rotation = Quaternion.identity;
                    p.localScale = Vector3.one * Mathf.Max(0.2f, Ball.Radius * 2f);
                }
            }

            if (sel.Marker == "soccergoal" && sel.MarkerIndex < SoccerGoals.Count)
            {
                var goal = SoccerGoals[sel.MarkerIndex];
                var p = SoccerGoalProxy().transform;
                if (Gizmo.IsDragging)
                {
                    goal.Center = VecUtil.ToArray(p.position);
                    goal.Rot = VecUtil.ToArray(p.eulerAngles);
                    var s = p.localScale;
                    goal.Size = VecUtil.ToArray(new Vector3(
                        Mathf.Max(1f, s.x), Mathf.Max(1f, s.y), Mathf.Max(1f, s.z)));
                }
                else
                {
                    p.position = VecUtil.ToVector3(goal.Center);
                    p.localScale = VecUtil.ToVector3(goal.Size, Vector3.one * 4f);
                    p.rotation = VecUtil.ToRotation(goal.Rot);
                }
            }

            if (sel.Marker == "scoreboard" && Scoreboard != null)
            {
                var p = ScoreboardProxy().transform;
                if (Gizmo.IsDragging)
                {
                    Scoreboard.Pos = VecUtil.ToArray(p.position);
                    Scoreboard.Rot = VecUtil.ToArray(p.eulerAngles);
                    // Uniform scale from whichever axis got stretched furthest.
                    Scoreboard.Scale = Mathf.Clamp(
                        Mathf.Max(p.localScale.x, p.localScale.y, p.localScale.z), 0.2f, 40f);
                }
                else
                {
                    p.position = VecUtil.ToVector3(Scoreboard.Pos);
                    p.rotation = VecUtil.ToRotation(Scoreboard.Rot);
                    p.localScale = Vector3.one * Mathf.Max(0.2f, Scoreboard.Scale);
                }
            }

            if (sel.Marker == "cannontarget")
            {
                var pc = PlacedManager.FindById(sel.MarkerIndex);
                if (pc?.CannonTarget == null)
                {
                    SelectionSys.Deselect(); // cannon deleted while its target was selected
                }
                else
                {
                    var p = CannonTargetProxy().transform;
                    if (Gizmo.IsDragging)
                    {
                        pc.CannonTarget = VecUtil.ToArray(p.position);
                    }
                    else
                    {
                        p.position = VecUtil.ToVector3(pc.CannonTarget);
                        p.rotation = Quaternion.identity;  // a point: position only
                        p.localScale = Vector3.one;
                    }
                }
            }

            if (sel.Marker == "cannonlaunch")
            {
                var lc = PlacedManager.FindById(sel.MarkerIndex);
                if (lc == null || lc.Mechanic != MechanicType.Cannon || lc.Root == null)
                {
                    SelectionSys.Deselect();
                }
                else
                {
                    // Materialize the auto position so there is a stored value to edit.
                    lc.CannonLaunchPos ??= VecUtil.ToArray(MechanicsController.GetLaunchPos(lc));
                    var p = CannonLaunchProxy().transform;
                    if (Gizmo.IsDragging)
                    {
                        lc.CannonLaunchPos = VecUtil.ToArray(p.position);
                    }
                    else
                    {
                        p.position = VecUtil.ToVector3(lc.CannonLaunchPos);
                        p.rotation = Quaternion.identity;
                        p.localScale = Vector3.one;
                    }
                }
            }
        }

        private void UpdateEditorMarkers()
        {
            if (Goal?.Center != null)
                _goalBox.ShowBox(VecUtil.ToVector3(Goal.Center),
                                 VecUtil.ToVector3(Goal.Size, Vector3.one * 3f),
                                 VecUtil.ToRotation(Goal.Rot),
                                 new Color(0.3f, 1f, 0.5f, 0.9f));
            else
                _goalBox.Hide();

            if (Spawn?.Pos != null)
                _spawnBox.ShowBox(new Bounds(VecUtil.ToVector3(Spawn.Pos) + Vector3.up * 1f,
                                             new Vector3(1f, 2f, 1f)),
                                  new Color(0.4f, 0.6f, 1f, 0.9f));
            else
                _spawnBox.Hide();

            // Checkpoints (orange): coin rings, or wireframe boxes for the box variant.
            while (_checkpointBoxes.Count < Checkpoints.Count)
                _checkpointBoxes.Add(new LineBox($"FIH_Line_EdCheckpoint{_checkpointBoxes.Count}"));
            for (int i = 0; i < _checkpointBoxes.Count; i++)
            {
                if (i >= Checkpoints.Count || Checkpoints[i]?.Pos == null)
                {
                    _checkpointBoxes[i].Hide();
                    continue;
                }
                var cp = Checkpoints[i];
                var cpColor = new Color(1f, 0.62f, 0.15f, 0.95f);
                if (cp.Size != null)
                    _checkpointBoxes[i].ShowBox(VecUtil.ToVector3(cp.Pos),
                        VecUtil.ToVector3(cp.Size, Vector3.one * 4f),
                        VecUtil.ToRotation(cp.Rot), cpColor);
                else
                    _checkpointBoxes[i].ShowRing(VecUtil.ToVector3(cp.Pos) + Vector3.up * 1f,
                        Mathf.Max(0.5f, cp.Radius), cpColor);
            }

            // Reset triggers (red boxes) — editor-only, invisible while playing.
            while (_resetBoxes.Count < ResetZones.Count)
                _resetBoxes.Add(new LineBox($"FIH_Line_EdReset{_resetBoxes.Count}"));
            for (int i = 0; i < _resetBoxes.Count; i++)
            {
                if (i >= ResetZones.Count || ResetZones[i]?.Center == null)
                {
                    _resetBoxes[i].Hide();
                    continue;
                }
                var zone = ResetZones[i];
                _resetBoxes[i].ShowBox(VecUtil.ToVector3(zone.Center),
                                       VecUtil.ToVector3(zone.Size, Vector3.one * 4f),
                                       VecUtil.ToRotation(zone.Rot),
                                       new Color(1f, 0.25f, 0.25f, 0.9f));
            }

            // Soccer: the ball kickoff point (white ring) + goal boxes (blue = team A,
            // red = team B) — editor-only.
            if (Ball?.Center != null)
                _ballRing.ShowRing(VecUtil.ToVector3(Ball.Center), Mathf.Max(0.25f, Ball.Radius),
                                   new Color(0.95f, 0.95f, 1f, 0.95f));
            else
                _ballRing.Hide();

            // The 3D scoreboard: live score while a match runs, 0-0 while editing.
            if (Scoreboard?.Pos != null)
                _scoreboardRenderer.Draw(Scoreboard, PlayMode.ScoreA, PlayMode.ScoreB);
            else
                _scoreboardRenderer.Hide();

            while (_soccerGoalBoxes.Count < SoccerGoals.Count)
                _soccerGoalBoxes.Add(new LineBox($"FIH_Line_EdSoccer{_soccerGoalBoxes.Count}"));
            for (int i = 0; i < _soccerGoalBoxes.Count; i++)
            {
                if (i >= SoccerGoals.Count || SoccerGoals[i]?.Center == null)
                {
                    _soccerGoalBoxes[i].Hide();
                    continue;
                }
                var g = SoccerGoals[i];
                var col = g.Team == 0 ? new Color(0.3f, 0.6f, 1f, 0.95f) : new Color(1f, 0.35f, 0.35f, 0.95f);
                _soccerGoalBoxes[i].ShowBox(VecUtil.ToVector3(g.Center),
                                            VecUtil.ToVector3(g.Size, Vector3.one * 4f),
                                            VecUtil.ToRotation(g.Rot), col);
            }

            // Launch targets (violet ring + aim line) of cannons and aimed pads — editor-only.
            var cannons = new List<PlacedObject>();
            foreach (var p in PlacedManager.Placed)
                if (p.Mechanic != MechanicType.None && p.CannonTarget != null && p.Root != null)
                    cannons.Add(p);

            while (_cannonTargetBoxes.Count < cannons.Count)
            {
                _cannonTargetBoxes.Add(new LineBox($"FIH_Line_EdCannonTgt{_cannonTargetBoxes.Count}"));
                _cannonLines.Add(new LineBox($"FIH_Line_EdCannonAim{_cannonLines.Count}"));
            }
            while (_cannonLaunchBoxes.Count < cannons.Count)
                _cannonLaunchBoxes.Add(new LineBox($"FIH_Line_EdCannonLaunch{_cannonLaunchBoxes.Count}"));
            for (int i = 0; i < _cannonTargetBoxes.Count; i++)
            {
                if (i >= cannons.Count)
                {
                    _cannonTargetBoxes[i].Hide();
                    _cannonLines[i].Hide();
                    if (i < _cannonLaunchBoxes.Count) _cannonLaunchBoxes[i].Hide();
                    continue;
                }
                var cannon = cannons[i];
                Vector3 target = VecUtil.ToVector3(cannon.CannonTarget);
                var color = new Color(0.75f, 0.45f, 1f, 0.95f);
                _cannonTargetBoxes[i].ShowRing(target + Vector3.up * 0.2f, 0.8f, color);

                if (cannon.Mechanic == MechanicType.Cannon)
                {
                    // Cyan ring = where the cannon holds and launches you from.
                    Vector3 launch = MechanicsController.GetLaunchPos(cannon);
                    _cannonLaunchBoxes[i].ShowRing(launch, 0.55f, new Color(0.25f, 0.9f, 1f, 0.95f));
                    _cannonLines[i].ShowLine(launch, target, new Color(0.75f, 0.45f, 1f, 0.5f));
                }
                else
                {
                    _cannonLaunchBoxes[i].Hide();
                    var padBounds = MechanicsController.ComputeColliderBounds(cannon.Root);
                    _cannonLines[i].ShowLine(padBounds.center + Vector3.up * padBounds.extents.y, target,
                                             new Color(0.75f, 0.45f, 1f, 0.5f));
                }
            }
        }

        private void HideMarkerVisuals()
        {
            _goalBox.Hide();
            _spawnBox.Hide();
            foreach (var box in _checkpointBoxes) box.Hide();
            foreach (var box in _resetBoxes) box.Hide();
            foreach (var box in _cannonTargetBoxes) box.Hide();
            foreach (var box in _cannonLines) box.Hide();
            foreach (var box in _cannonLaunchBoxes) box.Hide();
            foreach (var box in _multiBoxes) box.Hide();
            _ballRing.Hide();
            foreach (var box in _soccerGoalBoxes) box.Hide();
            _scoreboardRenderer.Hide();
        }

        private bool AnyTextFieldFocused() => _menu.HasFocusedTextField || _mapsHub.HasFocusedTextField;

        // ─────────────────────────────────────────────────────────────── undo ──

        private void CaptureTransformUndo(GameObject go, bool isRaw)
        {
            if (go == null) return;
            var t = go.transform;
            _pendingUndoGo = go;
            _pendingUndoRaw = isRaw;
            _pendingUndoPos = t.position;
            _pendingUndoRot = t.rotation;
            _pendingUndoScale = t.localScale;
        }

        private void PushTransformUndoIfChanged()
        {
            var go = _pendingUndoGo;
            _pendingUndoGo = null;
            if (go == null) return;

            var t = go.transform;
            if (Vector3.Distance(t.position, _pendingUndoPos) < 0.001f &&
                Quaternion.Angle(t.rotation, _pendingUndoRot) < 0.01f &&
                Vector3.Distance(t.localScale, _pendingUndoScale) < 0.001f)
                return;

            Vector3 pos = _pendingUndoPos;
            Quaternion rot = _pendingUndoRot;
            Vector3 scale = _pendingUndoScale;
            bool raw = _pendingUndoRaw;
            Undo.Push($"transform of {go.name}", () =>
            {
                if (go == null) return;
                var tt = go.transform;
                tt.position = pos;
                tt.rotation = rot;
                tt.localScale = scale;
                if (raw) LevelEdits.RecordTransform(go);
                SetDirty();
            });
        }

        private void ClearPendingUndo()
        {
            _pendingUndoGo = null;
            _pendingMarkerUndo = null;
        }

        // Deep copy of every marker (spawn/goal/checkpoints/resets) as a restore closure.
        private Action CaptureMarkersState()
        {
            var spawn = Spawn == null ? null
                : new SpawnPointData { Pos = (float[])Spawn.Pos?.Clone(), Yaw = Spawn.Yaw };
            var goal = Goal == null ? null
                : new GoalZoneData
                {
                    Center = (float[])Goal.Center?.Clone(),
                    Size = (float[])Goal.Size?.Clone(),
                    Rot = (float[])Goal.Rot?.Clone(),
                };
            // Uid must survive the round-trip: losing it makes multiplayer sync see the
            // marker as deleted and broadcast DELETEs to every peer after a Ctrl+Z.
            var cps = new List<CheckpointData>();
            foreach (var c in Checkpoints)
                cps.Add(new CheckpointData
                {
                    Uid = c.Uid,
                    Pos = (float[])c.Pos?.Clone(),
                    Yaw = c.Yaw,
                    Radius = c.Radius,
                    Size = (float[])c.Size?.Clone(),
                    Rot = (float[])c.Rot?.Clone(),
                });
            var zones = new List<ResetZoneData>();
            foreach (var z in ResetZones)
                zones.Add(new ResetZoneData
                {
                    Uid = z.Uid,
                    Center = (float[])z.Center?.Clone(),
                    Size = (float[])z.Size?.Clone(),
                    Rot = (float[])z.Rot?.Clone(),
                });
            var ball = Ball == null ? null
                : new BallData { Uid = Ball.Uid, Center = (float[])Ball.Center?.Clone(), Radius = Ball.Radius };
            var goals = new List<SoccerGoalData>();
            foreach (var g in SoccerGoals)
                goals.Add(new SoccerGoalData
                {
                    Uid = g.Uid,
                    Center = (float[])g.Center?.Clone(),
                    Size = (float[])g.Size?.Clone(),
                    Rot = (float[])g.Rot?.Clone(),
                    Team = g.Team,
                });
            var scoreboard = Scoreboard == null ? null
                : new ScoreboardData
                {
                    Uid = Scoreboard.Uid,
                    Pos = (float[])Scoreboard.Pos?.Clone(),
                    Rot = (float[])Scoreboard.Rot?.Clone(),
                    Scale = Scoreboard.Scale,
                };

            return () =>
            {
                Spawn = spawn;
                Goal = goal;
                Checkpoints = cps;
                ResetZones = zones;
                Ball = ball;
                SoccerGoals = goals;
                Scoreboard = scoreboard;
                if (SelectionSys.Current.IsMarker) SelectionSys.Deselect();
                SetDirty();
            };
        }

        // ─────────────────────────────────────────────────────── multi-selection ──

        private void BeginMultiDrag()
        {
            _multiDragStarts.Clear();
            _multiDragCentroid = MultiCentroid();
            foreach (var m in SelectionSys.Multi)
            {
                if (m.Target == null) continue;
                if (m.IsRaw) LevelEdits.CaptureOriginal(m.Raw);
                var t = m.Target.transform;
                _multiDragStarts.Add(new MultiDragStart
                {
                    Go = m.Target,
                    Raw = m.IsRaw,
                    Pos = t.position,
                    Rot = t.rotation,
                    Scale = t.localScale,
                });
            }
            _pendingMarkerUndo = CaptureMultiState();
        }

        // Move/rotate/scale every member by the proxy's delta, pivoting on the centroid
        // (Blender-style group transform).
        private void ApplyMultiDrag()
        {
            var p = MultiProxy().transform;
            Vector3 dPos = p.position - _multiDragCentroid;
            Quaternion dRot = p.rotation;                    // started as identity
            float f = Mathf.Max(0.05f, Mathf.Max(p.localScale.x, Mathf.Max(p.localScale.y, p.localScale.z)));

            foreach (var start in _multiDragStarts)
            {
                if (start.Go == null) continue;
                var t = start.Go.transform;
                t.position = _multiDragCentroid + dPos + dRot * ((start.Pos - _multiDragCentroid) * f);
                t.rotation = dRot * start.Rot;
                t.localScale = start.Scale * f;
            }
        }

        // One restore closure for the whole group (single Ctrl+Z entry).
        private Action CaptureMultiState()
        {
            var snap = new List<MultiDragStart>();
            foreach (var m in SelectionSys.Multi)
            {
                if (m.Target == null) continue;
                var t = m.Target.transform;
                snap.Add(new MultiDragStart
                {
                    Go = m.Target,
                    Raw = m.IsRaw,
                    Pos = t.position,
                    Rot = t.rotation,
                    Scale = t.localScale,
                });
            }
            return () =>
            {
                foreach (var s in snap)
                {
                    if (s.Go == null) continue;
                    var t = s.Go.transform;
                    t.position = s.Pos;
                    t.rotation = s.Rot;
                    t.localScale = s.Scale;
                    if (s.Raw) LevelEdits.RecordTransform(s.Go);
                }
                SetDirty();
            };
        }

        private void MultiTransform(string undoLabel, Action<Transform, Vector3> apply)
        {
            Undo.Push(undoLabel, CaptureMultiState());
            Vector3 centroid = MultiCentroid();
            foreach (var m in SelectionSys.Multi)
            {
                if (m.Target == null) continue;
                if (m.IsRaw) LevelEdits.CaptureOriginal(m.Raw);
                apply(m.Target.transform, centroid);
                if (m.IsRaw) LevelEdits.RecordTransform(m.Raw);
            }
            SetDirty();
        }

        private void MultiDelete()
        {
            var placedDatas = new List<MapObjectData>();
            var hiddenRaws = new List<GameObject>();
            foreach (var m in SelectionSys.Multi)
            {
                if (m.IsPlaced && m.Placed.Root != null)
                {
                    placedDatas.Add(m.Placed.ToData());
                    PlacedManager.Delete(m.Placed);
                }
                else if (m.IsRaw && m.Raw != null)
                {
                    LevelEdits.Hide(m.Raw);
                    hiddenRaws.Add(m.Raw);
                }
            }
            int total = placedDatas.Count + hiddenRaws.Count;
            SelectionSys.Deselect();

            Undo.Push($"group delete ({total} objects)", () =>
            {
                foreach (var data in placedDatas)
                {
                    var src = Catalog.ResolveSource(data.Source, data.SourceName);
                    if (src == null) continue;
                    PlacedManager.Spawn(src, data.Source, data.SourceName,
                        VecUtil.ToVector3(data.Pos),
                        Quaternion.Euler(VecUtil.ToVector3(data.Rot)),
                        VecUtil.ToVector3(data.Scale, Vector3.one),
                        data.Tint, restore: data);
                }
                foreach (var raw in hiddenRaws)
                {
                    var record = LevelEdits.Find(raw);
                    if (record != null)
                    {
                        LevelEdits.Unhide(record);
                        LevelEdits.RecordTransform(raw);
                    }
                }
                SetDirty();
            });

            SetDirty();
            ShowToast($"Deleted {total} objects (placed removed, level objects hidden)");
        }

        private void MultiDuplicate()
        {
            Vector3 offset = Vector3.up * 0.5f + CameraAxisForward() * 1.5f;
            var copies = new List<PlacedObject>();
            foreach (var m in SelectionSys.Multi)
            {
                if (m.Target == null) continue;
                GameObject source = m.Target;
                string path = m.IsPlaced ? m.Placed.SourcePath : ObjectCatalog.BuildPath(m.Raw.transform);
                string name = m.IsPlaced ? m.Placed.SourceName : m.Raw.name;
                TintColor tint = m.IsPlaced ? m.Placed.Tint : TintColor.None;
                // A duplicate is a new independent object: fresh Uid, no inherited group.
                var restore = m.IsPlaced ? m.Placed.ToData() : null;
                if (restore != null) { restore.Uid = null; restore.GroupId = null; }

                var t = source.transform;
                var copy = PlacedManager.Spawn(source, path, name, t.position + offset, t.rotation, t.localScale, tint, restore);
                if (copy == null) continue;
                if (copy.Mechanic != MechanicType.None && copy.CannonTarget != null)
                    copy.CannonTarget = VecUtil.ToArray(VecUtil.ToVector3(copy.CannonTarget) + offset);
                copies.Add(copy);
            }
            if (copies.Count == 0) return;

            var undoCopies = new List<PlacedObject>(copies);
            Undo.Push($"group duplicate ({copies.Count})", () =>
            {
                foreach (var c in undoCopies)
                {
                    if (SelectionSys.Current.Placed == c) SelectionSys.Deselect();
                    PlacedManager.Delete(c);
                }
                SelectionSys.PruneMulti();
                SetDirty();
            });

            // The copies become the new group.
            var newMembers = new List<Selection>();
            foreach (var c in copies) newMembers.Add(new Selection { Placed = c });
            SelectionSys.SetMulti(newMembers);
            SetDirty();
            ShowToast($"Duplicated {copies.Count} objects");
        }

        // ──────────────────────────────────────────────────────────── grouping ──

        // Turns the current multi-selection into a persistent group: selecting any
        // member later (world click or LIST row) reselects the whole set.
        public void GroupSelected()
        {
            if (ReadOnlyBlock()) return;
            var members = new List<PlacedObject>();
            foreach (var m in SelectionSys.IsMulti ? SelectionSys.Multi : new List<Selection> { SelectionSys.Current })
                if (m.IsPlaced && m.Placed.Root != null) members.Add(m.Placed);
            if (members.Count < 2)
            {
                ShowToast("Select 2+ objects (Ctrl+Click) to group them");
                return;
            }

            var prevIds = new Dictionary<PlacedObject, string>();
            foreach (var p in members) prevIds[p] = p.GroupId;

            string groupId = Guid.NewGuid().ToString("N");
            foreach (var p in members) p.GroupId = groupId;

            Undo.Push($"group {members.Count} objects", () =>
            {
                foreach (var kv in prevIds)
                    if (kv.Key.Root != null) kv.Key.GroupId = kv.Value;
                SetDirty();
            });

            SetDirty();
            ShowToast($"Grouped {members.Count} objects — click any of them to reselect the group");
        }

        public void UngroupSelected()
        {
            if (ReadOnlyBlock()) return;
            var members = new List<PlacedObject>();
            foreach (var m in SelectionSys.IsMulti ? SelectionSys.Multi : new List<Selection> { SelectionSys.Current })
                if (m.IsPlaced && m.Placed.Root != null && m.Placed.GroupId != null) members.Add(m.Placed);
            if (members.Count == 0) return;

            var prevIds = new Dictionary<PlacedObject, string>();
            foreach (var p in members) prevIds[p] = p.GroupId;
            foreach (var p in members) p.GroupId = null;

            Undo.Push($"ungroup {members.Count} objects", () =>
            {
                foreach (var kv in prevIds)
                    if (kv.Key.Root != null) kv.Key.GroupId = kv.Value;
                SetDirty();
            });

            SetDirty();
            ShowToast($"Ungrouped {members.Count} objects");
        }

        // ────────────────────────────────────────────────────────────── color ──

        private IEnumerable<PlacedObject> ColorTargets()
        {
            if (SelectionSys.IsMulti)
            {
                foreach (var m in SelectionSys.Multi)
                    if (m.IsPlaced && m.Placed.Root != null) yield return m.Placed;
            }
            else if (SelectionSys.Current.IsPlaced && SelectionSys.Current.Placed.Root != null)
            {
                yield return SelectionSys.Current.Placed;
            }
        }

        private void PushColorUndo(List<PlacedObject> targets)
        {
            var prev = new List<(PlacedObject p, TintColor tint, float[] custom)>();
            foreach (var p in targets) prev.Add((p, p.Tint, (float[])p.CustomColor?.Clone()));
            Undo.Push($"color change ({targets.Count})", () =>
            {
                foreach (var (p, tint, custom) in prev)
                {
                    if (p.Root == null) continue;
                    if (custom != null) PlacedManager.ApplyCustomColor(p, new Color(custom[0], custom[1], custom[2]));
                    else PlacedManager.ApplyTint(p, tint);
                }
                SetDirty();
            });
        }

        public void TintSelected(TintColor tint)
        {
            if (ReadOnlyBlock()) return;
            var targets = new List<PlacedObject>(ColorTargets());
            if (targets.Count == 0) return;
            PushColorUndo(targets);
            foreach (var p in targets) PlacedManager.ApplyTint(p, tint);
            SetDirty();
        }

        public void SetCustomColorSelected(Color color)
        {
            if (ReadOnlyBlock()) return;
            var targets = new List<PlacedObject>(ColorTargets());
            if (targets.Count == 0)
            {
                ShowToast("Select an object first");
                return;
            }
            PushColorUndo(targets);
            foreach (var p in targets) PlacedManager.ApplyCustomColor(p, color);
            SetDirty();
            ShowToast($"Color applied to {targets.Count} object(s)");
        }

        public void ClearColorSelected()
        {
            if (ReadOnlyBlock()) return;
            var targets = new List<PlacedObject>(ColorTargets());
            if (targets.Count == 0) return;
            PushColorUndo(targets);
            foreach (var p in targets) PlacedManager.ClearColor(p);
            SetDirty();
        }

        // Cannon targets live on PlacedObjects, not the marker lists, so they need
        // their own restore closure (CaptureMarkersState would miss them).
        private Action CaptureCannonTargetState(PlacedObject cannon)
        {
            if (cannon?.CannonTarget == null) return null;
            var saved = (float[])cannon.CannonTarget.Clone();
            return () =>
            {
                if (cannon.Root != null) cannon.CannonTarget = (float[])saved.Clone();
                SetDirty();
            };
        }

        private Action CaptureCannonLaunchState(PlacedObject cannon)
        {
            if (cannon == null) return null;
            var saved = (float[])cannon.CannonLaunchPos?.Clone();   // null = auto position
            return () =>
            {
                if (cannon.Root != null) cannon.CannonLaunchPos = (float[])saved?.Clone();
                SetDirty();
            };
        }

        public void UndoLast()
        {
            if (Undo.Undo(out string label))
                ShowToast($"Undone: {label}  ({Undo.Count} more)");
            else
                ShowToast("Nothing to undo");
        }

        private void HandleWorldClick()
        {
            // Ongoing gizmo drag owns the mouse until release.
            if (Gizmo.IsDragging)
            {
                bool multi = SelectionSys.IsMulti;
                if (!UnityEngine.Input.GetMouseButton(0))
                {
                    Gizmo.EndDrag();
                    var dragged = SelectionSys.Current;
                    if (multi)
                    {
                        foreach (var start in _multiDragStarts)
                            if (start.Raw && start.Go != null) LevelEdits.RecordTransform(start.Go);
                    }
                    else if (dragged.IsRaw)
                    {
                        LevelEdits.RecordTransform(dragged.Raw);
                    }
                    if (_pendingMarkerUndo != null)
                    {
                        string label = multi
                            ? $"group edit ({SelectionSys.Multi.Count} objects)"
                            : $"edit of {dragged.DisplayName}";
                        Undo.Push(label, _pendingMarkerUndo);
                        _pendingMarkerUndo = null;
                    }
                    else
                    {
                        PushTransformUndoIfChanged();
                    }
                    SetDirty();
                }
                else
                {
                    var dragRay = MouseRay();
                    if (dragRay.HasValue) Gizmo.UpdateDrag(dragRay.Value);
                    if (multi) ApplyMultiDrag();
                }
                return;
            }

            if (!UnityEngine.Input.GetMouseButtonDown(0)) return;
            // Ask the windows directly whether the mouse is on them — clicks on the menu
            // must never reach the world.
            if (_menu.ContainsMouse() || _mapsHub.ContainsMouse()) return;

            var ray = MouseRay();
            if (!ray.HasValue) return;

            // Ctrl+Click = multi-selection toggle; it never grabs the gizmo or stamps.
            bool ctrlClick = Input.IsCtrlHeld();

            // Gizmo handles win over selection picking — on clones, level objects and
            // marker proxies alike. Skipped on play-only maps (no editing gizmo).
            var gizmoTarget = ReadOnly ? null : GizmoTarget();
            if (!ctrlClick && gizmoTarget != null && Gizmo.TryBeginDrag(ray.Value, gizmoTarget, Camera.main))
            {
                if (SelectionSys.IsMulti)
                {
                    BeginMultiDrag();
                    return;
                }

                // Capture the pristine state of a level object before the drag mutates it.
                if (SelectionSys.Current.IsRaw) LevelEdits.CaptureOriginal(SelectionSys.Current.Raw);

                // Arm the Ctrl+Z entry for this drag.
                if (SelectionSys.Current.Marker == "cannontarget")
                    _pendingMarkerUndo = CaptureCannonTargetState(PlacedManager.FindById(SelectionSys.Current.MarkerIndex));
                else if (SelectionSys.Current.Marker == "cannonlaunch")
                    _pendingMarkerUndo = CaptureCannonLaunchState(PlacedManager.FindById(SelectionSys.Current.MarkerIndex));
                else if (SelectionSys.Current.IsMarker)
                    _pendingMarkerUndo = CaptureMarkersState();
                else
                    CaptureTransformUndo(gizmoTarget.gameObject, SelectionSys.Current.IsRaw);
                return;
            }

            var previousSelection = SelectionSys.Current;
            bool hit = SelectionSys.PickAtMouse(UnlockOriginals, PickInvisible, out Vector3 hitPoint,
                additive: ctrlClick);

            if (ctrlClick)
            {
                // Only merge when the click actually picked something (PickAtMouse
                // leaves Current untouched on empty space).
                if (!ReferenceEquals(SelectionSys.Current, previousSelection))
                {
                    SelectionSys.CtrlMerge(previousSelection);
                    if (SelectionSys.IsMulti)
                        ShowToast($"{SelectionSys.Multi.Count} objects selected (Ctrl+Click to add/remove)");
                }
                return;
            }

            // Markers (goal/spawn boxes have no colliders): select whichever is closer
            // than the physics hit, so they're clickable like any other object.
            float worldDist = hit
                ? Vector3.Distance(ray.Value.origin, hitPoint)
                : float.MaxValue;
            string marker = null;
            int markerIndex = 0;
            float markerDist = worldDist;
            if (Goal?.Center != null)
            {
                if (SelectionSystem.RayIntersectsOBB(ray.Value, VecUtil.ToVector3(Goal.Center),
                        VecUtil.ToVector3(Goal.Size, Vector3.one * 4f), VecUtil.ToRotation(Goal.Rot),
                        out float d) && d < markerDist)
                    { marker = "goal"; markerIndex = 0; markerDist = d; }
            }
            if (Spawn?.Pos != null)
            {
                var b = new Bounds(VecUtil.ToVector3(Spawn.Pos) + Vector3.up * 1f, new Vector3(1f, 2f, 1f));
                if (SelectionSystem.RayIntersectsAABB(ray.Value, b, out float d) && d < markerDist)
                    { marker = "spawn"; markerIndex = 0; markerDist = d; }
            }
            for (int i = 0; i < Checkpoints.Count; i++)
            {
                var cp = Checkpoints[i];
                if (cp?.Pos == null) continue;
                if (cp.Size != null)
                {
                    if (SelectionSystem.RayIntersectsOBB(ray.Value, VecUtil.ToVector3(cp.Pos),
                            VecUtil.ToVector3(cp.Size, Vector3.one * 4f), VecUtil.ToRotation(cp.Rot),
                            out float bd) && bd < markerDist)
                        { marker = "checkpoint"; markerIndex = i; markerDist = bd; }
                    continue;
                }
                float r = Mathf.Max(0.5f, cp.Radius);
                var b = new Bounds(VecUtil.ToVector3(cp.Pos) + Vector3.up * 1f, Vector3.one * r * 2f);
                if (SelectionSystem.RayIntersectsAABB(ray.Value, b, out float d) && d < markerDist)
                    { marker = "checkpoint"; markerIndex = i; markerDist = d; }
            }
            for (int i = 0; i < ResetZones.Count; i++)
            {
                var zone = ResetZones[i];
                if (zone?.Center == null) continue;
                if (SelectionSystem.RayIntersectsOBB(ray.Value, VecUtil.ToVector3(zone.Center),
                        VecUtil.ToVector3(zone.Size, Vector3.one * 4f), VecUtil.ToRotation(zone.Rot),
                        out float d) && d < markerDist)
                    { marker = "reset"; markerIndex = i; markerDist = d; }
            }
            if (Ball?.Center != null)
            {
                float r = Mathf.Max(0.25f, Ball.Radius);
                var b = new Bounds(VecUtil.ToVector3(Ball.Center), Vector3.one * r * 2f);
                if (SelectionSystem.RayIntersectsAABB(ray.Value, b, out float d) && d < markerDist)
                    { marker = "ball"; markerIndex = 0; markerDist = d; }
            }
            for (int i = 0; i < SoccerGoals.Count; i++)
            {
                var goal = SoccerGoals[i];
                if (goal?.Center == null) continue;
                if (SelectionSystem.RayIntersectsOBB(ray.Value, VecUtil.ToVector3(goal.Center),
                        VecUtil.ToVector3(goal.Size, Vector3.one * 4f), VecUtil.ToRotation(goal.Rot),
                        out float d) && d < markerDist)
                    { marker = "soccergoal"; markerIndex = i; markerDist = d; }
            }
            if (Scoreboard?.Pos != null)
            {
                // Board plane ≈ 4×2 digit-units drawn at scale*0.5 → half-metre depth.
                float s = Mathf.Max(0.2f, Scoreboard.Scale) * 0.5f;
                if (SelectionSystem.RayIntersectsOBB(ray.Value, VecUtil.ToVector3(Scoreboard.Pos),
                        new Vector3(4.6f * s, 2.4f * s, 0.6f), VecUtil.ToRotation(Scoreboard.Rot),
                        out float d) && d < markerDist)
                    { marker = "scoreboard"; markerIndex = 0; markerDist = d; }
            }
            foreach (var p in PlacedManager.Placed)
            {
                if (p.Mechanic == MechanicType.None || p.Root == null) continue;
                if (p.CannonTarget != null)
                {
                    var b = new Bounds(VecUtil.ToVector3(p.CannonTarget), Vector3.one * 1.6f);
                    if (SelectionSystem.RayIntersectsAABB(ray.Value, b, out float d) && d < markerDist)
                        { marker = "cannontarget"; markerIndex = p.Id; markerDist = d; }
                }
                if (p.Mechanic == MechanicType.Cannon)
                {
                    var b = new Bounds(MechanicsController.GetLaunchPos(p), Vector3.one * 1.2f);
                    if (SelectionSystem.RayIntersectsAABB(ray.Value, b, out float d) && d < markerDist)
                        { marker = "cannonlaunch"; markerIndex = p.Id; markerDist = d; }
                }
            }
            if (marker != null)
            {
                SelectionSys.SelectMarker(marker, markerIndex);
                return;
            }

            // Stamp placement only when the master mouse-place switch is on.
            if (MousePlaceEnabled && StampEntry != null && hit)
            {
                PlaceEntryAt(StampEntry, hitPoint);
            }
        }

        private Ray? MouseRay()
        {
            var cam = Camera.main;
            if (cam == null) return null;
            return cam.ScreenPointToRay(UnityEngine.Input.mousePosition);
        }

        private void HandleEditKeys()
        {
            // Play-only map: no keyboard editing at all (undo/save/etc. are gated too).
            if (ReadOnly) return;

            var sel = SelectionSys.Current;

            // Gizmo mode hotkeys (1/2/3, like Unity's W/E/R but free of fly-key conflicts)
            if (Input.WasKeyPressed("gzMove", KeyCode.Alpha1)) Gizmo.Mode = GizmoMode.Move;
            if (Input.WasKeyPressed("gzRot", KeyCode.Alpha2)) Gizmo.Mode = GizmoMode.Rotate;
            if (Input.WasKeyPressed("gzScale", KeyCode.Alpha3)) Gizmo.Mode = GizmoMode.Scale;

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
            _timesViewer?.Close();   // the Off-mode windows never outlive a mode change
            if (Mode == EditorMode.Off) _mapsHub?.Close();   // (leaving Editor closes it below)
            // Play-only maps lock the WHOLE editor, not just the edit operations —
            // otherwise fly mode + closing the mod mid-course is a free teleport cheat
            // ("start the run from wherever I flew to"). Play ↔ Off only; discarding
            // the map (F6 twice) is the only way back into the editor.
            if (newMode == EditorMode.Editor && ReadOnly)
            {
                ShowToast($"Play-only map — editor locked. {EditorConfig.Settings.TogglePlayKey}: play it, " +
                          $"{EditorConfig.Settings.ToggleEditorKey} twice: discard it");
                return;
            }
            var old = Mode;
            Mode = newMode;

            try
            {
                // Leave old mode
                if (old == EditorMode.Editor)
                {
                    // Closing the editor / starting a run are the moments a crash hurts
                    // most — snapshot to the autosave right now.
                    AutosaveNow("leaving editor");
                    if (CursorFree) SetCursorFree(false);
                    _mapsHub.Close();
                    Mechanics.ResetState(); // before Fly.Exit: it may need to re-enter fly first
                    Fly.Exit();
                    Highlight.Hide();
                    Gizmo.CancelDrag();
                    Gizmo.Hide();
                    Xray.HideAll();
                    HideMarkerVisuals();
                }
                else if (old == EditorMode.Play)
                {
                    Mechanics.ResetState();
                    PlayMode.Exit();
                }

                // Enter new mode
                if (newMode == EditorMode.Editor)
                {
                    if (!Catalog.HasScanned) Catalog.Scan();
                    // Arm the warm-up rescans once per scene: lazily-loaded content
                    // appears during the first minute, then the catalog stabilizes.
                    if (_warmupBaseTime < 0) _warmupBaseTime = Time.time;
                    if (_workingSnapshot == null) NewMap(silent: true);
                    Fly.Enter();
                    Multiplayer.OnEnteredEditor();   // apply a map queued during Play
                    var k = EditorConfig.Settings;
                    ShowToast($"EDITOR — {k.ToggleCursorKey}: free cursor, {k.MapsHubKey}: maps, {k.TogglePlayKey}: Play mode, {k.ToggleEditorKey}: close");
                }
                else if (newMode == EditorMode.Play)
                {
                    RefreshSnapshot();
                    if (string.IsNullOrEmpty(MapId)) MapId = Guid.NewGuid().ToString("N");
                    PlayMode.Enter(Spawn, Goal, BaseMode == MapBaseMode.Blank, MapName, MapId,
                        Checkpoints, ResetZones, Ball, SoccerGoals);
                    ShowToast($"PLAY — {EditorConfig.Settings.RestartRunKey} / pad X: retry (last coin), Shift+{EditorConfig.Settings.RestartRunKey} / LB+X: full restart, {EditorConfig.Settings.TogglePlayKey}: editor");
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

        // Independent of SetCursorFree/CursorFree (an Editor-mode concept): Play mode
        // never otherwise touches cursor lock, but the post-run upload prompt needs a
        // clickable cursor for its buttons. Self-corrects every frame, so leaving Play
        // (or the prompt closing) while it's active restores the game's own cursor
        // control on the very next Update().
        private bool _playPromptCursorFree;

        private void UpdatePlayPromptCursor()
        {
            // Windows outside editor mode that need a clickable cursor: the post-run
            // upload prompt (Play), the play-only leaderboard viewer and the maps hub (Off).
            bool wantFree = (Mode == EditorMode.Play && PlayMode.UploadPrompt == UploadPromptState.Offered)
                || (Mode == EditorMode.Off && ((_timesViewer != null && _timesViewer.Visible)
                                               || (_mapsHub != null && _mapsHub.Visible)));
            if (wantFree == _playPromptCursorFree) return;
            _playPromptCursorFree = wantFree;

            try
            {
                if (wantFree)
                {
                    if (UnityEngine.InputSystem.Keyboard.current != null)
                        UnityEngine.InputSystem.InputSystem.DisableDevice(UnityEngine.InputSystem.Keyboard.current);
                    if (UnityEngine.InputSystem.Mouse.current != null)
                        UnityEngine.InputSystem.InputSystem.DisableDevice(UnityEngine.InputSystem.Mouse.current);
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                else
                {
                    if (UnityEngine.InputSystem.Keyboard.current != null)
                        UnityEngine.InputSystem.InputSystem.EnableDevice(UnityEngine.InputSystem.Keyboard.current);
                    if (UnityEngine.InputSystem.Mouse.current != null)
                        UnityEngine.InputSystem.InputSystem.EnableDevice(UnityEngine.InputSystem.Mouse.current);
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[PLAY] Upload-prompt cursor toggle error: {ex.Message}");
            }
        }

        // ───────────────────────────────────────────────────────── map lifecycle ──

        private float _readOnlyToastAt = -99f;

        // The times-viewer's discard button: throw away the play-only map and hand the
        // user a fresh, unlocked editor.
        public void DiscardPlayOnlyMap()
        {
            NewMap();
            SetMode(EditorMode.Editor);
            ShowToast("Play-only map discarded — empty editor ready");
        }
        // Returns true (and nags, throttled) when the current map is play-only, so edit
        // operations can early-return. Selection/teleport/play stay allowed.
        public bool ReadOnlyBlock()
        {
            if (!ReadOnly) return false;
            if (Time.unscaledTime - _readOnlyToastAt > 2f)
            {
                _readOnlyToastAt = Time.unscaledTime;
                ShowToast("This map is play-only — the creator locked editing");
            }
            return true;
        }

        public void SetDirty()
        {
            Dirty = true;
            if (_autosaveDirtySince < 0) _autosaveDirtySince = Time.unscaledTime;
            RefreshSnapshot();
            Multiplayer?.NotifyDirty();
        }

        private void RefreshSnapshot()
        {
            _workingSnapshot = BuildMapFile();
        }

        public MapFile BuildMapFile()
        {
            // Unresolved (pending) objects ride along in every snapshot: saves keep
            // them, and the sync diff never mistakes them for deletions.
            var objects = PlacedManager.Snapshot();
            if (_unresolvedObjects.Count > 0) objects.AddRange(_unresolvedObjects);
            return new MapFile
            {
                FormatVersion = MapFile.CURRENT_FORMAT_VERSION,
                MapId = MapId,
                Editable = Editable,
                AuthorName = AuthorName,
                AuthorSteamId = AuthorSteamId,
                Name = MapName,
                BaseMode = BaseMode,
                GameScene = _lastSceneName,
                Spawn = Spawn,
                Goal = Goal,
                Objects = objects,
                LevelEdits = LevelEdits.Snapshot(),
                Checkpoints = new List<CheckpointData>(Checkpoints),
                ResetZones = new List<ResetZoneData>(ResetZones),
                Ball = Ball,
                SoccerGoals = new List<SoccerGoalData>(SoccerGoals),
                Scoreboard = Scoreboard,
            };
        }

        private void UpdateAutosave()
        {
            if (!Dirty || _autosaveDirtySince < 0) return;
            if (Time.unscaledTime - _autosaveDirtySince < EditorConfig.Settings.AutosaveIntervalSeconds) return;
            AutosaveNow("interval");
        }

        private void AutosaveNow(string reason)
        {
            if (!Dirty) return;
            try
            {
                RefreshSnapshot();
                MapSerializer.SaveAutosave(_workingSnapshot);
                LastAutosaveTime = Time.unscaledTime;
                _autosaveDirtySince = Time.unscaledTime; // keep autosaving while still dirty
                MapEditorPlugin.Logger.LogInfo($"[MAP] Autosaved ({reason}).");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[MAP] Autosave error: {ex.Message}");
            }
        }

        // Ctrl+S: overwrite the current file, or create one from the map name.
        public void QuickSave()
        {
            if (ReadOnlyBlock()) return;
            if (!SaveOverwrite())
                SaveAsNew(MapName);
        }

        public void NewMap(bool silent = false)
        {
            PlacedManager.WipeAll();
            LevelEdits.RestoreAll();
            SelectionSys.Deselect();
            BlankCanvas.Restore();
            MapName = "Untitled";
            MapId = System.Guid.NewGuid().ToString("N");
            Editable = true;
            AuthorName = null;
            AuthorSteamId = 0;
            ReadOnly = false;
            CurrentFileName = null;
            BaseMode = MapBaseMode.Overlay;
            Spawn = null;
            Goal = null;
            Checkpoints.Clear();
            ResetZones.Clear();
            Ball = null;
            SoccerGoals.Clear();
            Scoreboard = null;
            _unresolvedObjects.Clear();
            Dirty = false;
            _autosaveDirtySince = -1f;
            LoadReport = "";
            Undo.Clear();
            ClearPendingUndo();
            RefreshSnapshot();
            // A user-invoked "New map" wipes the co-edit session for everyone (DELETEs
            // for every synced key). The SILENT call — the editor's first-open default
            // init — must never broadcast: it races the initial seed from peers and
            // could clobber their map with an empty "Untitled".
            if (!silent)
            {
                Multiplayer?.NotifyDirty();
                ShowToast("New empty map");
            }
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
            if (ReadOnlyBlock()) return false;
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
                // Autosave slots are recovery buffers, never a save target.
                CurrentFileName = fileName.StartsWith(MapSerializer.AUTOSAVE_NAME) ? null : fileName;
                ShowToast($"Loaded \"{MapName}\" — {LoadReport}");
                // Play-only: never leave the user sitting in the editor with fly mode.
                if (ReadOnly && Mode == EditorMode.Editor)
                {
                    SetMode(EditorMode.Play);
                    ShowToast($"\"{MapName}\" is play-only — straight into Play. {EditorConfig.Settings.TogglePlayKey}: stop playing");
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[MAP] Load error: {ex}");
                ShowToast($"ERROR loading \"{fileName}\": {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────── online maps ──

        // Whether the local player owns this online map (has its secret token).
        public bool OwnsOnlineMap(string mapId)
            => !string.IsNullOrEmpty(mapId) && EditorConfig.Settings.OwnerTokens.ContainsKey(mapId);

        // Upload the current working map to the community library. Stamps author from
        // Steam, mints an owner token on first upload (kept locally). editable=false
        // marks it play-only for everyone else.
        public void UploadCurrentMap(bool editable)
        {
            if (!OnlineMaps.Configured) { ShowToast("Online maps not configured"); return; }
            if (!HasWorkingContent()) { ShowToast("Nothing to upload — the map is empty"); return; }
            // A play-only map without a spawn would "play from wherever you stand" —
            // trivially cheatable. Force the author to define the intended start.
            if (!editable && Spawn?.Pos == null)
            {
                ShowToast("Play-only maps need a spawn — Set Spawn Here (TOOLS) first");
                return;
            }
            if (string.IsNullOrEmpty(MapId)) MapId = Guid.NewGuid().ToString("N");

            var (sid, name) = SteamIdentity();
            AuthorName = name;
            AuthorSteamId = sid;
            Editable = editable;

            // Reuse our token if we've uploaded this map before, else mint one.
            if (!EditorConfig.Settings.OwnerTokens.TryGetValue(MapId, out string token))
            {
                token = Guid.NewGuid().ToString("N");
                EditorConfig.Settings.OwnerTokens[MapId] = token;
                EditorConfig.Save();
            }

            RefreshSnapshot();
            var map = _workingSnapshot;
            ShowToast("Uploading map…");
            OnlineMaps.Upload(map, token, result => RunOnMainThread(() =>
            {
                if (result == "inserted" || result == "updated")
                    ShowToast($"Map uploaded ({(editable ? "editable" : "play-only")}) — \"{MapName}\"");
                else if (result == "forbidden")
                    ShowToast("Upload rejected: this map id belongs to someone else");
                else
                    ShowToast($"Upload failed: {result}");
            }));
        }

        // Download an online map and load it. If it's play-only and we don't own it,
        // ApplyMapFile sets ReadOnly.
        public void DownloadAndLoadMap(string mapId)
        {
            if (!OnlineMaps.Configured) { ShowToast("Online maps not configured"); return; }
            ShowToast("Downloading map…");
            OnlineMaps.Download(mapId,
                onLoaded: map => RunOnMainThread(() =>
                {
                    try
                    {
                        ApplyMapFile(map, resetDirty: true);
                        CurrentFileName = null;   // an online map isn't a local file yet
                        ShowToast($"Loaded \"{MapName}\"{(ReadOnly ? " (play-only)" : "")} — {LoadReport}");
                        if (ReadOnly && Mode == EditorMode.Editor)
                        {
                            SetMode(EditorMode.Play);
                            ShowToast($"\"{MapName}\" is play-only — straight into Play. {EditorConfig.Settings.TogglePlayKey}: stop playing");
                        }
                    }
                    catch (Exception ex)
                    {
                        ShowToast($"Load error: {ex.Message}");
                    }
                }),
                onError: err => RunOnMainThread(() => ShowToast($"Download failed: {err}")));
        }

        // Delete one of your own online maps (owner-token gated server-side).
        public void DeleteOnlineMap(string mapId)
        {
            if (!EditorConfig.Settings.OwnerTokens.TryGetValue(mapId, out string token))
            {
                ShowToast("You can only delete maps you uploaded");
                return;
            }
            OnlineMaps.Delete(mapId, token, result => RunOnMainThread(() =>
            {
                if (result == "deleted")
                {
                    EditorConfig.Settings.OwnerTokens.Remove(mapId);
                    EditorConfig.Save();
                    ShowToast("Online map deleted");
                    OnlineMaps.RefreshList(force: true);
                }
                else ShowToast($"Delete failed: {result}");
            }));
        }

        // Whether the working map has anything worth sending to a joining peer.
        public bool HasMapContent() => HasWorkingContent();

        // ───────────────────────────────────────────── remote sync (multiplayer) ──
        // Applied by MultiplayerSync when an incoming op wins its per-key
        // last-writer-wins check. Each mutates exactly one unit directly — no wipe,
        // no full rebuild — so co-editing with several people never stutters and
        // never rolls back a newer edit that happened to arrive out of order.

        public void ApplyRemoteObjectUpsert(MapObjectData data)
        {
            if (string.IsNullOrEmpty(data?.Uid)) return;
            try
            {
                var existing = PlacedManager.FindByUid(data.Uid);
                if (existing != null && existing.Root != null)
                {
                    var t = existing.Root.transform;
                    t.position = VecUtil.ToVector3(data.Pos, t.position);
                    t.eulerAngles = VecUtil.ToVector3(data.Rot, t.eulerAngles);
                    t.localScale = VecUtil.ToVector3(data.Scale, t.localScale);
                    existing.GroupId = data.GroupId;
                    if (data.CustomColor != null && data.CustomColor.Length >= 3)
                        PlacedManager.ApplyCustomColor(existing, new Color(data.CustomColor[0], data.CustomColor[1], data.CustomColor[2]));
                    else
                        PlacedManager.ApplyTint(existing, data.Tint);
                    if (data.BoostForce.HasValue) existing.BoostForce = data.BoostForce.Value;
                    if (data.CannonTimer.HasValue) existing.CannonTimer = data.CannonTimer.Value;
                    if (data.CannonTarget != null) existing.CannonTarget = (float[])data.CannonTarget.Clone();
                    existing.CannonLaunchPos = data.CannonLaunchPos != null ? (float[])data.CannonLaunchPos.Clone() : null;
                }
                else
                {
                    if (!Catalog.HasScanned) Catalog.Scan();
                    var source = Catalog.ResolveSource(data.Source, data.SourceName);
                    if (source == null)
                    {
                        // Keep it pending instead of dropping it: dropping would make our
                        // next diff broadcast a phantom DELETE and erase it for the peer
                        // who placed it. The retry loop spawns it once the source loads.
                        _unresolvedObjects.RemoveAll(o => o.Uid == data.Uid);
                        _unresolvedObjects.Add(data);
                        MapEditorPlugin.Logger.LogWarning($"[COOP] Remote object source not found (kept pending): {data.SourceName}");
                        return;
                    }
                    PlacedManager.Spawn(source, data.Source, data.SourceName,
                        VecUtil.ToVector3(data.Pos), Quaternion.Euler(VecUtil.ToVector3(data.Rot)),
                        VecUtil.ToVector3(data.Scale, Vector3.one), data.Tint, restore: data);
                }
                SetDirty();
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[COOP] ApplyRemoteObjectUpsert error: {ex.Message}");
            }
        }

        public void ApplyRemoteObjectDelete(string uid)
        {
            if (_unresolvedObjects.RemoveAll(o => o.Uid == uid) > 0) SetDirty();
            var p = PlacedManager.FindByUid(uid);
            if (p == null) return;
            if (SelectionSys.Current.Placed == p) SelectionSys.Deselect();
            PlacedManager.Delete(p);
            SelectionSys.PruneMulti();
            SetDirty();
        }

        public void ApplyRemoteCheckpointUpsert(CheckpointData data)
        {
            if (string.IsNullOrEmpty(data?.Uid)) return;
            int idx = Checkpoints.FindIndex(c => c.Uid == data.Uid);
            if (idx >= 0) Checkpoints[idx] = data; else Checkpoints.Add(data);
            SetDirty();
        }

        public void ApplyRemoteCheckpointDelete(string uid)
        {
            int idx = Checkpoints.FindIndex(c => c.Uid == uid);
            if (idx < 0) return;
            if (SelectionSys.Current.Marker == "checkpoint")
            {
                if (SelectionSys.Current.MarkerIndex == idx) SelectionSys.Deselect();
                // A removal below the selected index shifts the list — keep pointing at
                // the same checkpoint, not whatever slid into the old slot.
                else if (SelectionSys.Current.MarkerIndex > idx) SelectionSys.Current.MarkerIndex--;
            }
            Checkpoints.RemoveAt(idx);
            SetDirty();
        }

        public void ApplyRemoteResetZoneUpsert(ResetZoneData data)
        {
            if (string.IsNullOrEmpty(data?.Uid)) return;
            int idx = ResetZones.FindIndex(z => z.Uid == data.Uid);
            if (idx >= 0) ResetZones[idx] = data; else ResetZones.Add(data);
            SetDirty();
        }

        public void ApplyRemoteResetZoneDelete(string uid)
        {
            int idx = ResetZones.FindIndex(z => z.Uid == uid);
            if (idx < 0) return;
            if (SelectionSys.Current.Marker == "reset")
            {
                if (SelectionSys.Current.MarkerIndex == idx) SelectionSys.Deselect();
                else if (SelectionSys.Current.MarkerIndex > idx) SelectionSys.Current.MarkerIndex--;
            }
            ResetZones.RemoveAt(idx);
            SetDirty();
        }

        public void ApplyRemoteLevelEditUpsert(LevelEditData data)
        {
            if (data == null) return;
            if (!Catalog.HasScanned) Catalog.Scan();
            LevelEdits.Apply(new List<LevelEditData> { data }, Catalog, out _, out _);
            SetDirty();
        }

        public void ApplyRemoteLevelEditRevert(string path)
        {
            var go = Catalog.ResolveSource(path, null);
            if (go == null) return;
            var record = LevelEdits.Find(go);
            if (record == null) return;
            if (SelectionSys.Current.IsRaw && SelectionSys.Current.Raw == go) SelectionSys.Deselect();
            LevelEdits.Revert(record);
            SetDirty();
        }

        public void ApplyRemoteSpawn(SpawnPointData data)
        {
            Spawn = data;
            if (SelectionSys.Current.Marker == "spawn") SelectionSys.Deselect();
            SetDirty();
        }

        public void ApplyRemoteGoal(GoalZoneData data)
        {
            Goal = data;
            if (SelectionSys.Current.Marker == "goal") SelectionSys.Deselect();
            SetDirty();
        }

        public void ApplyRemoteBaseMode(MapBaseMode mode)
        {
            if (mode == BaseMode) return;
            BaseMode = mode;
            if (mode == MapBaseMode.Blank) BlankCanvas.Apply();
            else BlankCanvas.Restore();
            SetDirty();
        }

        public void ApplyRemoteMapName(string name)
        {
            if (string.IsNullOrEmpty(name) || name == MapName) return;
            MapName = name;
            SetDirty();
        }

        // Synced so a whole-map swap re-keys everyone's leaderboard/upload identity to
        // the same map, instead of each peer keeping their previous MapId.
        public void ApplyRemoteMapId(string mapId)
        {
            if (string.IsNullOrEmpty(mapId) || mapId == MapId) return;
            MapId = mapId;
            RefreshReadOnly();   // the owner-token check keys off MapId
            SetDirty();
        }

        // Synced so a swap TO a play-only map locks every co-editor too — without this,
        // peers kept full editor powers over content they weren't meant to edit.
        public void ApplyRemoteEditable(bool editable)
        {
            if (editable != Editable)
            {
                Editable = editable;
                SetDirty();
            }
            RefreshReadOnly();
        }

        // Re-derive the play-only lock after remote Editable/MapId changes. Arrival
        // order of the two ops is not guaranteed, so both call this.
        private void RefreshReadOnly()
        {
            bool ro = !Editable && !EditorConfig.Settings.OwnerTokens.ContainsKey(MapId ?? "");
            if (ro == ReadOnly) return;
            ReadOnly = ro;
            if (ReadOnly && Mode == EditorMode.Editor)
            {
                // Kicked out mid-edit: SetMode(Play) runs the full leave-editor cleanup
                // (fly off, cursor, autosave); only ENTERING Editor is guarded.
                SetMode(EditorMode.Play);
                ShowToast("Map is play-only now — editor locked");
            }
        }

        // Soccer PLACEMENT sync (kickoff point / goal boxes / scoreboard marker) — the
        // editor-time config, not the live ball position (that's PlayModeController.
        // ApplyRemoteBall over MSG_BALL). Without this a peer who never placed soccer
        // themselves never receives it and their client silently has no match to run.
        public void ApplyRemoteBallMarker(BallData data)
        {
            Ball = data;
            if (SelectionSys.Current.Marker == "ball") SelectionSys.Deselect();
            SetDirty();
        }

        public void ApplyRemoteSoccerGoalUpsert(SoccerGoalData data)
        {
            if (string.IsNullOrEmpty(data?.Uid)) return;
            int idx = SoccerGoals.FindIndex(g => g.Uid == data.Uid);
            if (idx >= 0) SoccerGoals[idx] = data; else SoccerGoals.Add(data);
            SetDirty();
        }

        public void ApplyRemoteSoccerGoalDelete(string uid)
        {
            int idx = SoccerGoals.FindIndex(g => g.Uid == uid);
            if (idx < 0) return;
            if (SelectionSys.Current.Marker == "soccergoal")
            {
                if (SelectionSys.Current.MarkerIndex == idx) SelectionSys.Deselect();
                else if (SelectionSys.Current.MarkerIndex > idx) SelectionSys.Current.MarkerIndex--;
            }
            SoccerGoals.RemoveAt(idx);
            SetDirty();
        }

        public void ApplyRemoteScoreboard(ScoreboardData data)
        {
            Scoreboard = data;
            if (SelectionSys.Current.Marker == "scoreboard") SelectionSys.Deselect();
            SetDirty();
        }

        private void ApplyMapFile(MapFile map, bool resetDirty)
        {
            if (!Catalog.HasScanned) Catalog.Scan();

            PlacedManager.WipeAll();
            LevelEdits.RestoreAll();
            SelectionSys.Deselect();
            Undo.Clear();
            ClearPendingUndo();

            MapName = map.Name ?? "Untitled";
            // Adopt the file's / sender's id so shared maps key the same leaderboard.
            MapId = string.IsNullOrEmpty(map.MapId) ? System.Guid.NewGuid().ToString("N") : map.MapId;
            Editable = map.Editable;
            AuthorName = map.AuthorName;
            AuthorSteamId = map.AuthorSteamId;
            // Play-only maps lock the editor — unless you're the author (you hold its
            // owner token), so you can still update your own non-editable uploads.
            ReadOnly = !map.Editable && !EditorConfig.Settings.OwnerTokens.ContainsKey(MapId);
            Spawn = map.Spawn;
            Goal = map.Goal;
            Checkpoints = map.Checkpoints != null ? new List<CheckpointData>(map.Checkpoints) : new List<CheckpointData>();
            ResetZones = map.ResetZones != null ? new List<ResetZoneData>(map.ResetZones) : new List<ResetZoneData>();
            Ball = map.Ball;
            SoccerGoals = map.SoccerGoals != null ? new List<SoccerGoalData>(map.SoccerGoals) : new List<SoccerGoalData>();
            Scoreboard = map.Scoreboard;
            // Backfill stable ids on maps saved before multiplayer sync/grouping existed.
            foreach (var cp in Checkpoints) cp.Uid ??= Guid.NewGuid().ToString("N");
            foreach (var z in ResetZones) z.Uid ??= Guid.NewGuid().ToString("N");
            if (Ball != null) Ball.Uid ??= Guid.NewGuid().ToString("N");
            foreach (var g in SoccerGoals) g.Uid ??= Guid.NewGuid().ToString("N");
            if (Scoreboard != null) Scoreboard.Uid ??= Guid.NewGuid().ToString("N");
            BaseMode = map.BaseMode;

            if (BaseMode == MapBaseMode.Blank) BlankCanvas.Apply();
            else BlankCanvas.Restore();

            _unresolvedObjects.Clear();
            int loaded = 0, pending = 0;
            foreach (var obj in map.Objects)
            {
                obj.Uid ??= Guid.NewGuid().ToString("N");   // pre-v7 files: stable key for sync/retry
                var source = Catalog.ResolveSource(obj.Source, obj.SourceName);
                if (source == null)
                {
                    pending++;
                    _unresolvedObjects.Add(obj);
                    MapEditorPlugin.Logger.LogWarning(
                        $"[MAP] Source not found (kept pending, will retry) for '{obj.SourceName}' ({obj.Source})");
                    continue;
                }
                var placed = PlacedManager.Spawn(source, obj.Source, obj.SourceName,
                    VecUtil.ToVector3(obj.Pos),
                    Quaternion.Euler(VecUtil.ToVector3(obj.Rot)),
                    VecUtil.ToVector3(obj.Scale, Vector3.one),
                    obj.Tint, restore: obj);
                if (placed != null) loaded++;
                else { pending++; _unresolvedObjects.Add(obj); }
            }
            _unresolvedRetryAt = Time.time + 5f;

            LevelEdits.Apply(map.LevelEdits, Catalog, out int editsApplied, out int editsSkipped);

            LoadReport = pending == 0
                ? $"{loaded} objects"
                : $"{loaded} objects, {pending} pending (source not loaded yet — kept, retrying)";
            if (editsApplied + editsSkipped > 0)
                LoadReport += editsSkipped == 0
                    ? $", {editsApplied} level edits"
                    : $", {editsApplied} level edits ({editsSkipped} skipped)";
            if (resetDirty)
            {
                Dirty = false;
                _autosaveDirtySince = -1f;
            }
            RefreshSnapshot();
            // Swapping the whole working map (load / online download) must reach the
            // other editors like any other change: the per-key diff turns it into
            // DELETEs of the old content + upserts of the new. After a mere scene
            // re-apply the diff is empty, so this is a no-op there.
            Multiplayer?.NotifyDirty();
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
            if (ReadOnlyBlock()) return;
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

            var undoTarget = placed;
            Undo.Push($"place {entry.DisplayName}", () =>
            {
                if (SelectionSys.Current.Placed == undoTarget) SelectionSys.Deselect();
                PlacedManager.Delete(undoTarget);
                SetDirty();
            });

            SelectionSys.Select(placed);
            SetDirty();
        }

        public void DuplicateSelected()
        {
            if (ReadOnlyBlock()) return;
            if (SelectionSys.IsMulti)
            {
                MultiDuplicate();
                return;
            }

            var sel = SelectionSys.Current;
            if (!sel.IsValid || sel.Target == null) return;

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
            // A duplicate is a new independent object: fresh Uid, no inherited group.
            var restore = sel.IsPlaced ? sel.Placed.ToData() : null;
            if (restore != null) { restore.Uid = null; restore.GroupId = null; }
            var placed = PlacedManager.Spawn(source, sourcePath, sourceName,
                t.position + offset, t.rotation, scale, tint, restore);
            if (placed != null)
            {
                // A duplicated cannon/aimed pad keeps its aim relative to itself.
                if (placed.Mechanic != MechanicType.None && placed.CannonTarget != null)
                    placed.CannonTarget = VecUtil.ToArray(VecUtil.ToVector3(placed.CannonTarget) + offset);

                var undoTarget = placed;
                Undo.Push($"duplicate of {sourceName}", () =>
                {
                    if (SelectionSys.Current.Placed == undoTarget) SelectionSys.Deselect();
                    PlacedManager.Delete(undoTarget);
                    SetDirty();
                });

                SelectionSys.Select(placed);
                SetDirty();
                ShowToast($"Duplicated: {placed.Root.name}");
            }
        }

        // Transform that menu/keyboard mutations act on: a placed clone's root or an
        // original level object (whose pristine state gets captured first). Markers are
        // gizmo-only. Pair every use with EndTransformEdit().
        private Transform BeginTransformEdit()
        {
            if (ReadOnlyBlock()) return null;
            var sel = SelectionSys.Current;
            if (!sel.IsValid) return null;
            if (sel.IsMarker)
            {
                ShowToast("Markers: drag the gizmo, or use the Set Spawn/Goal Here buttons");
                return null;
            }
            if (sel.IsRaw) LevelEdits.CaptureOriginal(sel.Raw);
            CaptureTransformUndo(sel.Target, sel.IsRaw);
            return sel.Target.transform;
        }

        private void EndTransformEdit()
        {
            var sel = SelectionSys.Current;
            if (sel.IsRaw) LevelEdits.RecordTransform(sel.Raw);
            PushTransformUndoIfChanged();
            SetDirty();
        }

        public void MoveSelected(Vector3 delta)
        {
            if (SelectionSys.IsMulti)
            {
                MultiTransform("group move", (t, _) => t.position += delta);
                return;
            }
            var single = BeginTransformEdit();
            if (single == null) return;
            single.position += delta;
            EndTransformEdit();
        }

        public void RotateSelectedY(float degrees)
        {
            if (SelectionSys.IsMulti)
            {
                // Group rotation pivots on the centroid, like the gizmo.
                var q = Quaternion.AngleAxis(degrees, Vector3.up);
                MultiTransform("group rotate", (t, centroid) =>
                {
                    t.position = centroid + q * (t.position - centroid);
                    t.rotation = q * t.rotation;
                });
                return;
            }
            var single = BeginTransformEdit();
            if (single == null) return;
            single.Rotate(0f, degrees, 0f, Space.World);
            EndTransformEdit();
        }

        public void RotateSelected(Vector3 axis, float degrees)
        {
            var t = BeginTransformEdit();
            if (t == null) return;
            t.Rotate(axis, degrees, Space.World);
            EndTransformEdit();
        }

        // Absolute rotation: type the exact world euler angles instead of adding steps.
        public void SetRotationSelected(Vector3 euler)
        {
            var t = BeginTransformEdit();
            if (t == null) return;
            t.eulerAngles = euler;
            EndTransformEdit();
        }

        public void ScaleSelected(float delta)
        {
            if (SelectionSys.IsMulti)
            {
                MultiTransform("group scale", (t, _) =>
                {
                    var ms = t.localScale + Vector3.one * delta;
                    ms.x = Mathf.Max(0.05f, ms.x);
                    ms.y = Mathf.Max(0.05f, ms.y);
                    ms.z = Mathf.Max(0.05f, ms.z);
                    t.localScale = ms;
                });
                return;
            }
            var single = BeginTransformEdit();
            if (single == null) return;
            var s = single.localScale + Vector3.one * delta;
            s.x = Mathf.Max(0.05f, s.x);
            s.y = Mathf.Max(0.05f, s.y);
            s.z = Mathf.Max(0.05f, s.z);
            single.localScale = s;
            EndTransformEdit();
        }

        // Per-axis scale step along the object's local axes (menu buttons).
        public void ScaleSelectedAxis(int axis, float delta)
        {
            var t = BeginTransformEdit();
            if (t == null) return;
            var s = t.localScale;
            if (axis == 0) s.x = Mathf.Max(0.05f, s.x + delta);
            else if (axis == 1) s.y = Mathf.Max(0.05f, s.y + delta);
            else s.z = Mathf.Max(0.05f, s.z + delta);
            t.localScale = s;
            EndTransformEdit();
        }

        // Absolute scale: multiplier over the object's original scale, so 1 = original
        // size and 2 = twice as big regardless of previous tweaking.
        public void SetScaleFactorSelected(float factor)
        {
            factor = Mathf.Max(0.05f, factor);
            var sel = SelectionSys.Current;
            var t = BeginTransformEdit();   // arms Ctrl+Z and captures raw originals
            if (t == null) return;
            Vector3 baseScale = sel.IsPlaced
                ? sel.Placed.OriginalScale
                : LevelEdits.Find(sel.Raw)?.OrigScale ?? t.localScale;
            t.localScale = baseScale * factor;
            EndTransformEdit();
        }

        public void ResetSelected()
        {
            var sel = SelectionSys.Current;
            if (sel.IsPlaced)
            {
                var t = BeginTransformEdit();
                if (t == null) return;
                t.rotation = Quaternion.identity;
                t.localScale = sel.Placed.OriginalScale;
                EndTransformEdit();
            }
            else if (sel.IsRaw)
            {
                // Level object: back to exactly how the game placed it.
                var record = LevelEdits.Find(sel.Raw);
                if (record == null) return;   // never edited — nothing to reset
                Vector3 origPos = record.OrigPos;
                Quaternion origRot = record.OrigRot;
                Vector3 origScale = record.OrigScale;
                var t = BeginTransformEdit();
                if (t == null) return;
                t.position = origPos;
                t.rotation = origRot;
                t.localScale = origScale;
                EndTransformEdit();   // RecordTransform sees it pristine and prunes the record
            }
        }

        public void AlignSelected()
        {
            var t = BeginTransformEdit();
            if (t == null) return;
            var p = t.position;
            t.position = new Vector3(Mathf.Round(p.x * 2f) / 2f, Mathf.Round(p.y * 2f) / 2f, Mathf.Round(p.z * 2f) / 2f);
            var e = t.eulerAngles;
            t.eulerAngles = new Vector3(Mathf.Round(e.x / 15f) * 15f, Mathf.Round(e.y / 15f) * 15f, Mathf.Round(e.z / 15f) * 15f);
            EndTransformEdit();
        }

        public void DropSelectedToFloor()
        {
            var targetT = BeginTransformEdit();
            if (targetT == null) return;
            var root = targetT.gameObject;
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
                EndTransformEdit();
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[EDIT] DropToFloor error: {ex.Message}");
            }
        }

        public void MultiClone(int count, Vector3 direction, bool stairs)
        {
            if (ReadOnlyBlock()) return;
            var sel = SelectionSys.Current;
            if (!sel.IsValid || sel.IsMarker || sel.Target == null) return;

            GameObject sourceGo = sel.Target;
            string sourcePath = sel.IsPlaced ? sel.Placed.SourcePath : ObjectCatalog.BuildPath(sel.Raw.transform);
            string sourceName = sel.IsPlaced ? sel.Placed.SourceName : sel.Raw.name;
            TintColor tint = sel.IsPlaced ? sel.Placed.Tint : TintColor.None;

            var bounds = ObjectCatalog.ComputeBounds(sourceGo);
            if (bounds.size == Vector3.zero) return;

            float stepAlong = Mathf.Abs(Vector3.Dot(bounds.size, direction));
            if (stepAlong < 0.05f) stepAlong = 1f;
            float stepUp = stairs ? bounds.size.y : 0f;

            var t = sourceGo.transform;
            PlacedObject last = null;
            var spawnedList = new List<PlacedObject>();
            // Each copy is independent: fresh Uid per spawn, no inherited group.
            var restore = sel.IsPlaced ? sel.Placed.ToData() : null;
            if (restore != null) { restore.Uid = null; restore.GroupId = null; }
            for (int i = 1; i <= count; i++)
            {
                Vector3 shift = direction * stepAlong * i + Vector3.up * stepUp * i;
                Vector3 pos = t.position + shift;
                last = PlacedManager.Spawn(sourceGo, sourcePath, sourceName,
                    pos, t.rotation, t.localScale, tint, restore);
                if (last != null)
                {
                    if (last.Mechanic != MechanicType.None && last.CannonTarget != null)
                        last.CannonTarget = VecUtil.ToArray(VecUtil.ToVector3(last.CannonTarget) + shift);
                    spawnedList.Add(last);
                }
            }
            if (last != null)
            {
                Undo.Push($"multi-clone ×{spawnedList.Count}", () =>
                {
                    foreach (var p in spawnedList)
                    {
                        if (SelectionSys.Current.Placed == p) SelectionSys.Deselect();
                        PlacedManager.Delete(p);
                    }
                    SetDirty();
                });

                SelectionSys.Select(last);
                SetDirty();
                ShowToast($"Multi-clone: {count} copies{(stairs ? " as stairs" : "")}");
            }
        }

        public void DeleteSelected()
        {
            if (ReadOnlyBlock()) return;
            if (SelectionSys.IsMulti)
            {
                MultiDelete();
                return;
            }

            var sel = SelectionSys.Current;
            if (!sel.IsValid) return;

            if (sel.IsMarker)
            {
                if (sel.Marker == "cannontarget")
                {
                    // Mechanics are always aimed — move the ring, or delete the object.
                    ShowToast("Launch targets can't be deleted — move it, or delete the object");
                    SelectionSys.Deselect();
                    return;
                }
                if (sel.Marker == "cannonlaunch")
                {
                    // Del on the launch point = back to the automatic position.
                    var owner = PlacedManager.FindById(sel.MarkerIndex);
                    if (owner != null && owner.CannonLaunchPos != null)
                    {
                        var restore = CaptureCannonLaunchState(owner);
                        if (restore != null) Undo.Push("launch point reset", restore);
                        owner.CannonLaunchPos = null;
                        SetDirty();
                        ShowToast("Launch point reset to automatic");
                    }
                    SelectionSys.Deselect();
                    return;
                }
                Undo.Push($"delete of {sel.DisplayName}", CaptureMarkersState());
                switch (sel.Marker)
                {
                    case "goal":
                        ClearGoal();
                        ShowToast("Goal removed");
                        break;
                    case "spawn":
                        ClearSpawn();
                        ShowToast("Spawn removed");
                        break;
                    case "checkpoint":
                        if (sel.MarkerIndex < Checkpoints.Count)
                        {
                            Checkpoints.RemoveAt(sel.MarkerIndex);
                            SetDirty();
                            ShowToast("Checkpoint removed");
                        }
                        break;
                    case "reset":
                        if (sel.MarkerIndex < ResetZones.Count)
                        {
                            ResetZones.RemoveAt(sel.MarkerIndex);
                            SetDirty();
                            ShowToast("Reset trigger removed");
                        }
                        break;
                    case "ball":
                        Ball = null;
                        SetDirty();
                        ShowToast("Ball removed");
                        break;
                    case "soccergoal":
                        if (sel.MarkerIndex < SoccerGoals.Count)
                        {
                            SoccerGoals.RemoveAt(sel.MarkerIndex);
                            SetDirty();
                            ShowToast("Goal removed");
                        }
                        break;
                    case "scoreboard":
                        Scoreboard = null;
                        SetDirty();
                        ShowToast("Scoreboard removed");
                        break;
                }
                SelectionSys.Deselect();
                return;
            }

            if (sel.IsPlaced)
            {
                DeletePlacedSingle(sel.Placed);
                return;
            }

            // Original level object: hide it (revertible from the LIST tab), never destroy.
            var rawName = sel.Raw.name;
            var rawGo = sel.Raw;
            LevelEdits.Hide(rawGo);
            SelectionSys.Deselect();

            Undo.Push($"hide of {rawName}", () =>
            {
                var record = LevelEdits.Find(rawGo);
                if (record != null)
                {
                    LevelEdits.Unhide(record);
                    LevelEdits.RecordTransform(rawGo); // prunes the record if now pristine
                }
                SetDirty();
            });

            SetDirty();
            ShowToast($"Level object hidden: {rawName} (revert from LIST)");
        }

        // Delete exactly ONE placed object, no group expansion — the LIST tab's per-row
        // X button must remove that row only, even when the object belongs to a group
        // (Select() would pull in every member and MultiDelete the lot).
        public void DeletePlacedSingle(PlacedObject placed)
        {
            if (ReadOnlyBlock()) return;
            if (placed?.Root == null) return;

            var name = placed.Root.name;
            var data = placed.ToData();
            if (SelectionSys.Current.Placed == placed) SelectionSys.Deselect();
            PlacedManager.Delete(placed);
            SelectionSys.PruneMulti();

            Undo.Push($"delete of {name}", () =>
            {
                var src = Catalog.ResolveSource(data.Source, data.SourceName);
                if (src == null)
                {
                    ShowToast($"Undo failed: source \"{data.SourceName}\" not found");
                    return;
                }
                var respawned = PlacedManager.Spawn(src, data.Source, data.SourceName,
                    VecUtil.ToVector3(data.Pos),
                    Quaternion.Euler(VecUtil.ToVector3(data.Rot)),
                    VecUtil.ToVector3(data.Scale, Vector3.one),
                    data.Tint, restore: data);
                if (respawned != null) SelectionSys.Select(respawned);
                SetDirty();
            });

            SetDirty();
            ShowToast($"Deleted: {name}");
        }

        public void WipeCustomObjects()
        {
            if (ReadOnlyBlock()) return;
            // Pending (unresolved) objects are custom objects too: the wipe takes them,
            // and the undo snapshot brings them back through the normal restore path.
            var datas = PlacedManager.Snapshot();
            datas.AddRange(_unresolvedObjects);
            int n = datas.Count;
            _unresolvedObjects.Clear();
            PlacedManager.WipeAll();
            SelectionSys.Deselect();

            Undo.Push($"wipe of {n} objects", () =>
            {
                int restored = 0;
                foreach (var data in datas)
                {
                    var src = Catalog.ResolveSource(data.Source, data.SourceName);
                    if (src == null) continue;
                    if (PlacedManager.Spawn(src, data.Source, data.SourceName,
                        VecUtil.ToVector3(data.Pos),
                        Quaternion.Euler(VecUtil.ToVector3(data.Rot)),
                        VecUtil.ToVector3(data.Scale, Vector3.one),
                        data.Tint, restore: data) != null) restored++;
                }
                SetDirty();
                ShowToast($"Restored {restored} objects");
            });

            SetDirty();
            ShowToast($"Deleted {n} custom objects");
        }

        public void TpPlayerOntoSelected()
        {
            var sel = SelectionSys.Current;
            if (!sel.IsValid || sel.Target == null) return;
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
            var cam = Finder.FindCameraTransform();
            if (cam == null) return;
            Vector3 dest = cam.position + cam.forward * 6f;

            // Marker circles (cannon landing / launch, aimed pad landing) aren't real
            // transforms — write the world point straight into the placed object's data.
            var sel = SelectionSys.Current;
            if (sel.IsMarker)
            {
                switch (sel.Marker)
                {
                    case "cannontarget":
                    {
                        var pc = PlacedManager.FindById(sel.MarkerIndex);
                        if (pc?.CannonTarget == null) return;
                        Undo.Push($"bring landing of #{pc.Id:000}", CaptureCannonTargetState(pc));
                        pc.CannonTarget = VecUtil.ToArray(dest);
                        SetDirty();
                        ShowToast("Landing point brought here");
                        return;
                    }
                    case "cannonlaunch":
                    {
                        var lc = PlacedManager.FindById(sel.MarkerIndex);
                        if (lc == null || lc.Mechanic != MechanicType.Cannon || lc.Root == null) return;
                        lc.CannonLaunchPos ??= VecUtil.ToArray(MechanicsController.GetLaunchPos(lc));
                        Undo.Push($"bring launch of #{lc.Id:000}", CaptureCannonLaunchState(lc));
                        lc.CannonLaunchPos = VecUtil.ToArray(dest);
                        SetDirty();
                        ShowToast("Launch point brought here");
                        return;
                    }
                    default:
                        ShowToast("Bring works on objects and cannon/pad circles");
                        return;
                }
            }

            var t = BeginTransformEdit();
            if (t == null) return;
            t.position = dest;
            EndTransformEdit();
        }

        public void RevertLevelEdit(LevelEditRecord record)
        {
            if (record == null) return;
            if (SelectionSys.Current.IsRaw && SelectionSys.Current.Raw == record.Target)
                SelectionSys.Deselect();
            LevelEdits.Revert(record);
            SetDirty();
            ShowToast($"Level edit reverted: {record.Name}");
        }

        public void RestoreAllLevelEdits()
        {
            int n = LevelEdits.Count;
            if (SelectionSys.Current.IsRaw) SelectionSys.Deselect();
            LevelEdits.RestoreAll();
            SetDirty();
            ShowToast($"Reverted {n} level edits");
        }

        // ─────────────────────────────────────────────────────── spawn/goal/base ──

        public void SetSpawnHere()
        {
            if (ReadOnlyBlock()) return;
            var t = Finder.FindPlayerTransform();
            var cam = Finder.FindCameraTransform();
            if (t == null) return;
            Undo.Push("spawn change", CaptureMarkersState());
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
            if (Spawn != null) Undo.Push("spawn removal", CaptureMarkersState());
            Spawn = null;
            if (SelectionSys.Current.Marker == "spawn") SelectionSys.Deselect();
            SetDirty();
        }

        public void SetGoalHere()
        {
            if (ReadOnlyBlock()) return;
            var t = Finder.FindPlayerTransform();
            if (t == null) return;
            Undo.Push("goal change", CaptureMarkersState());
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
            if (Goal != null) Undo.Push("goal removal", CaptureMarkersState());
            Goal = null;
            if (SelectionSys.Current.Marker == "goal") SelectionSys.Deselect();
            SetDirty();
        }

        public void AddCheckpointHere()
        {
            if (ReadOnlyBlock()) return;
            var t = Finder.FindPlayerTransform();
            var cam = Finder.FindCameraTransform();
            if (t == null) return;
            Undo.Push("checkpoint add", CaptureMarkersState());
            Checkpoints.Add(new CheckpointData
            {
                Uid = Guid.NewGuid().ToString("N"),
                Pos = VecUtil.ToArray(t.position),
                Yaw = cam != null ? cam.eulerAngles.y : t.eulerAngles.y,
                Radius = 1.5f,
            });
            SelectionSys.SelectMarker("checkpoint", Checkpoints.Count - 1);
            SetDirty();
            ShowToast($"Checkpoint #{Checkpoints.Count} placed here");
        }

        // Goal-style checkpoint: an oriented box (movable/rotatable/scalable with the
        // gizmo) that acts exactly like a coin — touching it sets the respawn point.
        public void AddBoxCheckpointHere()
        {
            if (ReadOnlyBlock()) return;
            var t = Finder.FindPlayerTransform();
            var cam = Finder.FindCameraTransform();
            if (t == null) return;
            Undo.Push("checkpoint add", CaptureMarkersState());
            Checkpoints.Add(new CheckpointData
            {
                Uid = Guid.NewGuid().ToString("N"),
                Pos = VecUtil.ToArray(t.position + Vector3.up * 1f),
                Yaw = cam != null ? cam.eulerAngles.y : t.eulerAngles.y,
                Size = new float[] { 4f, 4f, 4f },
            });
            SelectionSys.SelectMarker("checkpoint", Checkpoints.Count - 1);
            SetDirty();
            ShowToast($"Box checkpoint #{Checkpoints.Count} placed here — scale/rotate it with the gizmo");
        }

        public void AddResetZoneHere()
        {
            if (ReadOnlyBlock()) return;
            var t = Finder.FindPlayerTransform();
            if (t == null) return;
            Undo.Push("reset-trigger add", CaptureMarkersState());
            ResetZones.Add(new ResetZoneData
            {
                Uid = Guid.NewGuid().ToString("N"),
                Center = VecUtil.ToArray(t.position + Vector3.up * 1f),
                Size = new float[] { 4f, 4f, 4f },
            });
            SelectionSys.SelectMarker("reset", ResetZones.Count - 1);
            SetDirty();
            ShowToast($"Reset trigger #{ResetZones.Count} placed here (invisible in play)");
        }

        // ───────────────────────────────────────────────────────────── soccer ──

        // Place (or move) the single ball kickoff / centre point at the player.
        public void PlaceBallHere()
        {
            if (ReadOnlyBlock()) return;
            var t = Finder.FindPlayerTransform();
            if (t == null) return;
            Undo.Push("ball placement", CaptureMarkersState());
            Ball = new BallData
            {
                Uid = Ball?.Uid ?? Guid.NewGuid().ToString("N"),
                Center = VecUtil.ToArray(t.position + Vector3.up * 1f),
                Radius = Ball?.Radius ?? 0.5f,
            };
            SelectionSys.SelectMarker("ball", 0);
            SetDirty();
            ShowToast("Ball kickoff point placed — scale it with the gizmo to size the ball");
        }

        public void RemoveBall()
        {
            if (Ball != null) Undo.Push("ball removal", CaptureMarkersState());
            Ball = null;
            if (SelectionSys.Current.Marker == "ball") SelectionSys.Deselect();
            SetDirty();
        }

        public void AddSoccerGoalHere(int team)
        {
            if (ReadOnlyBlock()) return;
            var t = Finder.FindPlayerTransform();
            if (t == null) return;
            Undo.Push("goal box add", CaptureMarkersState());
            SoccerGoals.Add(new SoccerGoalData
            {
                Uid = Guid.NewGuid().ToString("N"),
                Center = VecUtil.ToArray(t.position + Vector3.up * 1f),
                Size = new float[] { 4f, 4f, 4f },
                Team = Mathf.Clamp(team, 0, 1),
            });
            SelectionSys.SelectMarker("soccergoal", SoccerGoals.Count - 1);
            SetDirty();
            ShowToast($"Goal (Team {(team == 0 ? "A" : "B")}) added — scale/rotate it with the gizmo");
        }

        // Place (or move) the 3D scoreboard in front of the camera, facing the player.
        public void PlaceScoreboardHere()
        {
            if (ReadOnlyBlock()) return;
            var cam = Finder.FindCameraTransform();
            var t = Finder.FindPlayerTransform();
            if (cam == null && t == null) return;

            Vector3 basePos = cam != null ? cam.position + cam.forward * 8f : t.position + Vector3.up * 3f;
            float yaw = cam != null ? cam.eulerAngles.y + 180f : 0f;   // face back at the camera

            Undo.Push("scoreboard placement", CaptureMarkersState());
            Scoreboard = new ScoreboardData
            {
                Uid = Scoreboard?.Uid ?? Guid.NewGuid().ToString("N"),
                Pos = VecUtil.ToArray(basePos + Vector3.up * 1.5f),
                Rot = new float[] { 0f, yaw, 0f },
                Scale = Scoreboard?.Scale ?? 2f,
            };
            SelectionSys.SelectMarker("scoreboard", 0);
            SetDirty();
            ShowToast("Scoreboard placed — move/rotate/scale it with the gizmo");
        }

        // Flip which team a goal box belongs to (menu button on the selected goal).
        public void ToggleSoccerGoalTeam(int index)
        {
            if (ReadOnlyBlock()) return;
            if (index < 0 || index >= SoccerGoals.Count) return;
            Undo.Push("goal team change", CaptureMarkersState());
            SoccerGoals[index].Team = 1 - SoccerGoals[index].Team;
            SetDirty();
        }

        public void AdjustGoalSize(float delta)
        {
            if (ReadOnlyBlock()) return;
            if (Goal == null) return;
            Undo.Push("goal resize", CaptureMarkersState());
            var size = VecUtil.ToVector3(Goal.Size, Vector3.one * 4f) + Vector3.one * delta;
            size.x = Mathf.Max(1f, size.x);
            size.y = Mathf.Max(1f, size.y);
            size.z = Mathf.Max(1f, size.z);
            Goal.Size = VecUtil.ToArray(size);
            SetDirty();
        }

        // "Clear space" button: hide the whole original level like Blank canvas does,
        // but with no starting platform and no spawn changes — a truly empty void.
        public void WipeLevelGeometry()
        {
            if (ReadOnlyBlock()) return;
            if (BaseMode == MapBaseMode.Blank)
            {
                ShowToast("Level is already hidden (Blank canvas)");
                return;
            }
            Undo.Push("level wipe", () => SetBaseMode(MapBaseMode.Overlay));
            BaseMode = MapBaseMode.Blank;
            BlankCanvas.Apply();
            SetDirty();
            ShowToast("Level wiped — clean space. Overlay (or Ctrl+Z) brings it back.");
        }

        public void SetBaseMode(MapBaseMode mode)
        {
            if (ReadOnlyBlock()) return;
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
