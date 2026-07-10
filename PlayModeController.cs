using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    public enum TimerState { Idle, Armed, Running, Finished }
    public enum UploadPromptState { None, Offered }

    // Play-mode runtime: teleport to the map spawn, run timer, goal detection by
    // bounds polling (no trigger callbacks — unreliable under IL2CPP), fall respawn.
    public class PlayModeController
    {
        private readonly GameObjectFinder _finder;

        public TimerState Timer { get; private set; } = TimerState.Idle;
        public double ElapsedSeconds { get; private set; }
        public double? BestTime { get; private set; }
        public bool NewBest { get; private set; }

        private double _startTime;
        private double _pausedAccum;      // total seconds spent in the pause menu this run
        private double _pauseEnteredAt;
        private bool _wasPaused;
        private bool _pauseUnavailable;   // EHS.GameManager.IsPauseMenuShown missing → treat as never paused
        private Vector3 _spawnPos;
        private float _spawnYaw;
        private bool _hasSpawn;
        private GoalZoneData _goal;
        private string _mapName = "";
        private string _mapId = "";

        // Post-run leaderboard upload: every finished run offers to upload, whether or
        // not it's a new best. OnRunFinished fires first so the owner can veto (backend
        // not configured, uploads disabled, no Steam identity) by calling
        // DismissUploadPrompt(); OnUploadConfirmed fires only when the player says yes.
        public UploadPromptState UploadPrompt { get; private set; } = UploadPromptState.None;
        public double PendingUploadSeconds { get; private set; }
        public bool PendingUploadIsNewBest { get; private set; }
        public Action OnRunFinished;
        public Action<double> OnUploadConfirmed;

        public void ConfirmUpload()
        {
            if (UploadPrompt != UploadPromptState.Offered) return;
            UploadPrompt = UploadPromptState.None;
            try { OnUploadConfirmed?.Invoke(PendingUploadSeconds); }
            catch (Exception ex) { MapEditorPlugin.Logger.LogWarning($"[TIMES] OnUploadConfirmed error: {ex.Message}"); }
        }

        public void DismissUploadPrompt() => UploadPrompt = UploadPromptState.None;

        // Checkpoints / reset triggers
        private List<CheckpointData> _checkpoints = new List<CheckpointData>();
        private List<ResetZoneData> _resetZones = new List<ResetZoneData>();
        private float _resetCooldownUntil = 0f;

        public int ActiveCheckpoint { get; private set; } = -1;
        public int CheckpointCount => _checkpoints.Count;

        private readonly LineBox _goalBeacon = new LineBox("FIH_Line_GoalBeacon");
        private readonly List<LineBox> _checkpointRings = new List<LineBox>();

        public PlayModeController(GameObjectFinder finder)
        {
            _finder = finder;
        }

        public void Enter(SpawnPointData spawn, GoalZoneData goal, bool blankMode, string mapName,
                          string mapId, List<CheckpointData> checkpoints = null, List<ResetZoneData> resetZones = null)
        {
            _goal = goal;   // blankMode no longer changes play behaviour; kept for signature stability
            _mapName = mapName ?? "";
            _mapId = mapId ?? "";
            _hasSpawn = spawn?.Pos != null;
            _checkpoints = checkpoints ?? new List<CheckpointData>();
            _resetZones = resetZones ?? new List<ResetZoneData>();
            ActiveCheckpoint = -1;
            NewBest = false;

            BestTime = null;
            if (!string.IsNullOrEmpty(_mapName)
                && EditorConfig.Settings.BestTimes.TryGetValue(_mapName, out double best))
                BestTime = best;

            if (_hasSpawn)
            {
                _spawnPos = VecUtil.ToVector3(spawn.Pos);
                _spawnYaw = spawn.Yaw;
                TeleportToSpawn();
                Timer = TimerState.Armed;
            }
            else
            {
                // No custom spawn: play from wherever the player is; timer arms in place.
                var t = _finder.FindPlayerTransform();
                _spawnPos = t != null ? t.position : Vector3.zero;
                _spawnYaw = 0f;
                Timer = TimerState.Armed;
            }
            ElapsedSeconds = 0;
            _pausedAccum = 0;
            _wasPaused = false;
            _warnedZoneLoop = false;
            UploadPrompt = UploadPromptState.None;
        }

        public void Exit()
        {
            Timer = TimerState.Idle;
            UploadPrompt = UploadPromptState.None;
            _goalBeacon.Hide();
            foreach (var ring in _checkpointRings) ring.Hide();
        }

        public void RestartRun()
        {
            ActiveCheckpoint = -1;   // full restart: back to spawn, checkpoints forgotten
            TeleportToSpawn();
            Timer = TimerState.Armed;
            ElapsedSeconds = 0;
            _pausedAccum = 0;
            _wasPaused = false;
            NewBest = false;
            UploadPrompt = UploadPromptState.None;
        }

        // R key: retry from the last collected coin, keeping the run timer going.
        // Falls back to a full restart when no coin was collected yet (or the run
        // is over). Shift+R always forces the full restart.
        public void QuickRestart()
        {
            if (Timer == TimerState.Running && ActiveCheckpoint >= 0)
            {
                RespawnAtCheckpoint();
                return;
            }
            RestartRun();
        }

        // Diagnostics: distinguish OUR teleports from the game moving the player on
        // its own (the "keeps restarting me" reports need to know which one it is).
        private Vector3 _lastPos;
        private bool _movedByUs;

        public void Update()
        {
            if (Timer == TimerState.Idle) return;

            var playerT = _finder.FindPlayerTransform();
            if (playerT == null) return;
            Vector3 pos = playerT.position;

            if (!_movedByUs && (pos - _lastPos).sqrMagnitude > 100f && _lastPos != Vector3.zero)
                MapEditorPlugin.Logger.LogWarning(
                    $"[PLAY] EXTERNAL teleport (not the mod): {_lastPos} -> {pos} (timer={Timer}, activeCp={ActiveCheckpoint})");
            _lastPos = pos;
            _movedByUs = false;

            // Show a subtle beacon at the goal so the player can find it.
            if (_goal?.Center != null && Timer != TimerState.Finished)
            {
                var bounds = GoalBounds();
                _goalBeacon.ShowBeacon(bounds, new Color(0.3f, 1f, 0.5f, 0.8f), Mathf.Max(bounds.size.y, 6f));
            }

            DrawCheckpointRings();

            // Track the pause menu so the run timer (and goal/checkpoint/reset checks)
            // freeze while it's open, instead of counting menu-browsing time.
            bool paused = IsGamePaused();
            if (paused && !_wasPaused) _pauseEnteredAt = Time.unscaledTimeAsDouble;
            else if (!paused && _wasPaused) _pausedAccum += Time.unscaledTimeAsDouble - _pauseEnteredAt;
            _wasPaused = paused;

            switch (Timer)
            {
                case TimerState.Armed:
                    // Start on the first real displacement (robust vs. physics settling).
                    // The pause menu can't be open yet with no input taken, but guard anyway.
                    if (!paused && Vector3.Distance(pos, _spawnPos) > 0.05f)
                    {
                        Timer = TimerState.Running;
                        _startTime = Time.unscaledTimeAsDouble;
                    }
                    break;

                case TimerState.Running:
                    if (paused) break; // frozen: don't advance elapsed or check the goal

                    ElapsedSeconds = Time.unscaledTimeAsDouble - _startTime - _pausedAccum;

                    if (_goal?.Center != null && VecUtil.ObbContains(pos,
                            VecUtil.ToVector3(_goal.Center),
                            VecUtil.ToVector3(_goal.Size, Vector3.one * 3f),
                            VecUtil.ToRotation(_goal.Rot)))
                    {
                        Timer = TimerState.Finished;
                        _goalBeacon.Hide();
                        SaveBestTime();
                    }
                    break;
            }

            if (Timer != TimerState.Finished && !paused)
            {
                UpdateCheckpoints(pos);
                UpdateResetZones(pos);
                // No automatic fall respawn: falling forever is the player's business.
                // Map makers who want one place a reset trigger where they need it.
            }
        }

        // EHS.GameManager.IsPauseMenuShown is a public static bool in the referenced
        // Assembly-CSharp.dll — direct compile-time access, guarded in case a future
        // game build renames or removes it.
        private bool IsGamePaused()
        {
            if (_pauseUnavailable) return false;
            try
            {
                return EHS.GameManager.IsPauseMenuShown;
            }
            catch (Exception ex)
            {
                _pauseUnavailable = true;
                MapEditorPlugin.Logger.LogWarning($"[PLAY] Pause detection unavailable: {ex.Message}");
                return false;
            }
        }

        private void UpdateCheckpoints(Vector3 playerPos)
        {
            // Last-touched wins, whatever the creation order — coins and boxes added at
            // different times must stay interchangeable mid-course. (An index-ordered
            // "forward-only" rule broke mixing: a box created last deactivated every
            // earlier-made coin the moment it was touched.)
            for (int i = 0; i < _checkpoints.Count; i++)
            {
                if (i == ActiveCheckpoint) continue;
                var cp = _checkpoints[i];
                if (cp?.Pos == null) continue;
                bool touched;
                if (cp.Size != null)
                {
                    // Box checkpoint (goal-style): fires when the body grazes the box.
                    touched = VecUtil.PlayerTouchesObb(playerPos,
                        VecUtil.ToVector3(cp.Pos),
                        VecUtil.ToVector3(cp.Size, Vector3.one * 4f),
                        VecUtil.ToRotation(cp.Rot));
                }
                else
                {
                    float r = Mathf.Max(0.5f, cp.Radius);
                    Vector3 center = VecUtil.ToVector3(cp.Pos) + Vector3.up * 1f;
                    touched = (playerPos - center).sqrMagnitude <= r * r;
                }
                if (touched)
                {
                    ActiveCheckpoint = i;
                    break;
                }
            }
        }

        private void UpdateResetZones(Vector3 playerPos)
        {
            if (Time.unscaledTime < _resetCooldownUntil) return;
            Vector3? dest = null;   // respawn destination, computed on first touch only
            foreach (var zone in _resetZones)
            {
                if (zone?.Center == null) continue;
                Vector3 c = VecUtil.ToVector3(zone.Center);
                Vector3 s = VecUtil.ToVector3(zone.Size, Vector3.one * 4f);
                var r = VecUtil.ToRotation(zone.Rot);
                // Instant: fires the moment the player's BODY grazes the (possibly
                // rotated) box, not once the pivot is deep inside it.
                if (!VecUtil.PlayerTouchesObb(playerPos, c, s, r)) continue;

                // A zone that CONTAINS the respawn destination can only teleport-loop
                // forever (join a co-edit session and press Play while standing on an
                // invisible zone with no spawn set → endless resets). Ignore it.
                dest ??= ActiveCheckpoint >= 0 && ActiveCheckpoint < _checkpoints.Count
                         && _checkpoints[ActiveCheckpoint]?.Pos != null
                    ? CheckpointRespawnPos(_checkpoints[ActiveCheckpoint])
                    : _spawnPos;
                if (VecUtil.PlayerTouchesObb(dest.Value, c, s, r))
                {
                    if (!_warnedZoneLoop)
                    {
                        _warnedZoneLoop = true;
                        MapEditorPlugin.Logger.LogWarning(
                            "[PLAY] A reset trigger overlaps the respawn point — ignoring it to avoid a teleport loop.");
                    }
                    continue;
                }

                MapEditorPlugin.Logger.LogInfo(
                    $"[PLAY] Reset trigger fired at player={playerPos} zone(center={c}, size={s}) activeCp={ActiveCheckpoint}");
                RespawnAtCheckpoint();
                _resetCooldownUntil = Time.unscaledTime + 0.5f;
                return;
            }
        }
        private bool _warnedZoneLoop;

        // Back to the last checkpoint — or the spawn when none is active. The timer
        // keeps running: a reset is a mid-run punishment, not a restart.
        private void RespawnAtCheckpoint()
        {
            if (ActiveCheckpoint >= 0 && ActiveCheckpoint < _checkpoints.Count
                && _checkpoints[ActiveCheckpoint]?.Pos != null)
            {
                var cp = _checkpoints[ActiveCheckpoint];
                TeleportTo(CheckpointRespawnPos(cp), cp.Yaw);
            }
            else
            {
                TeleportTo(_spawnPos, _spawnYaw);
            }
        }

        // Both shapes respawn the player STANDING ON THE FLOOR under the checkpoint,
        // not at the raw stored point (a coin placed while flying would otherwise
        // respawn you mid-air; a box stores its center, mid-box). Scan down from the
        // marker's center; when no floor is in range, fall back to the stored point
        // (coin) or the box's bottom face.
        private Vector3 CheckpointRespawnPos(CheckpointData cp)
        {
            Vector3 center;
            float scanDepth;
            Vector3 fallback;
            if (cp.Size == null)
            {
                center = VecUtil.ToVector3(cp.Pos) + Vector3.up * 1f;   // the ring's center
                scanDepth = Mathf.Max(0.5f, cp.Radius) + 4f;
                fallback = VecUtil.ToVector3(cp.Pos);
            }
            else
            {
                center = VecUtil.ToVector3(cp.Pos);
                Vector3 size = VecUtil.ToVector3(cp.Size, Vector3.one * 4f);
                var rot = VecUtil.ToRotation(cp.Rot);
                // World-Y half-height of the oriented box (projection of each local axis).
                float halfY = 0.5f * (Mathf.Abs((rot * Vector3.right).y) * size.x
                                    + Mathf.Abs((rot * Vector3.up).y) * size.y
                                    + Mathf.Abs((rot * Vector3.forward).y) * size.z);
                scanDepth = halfY + 3f;
                fallback = center + Vector3.down * Mathf.Max(0f, halfY - 0.1f);
            }

            try
            {
                var playerRoot = _finder.FindPlayer()?.transform?.root;
                var hits = Physics.RaycastAll(new Ray(center, Vector3.down), scanDepth);
                float best = float.MaxValue;
                Vector3 floor = Vector3.zero;
                bool found = false;
                foreach (var h in hits)
                {
                    if (h.collider == null || h.collider.isTrigger) continue;
                    var t = h.collider.transform;
                    if (playerRoot != null && t.root == playerRoot) continue;
                    if (t.name.StartsWith("FIH_Line") || t.name.StartsWith("FIH_Gizmo")) continue;
                    if (h.distance < best) { best = h.distance; floor = h.point; found = true; }
                }
                if (found) return floor + Vector3.up * 0.05f;
            }
            catch { }
            return fallback;
        }

        // Reset zones themselves stay invisible in play mode — only the coin rings show.
        private void DrawCheckpointRings()
        {
            while (_checkpointRings.Count < _checkpoints.Count)
                _checkpointRings.Add(new LineBox($"FIH_Line_Checkpoint{_checkpointRings.Count}"));

            for (int i = 0; i < _checkpointRings.Count; i++)
            {
                if (i >= _checkpoints.Count || _checkpoints[i]?.Pos == null)
                {
                    _checkpointRings[i].Hide();
                    continue;
                }
                var cp = _checkpoints[i];
                var color = i == ActiveCheckpoint
                    ? new Color(0.35f, 1f, 0.45f, 0.95f)     // active: green
                    : new Color(1f, 0.62f, 0.15f, 0.95f);    // pending: orange
                if (cp.Size != null)
                    _checkpointRings[i].ShowBox(VecUtil.ToVector3(cp.Pos),
                        VecUtil.ToVector3(cp.Size, Vector3.one * 4f),
                        VecUtil.ToRotation(cp.Rot), color);
                else
                    _checkpointRings[i].ShowRing(VecUtil.ToVector3(cp.Pos) + Vector3.up * 1f,
                        Mathf.Max(0.5f, cp.Radius), color);
            }
        }

        private Bounds GoalBounds()
        {
            return new Bounds(VecUtil.ToVector3(_goal.Center), VecUtil.ToVector3(_goal.Size, Vector3.one * 3f));
        }

        private void SaveBestTime()
        {
            if (string.IsNullOrEmpty(_mapName)) return;
            try
            {
                // BestTime still holds the PRE-this-run value here — used below for the
                // "will overwrite X" message when this run doesn't beat it.
                bool isNewBest = BestTime == null || ElapsedSeconds < BestTime.Value;
                if (isNewBest)
                {
                    NewBest = true;
                    BestTime = ElapsedSeconds;
                    EditorConfig.Settings.BestTimes[_mapName] = ElapsedSeconds;
                    EditorConfig.Save();
                }

                // Every finished run offers to upload — not just new bests. The owner
                // (EditorController) may veto immediately via DismissUploadPrompt() if
                // uploads aren't configured/enabled for this session.
                if (!string.IsNullOrEmpty(_mapId))
                {
                    PendingUploadSeconds = ElapsedSeconds;
                    PendingUploadIsNewBest = isNewBest;
                    UploadPrompt = UploadPromptState.Offered;
                    try { OnRunFinished?.Invoke(); }
                    catch (Exception ex) { MapEditorPlugin.Logger.LogWarning($"[TIMES] OnRunFinished error: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[PLAY] Error saving best time: {ex.Message}");
            }
        }

        public void TeleportToSpawn() => TeleportTo(_spawnPos, _spawnYaw);

        private void TeleportTo(Vector3 position, float yaw)
        {
            _movedByUs = true;
            _lastPos = position;
            MapEditorPlugin.Logger.LogInfo($"[PLAY] Mod teleport -> {position} (timer={Timer}, activeCp={ActiveCheckpoint})");
            try
            {
                var playerT = _finder.FindPlayerTransform();
                var rb = _finder.GetCachedPlayerRigidbody();
                var rotation = Quaternion.Euler(0f, yaw, 0f);

                if (rb != null)
                {
                    rb.position = position;
                    rb.rotation = rotation;

                    // Brief kinematic toggle forces the physics engine to sync immediately.
                    bool wasKinematic = rb.isKinematic;
                    rb.isKinematic = true;
                    rb.isKinematic = wasKinematic;

                    if (!rb.isKinematic)
                    {
                        rb.linearVelocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                }
                if (playerT != null)
                {
                    playerT.position = position;
                    playerT.rotation = rotation;
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[PLAY] Teleport error: {ex}");
            }

            // Always auto-repair on any teleport/respawn — self-gated to a no-op when the
            // phone is intact. Independent try/catch so a repair failure never aborts play.
            try
            {
                var player = _finder.FindPlayer();
                if (player != null) PlayerRepair.Repair(player);
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[PLAY] Auto-repair error: {ex.Message}");
            }
        }

        public static string FormatTime(double seconds)
        {
            int minutes = (int)(seconds / 60);
            double rest = seconds - minutes * 60;
            return $"{minutes:00}:{rest:00.000}";
        }
    }
}
