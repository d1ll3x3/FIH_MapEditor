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

        // Soccer mode
        private BallData _ball;
        private List<SoccerGoalData> _soccerGoals = new List<SoccerGoalData>();
        private readonly SoccerBall _soccerBall = new SoccerBall();
        private float _goalCooldownUntil = 0f;
        public int ScoreA { get; private set; }   // team 0
        public int ScoreB { get; private set; }   // team 1
        public bool HasSoccer => _ball != null && _soccerGoals.Count > 0;
        // Brief "GOAL!" flash for the HUD.
        public float GoalFlashUntil { get; private set; }

        // ── Soccer online sync (wired by EditorController → MultiplayerSync) ──
        // Authority = the lobby's lowest modded SteamID: it simulates the ball, counts
        // goals and broadcasts state; everyone else runs a kinematic follower ball and
        // sends kick requests. Defaults keep everything fully local in singleplayer.
        public Func<bool> BallAuthority = () => true;
        public Action<Vector3, Vector3, int, int, bool> BallSend;   // pos, vel, scoreA, scoreB, isKick
        private bool _ballWasAuthority = true;
        private float _nextBallSendAt;
        private Vector3 _remoteBallPos, _remoteBallVel;
        private float _remoteBallAt = -999f;

        // Manual kick: physics alone is unreliable (the player's collider layer may
        // ignore the ball's), so any player body overlapping the ball shoves it.
        private float _kickCooldownUntil;
        private readonly System.Collections.Generic.Dictionary<int, Vector3> _playerLastPos
            = new System.Collections.Generic.Dictionary<int, Vector3>();

        private readonly LineBox _goalBeacon = new LineBox("FIH_Line_GoalBeacon");
        private readonly List<LineBox> _checkpointRings = new List<LineBox>();

        public PlayModeController(GameObjectFinder finder)
        {
            _finder = finder;
        }

        public void Enter(SpawnPointData spawn, GoalZoneData goal, bool blankMode, string mapName,
                          string mapId, List<CheckpointData> checkpoints = null, List<ResetZoneData> resetZones = null,
                          BallData ball = null, List<SoccerGoalData> soccerGoals = null)
        {
            _goal = goal;   // blankMode no longer changes play behaviour; kept for signature stability
            _mapName = mapName ?? "";
            _mapId = mapId ?? "";
            _hasSpawn = spawn?.Pos != null;
            _checkpoints = checkpoints ?? new List<CheckpointData>();
            _resetZones = resetZones ?? new List<ResetZoneData>();
            _ball = ball;
            _soccerGoals = soccerGoals ?? new List<SoccerGoalData>();
            ActiveCheckpoint = -1;
            NewBest = false;
            SpawnSoccerBall();

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
            _soccerBall.Destroy();
        }

        // Soccer: (re)create the physics ball at its kickoff point and reset the score.
        private void SpawnSoccerBall()
        {
            _soccerBall.Destroy();
            ScoreA = 0;
            ScoreB = 0;
            _goalCooldownUntil = 0f;
            _ballWasAuthority = true;
            _remoteBallAt = -999f;
            _playerLastPos.Clear();
            if (_ball?.Center != null)
                _soccerBall.Spawn(VecUtil.ToVector3(_ball.Center), _ball.Radius, ChooseBallLayer());
        }

        // Pick a physics layer that provably collides with the player: the layer of
        // whatever the player is standing on. A fresh primitive's Default layer can be
        // ignored by the player's layer in the game's collision matrix, which made the
        // player walk straight through the ball.
        private int ChooseBallLayer()
        {
            try
            {
                var playerT = _finder.FindPlayerTransform();
                var playerCol = _finder.FindPlayer()?.GetComponentInChildren<Collider>(false);
                int playerLayer = playerCol != null ? playerCol.gameObject.layer : 0;

                // Default already collides with the player's layer? Keep Default.
                if (!Physics.GetIgnoreLayerCollision(0, playerLayer)) return 0;

                // Otherwise use the ground's layer (the player stands on it → collides).
                if (playerT != null)
                {
                    var hits = Physics.RaycastAll(new Ray(playerT.position + Vector3.up * 0.5f, Vector3.down), 30f);
                    float best = float.MaxValue;
                    int layer = 0;
                    foreach (var h in hits)
                    {
                        if (h.collider == null || h.collider.isTrigger) continue;
                        if (playerCol != null && h.collider.transform.root == playerCol.transform.root) continue;
                        if (h.distance < best) { best = h.distance; layer = h.collider.gameObject.layer; }
                    }
                    if (best < float.MaxValue)
                    {
                        MapEditorPlugin.Logger.LogInfo($"[BALL] Using ground layer {layer} (player layer {playerLayer} ignores Default).");
                        return layer;
                    }
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[BALL] Layer pick failed: {ex.Message}");
            }
            return 0;
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
            // A full restart also resets the match: score to 0 and ball back to centre.
            if (HasSoccer) SpawnSoccerBall();
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

            // Soccer runs independently of the run timer — it's a match, not a run.
            if (HasSoccer && _soccerBall.Alive && !paused)
                UpdateSoccer();
        }

        private void UpdateSoccer()
        {
            bool authority = BallAuthority?.Invoke() ?? true;
            if (authority != _ballWasAuthority)
            {
                // Role flip (a peer joined/left): follower balls freeze local physics.
                _soccerBall.SetKinematic(!authority);
                _ballWasAuthority = authority;
                MapEditorPlugin.Logger.LogInfo($"[BALL] Ball authority: {authority}");
            }

            if (authority) UpdateSoccerAuthority();
            else UpdateSoccerFollower();
        }

        private void UpdateSoccerAuthority()
        {
            Vector3 ballPos = _soccerBall.Position;

            // Any player body overlapping the ball shoves it — collision layers between
            // the player rig and our primitive are unreliable, so the kick is manual.
            TryKick(sendRemote: false);

            // Periodic state broadcast for online followers.
            if (BallSend != null && Time.unscaledTime >= _nextBallSendAt)
            {
                _nextBallSendAt = Time.unscaledTime + 0.12f;
                BallSend(ballPos, _soccerBall.Velocity, ScoreA, ScoreB, false);
            }

            // Fall out of bounds → back to centre (no score).
            if (_ball?.Center != null && ballPos.y < VecUtil.ToVector3(_ball.Center).y - 60f)
            {
                _soccerBall.ResetToCenter();
                return;
            }

            if (Time.unscaledTime < _goalCooldownUntil) return;

            foreach (var g in _soccerGoals)
            {
                if (g?.Center == null) continue;
                if (!VecUtil.ObbContains(ballPos, VecUtil.ToVector3(g.Center),
                        VecUtil.ToVector3(g.Size, Vector3.one * 4f), VecUtil.ToRotation(g.Rot),
                        _ball?.Radius ?? 0.5f))
                    continue;

                // Ball entered team g.Team's goal → the OTHER team scores.
                if (g.Team == 0) ScoreB++;
                else ScoreA++;

                GoalFlashUntil = Time.unscaledTime + 2f;
                _goalCooldownUntil = Time.unscaledTime + 1f;   // one goal per entry
                _soccerBall.ResetToCenter();
                MapEditorPlugin.Logger.LogInfo($"[BALL] GOAL! Score A {ScoreA} - {ScoreB} B");
                // Tell followers immediately so the goal doesn't lag a broadcast tick.
                BallSend?.Invoke(_soccerBall.Position, Vector3.zero, ScoreA, ScoreB, false);
                return;
            }
        }

        private void UpdateSoccerFollower()
        {
            // Move our kinematic ball toward the authority's state, extrapolating with
            // its last velocity so it doesn't stutter between packets.
            if (_remoteBallAt > 0)
            {
                float age = Mathf.Min(Time.unscaledTime - _remoteBallAt, 0.5f);
                Vector3 target = _remoteBallPos + _remoteBallVel * age;
                _soccerBall.MoveTo(Vector3.Lerp(_soccerBall.Position, target, Time.deltaTime * 12f));
            }

            // Local kicks are requests: the authority applies them and echoes the result.
            TryKick(sendRemote: true);
        }

        // Detect a player body overlapping the ball and shove it. On the authority this
        // applies directly (for every player in the scene); on followers it only checks
        // the LOCAL player and sends the kick to the authority.
        private void TryKick(bool sendRemote)
        {
            if (Time.unscaledTime < _kickCooldownUntil) return;
            float ballRadius = Mathf.Clamp(_ball?.Radius ?? 0.5f, 0.1f, 5f);
            Vector3 ballPos = _soccerBall.Position;

            GameObject[] players;
            if (sendRemote)
            {
                var local = _finder.FindPlayer();
                if (local == null) return;
                players = new[] { local };
            }
            else
            {
                try { players = GameObject.FindGameObjectsWithTag("Player"); }
                catch { players = null; }
                if (players == null || players.Length == 0)
                {
                    var local = _finder.FindPlayer();
                    if (local == null) return;
                    players = new[] { local };
                }
            }

            foreach (var p in players)
            {
                if (p == null) continue;
                Vector3 pos = p.transform.position;

                // Approximate the body as sample points up the capsule (feet or center
                // pivot both covered), same trick as VecUtil.PlayerTouchesObb.
                bool touching = false;
                foreach (float dy in new[] { -0.7f, 0f, 0.8f, 1.6f })
                {
                    if ((ballPos - (pos + Vector3.up * dy)).sqrMagnitude
                        <= (ballRadius + 0.55f) * (ballRadius + 0.55f))
                    {
                        touching = true;
                        break;
                    }
                }
                if (!touching) continue;

                // Kick direction: from the player through the ball, mostly horizontal.
                // Speed follows how fast the player is moving (estimated from position
                // deltas so it also works for remote player bodies).
                int id = p.GetInstanceID();
                Vector3 playerVel = Vector3.zero;
                if (_playerLastPos.TryGetValue(id, out var last) && Time.deltaTime > 0.0001f)
                    playerVel = (pos - last) / Time.deltaTime;

                Vector3 dir = ballPos - pos;
                dir.y = 0f;
                dir = dir.sqrMagnitude > 0.001f ? dir.normalized : p.transform.forward;

                float speed = Mathf.Clamp(new Vector3(playerVel.x, 0, playerVel.z).magnitude * 1.25f, 5f, 30f);
                Vector3 kick = dir * speed + Vector3.up * (speed * 0.28f);

                _kickCooldownUntil = Time.unscaledTime + 0.18f;
                if (sendRemote)
                    BallSend?.Invoke(ballPos, kick, ScoreA, ScoreB, true);
                else
                    _soccerBall.Kick(kick);
                break;
            }

            // Track player positions for the velocity estimate above.
            foreach (var p in players)
            {
                if (p == null) continue;
                _playerLastPos[p.GetInstanceID()] = p.transform.position;
            }
        }

        // A ball message arrived over P2P (routed via EditorController on main thread).
        public void ApplyRemoteBall(Vector3 pos, Vector3 vel, int a, int b, bool isKick)
        {
            if (!HasSoccer || !_soccerBall.Alive) return;

            bool authority = BallAuthority?.Invoke() ?? true;
            if (isKick)
            {
                // A follower asked to kick: the authority applies it if plausible.
                if (authority && (pos - _soccerBall.Position).sqrMagnitude < 16f)
                    _soccerBall.Kick(vel);
                return;
            }

            if (authority) return;   // stale state from an old authority — ignore

            _remoteBallPos = pos;
            _remoteBallVel = vel;
            _remoteBallAt = Time.unscaledTime;
            if (a != ScoreA || b != ScoreB)
            {
                ScoreA = a;
                ScoreB = b;
                GoalFlashUntil = Time.unscaledTime + 2f;
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
