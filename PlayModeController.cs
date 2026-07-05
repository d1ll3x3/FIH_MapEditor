using System;
using UnityEngine;

namespace FIHMapEditor
{
    public enum TimerState { Idle, Armed, Running, Finished }

    // Play-mode runtime: teleport to the map spawn, run timer, goal detection by
    // bounds polling (no trigger callbacks — unreliable under IL2CPP), fall respawn.
    public class PlayModeController
    {
        private readonly GameObjectFinder _finder;

        public TimerState Timer { get; private set; } = TimerState.Idle;
        public double ElapsedSeconds { get; private set; }
        public bool GoalReachedThisRun { get; private set; }
        public double? BestTime { get; private set; }
        public bool NewBest { get; private set; }

        private double _startTime;
        private Vector3 _spawnPos;
        private float _spawnYaw;
        private bool _hasSpawn;
        private GoalZoneData _goal;
        private bool _blankMode;
        private string _mapName = "";

        private readonly LineBox _goalBeacon = new LineBox("FIH_Line_GoalBeacon");

        public PlayModeController(GameObjectFinder finder)
        {
            _finder = finder;
        }

        public void Enter(SpawnPointData spawn, GoalZoneData goal, bool blankMode, string mapName)
        {
            _goal = goal;
            _blankMode = blankMode;
            _mapName = mapName ?? "";
            _hasSpawn = spawn?.Pos != null;
            GoalReachedThisRun = false;
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
        }

        public void Exit()
        {
            Timer = TimerState.Idle;
            _goalBeacon.Hide();
        }

        public void RestartRun()
        {
            TeleportToSpawn();
            Timer = TimerState.Armed;
            ElapsedSeconds = 0;
            GoalReachedThisRun = false;
            NewBest = false;
        }

        public void Update()
        {
            if (Timer == TimerState.Idle) return;

            var playerT = _finder.FindPlayerTransform();
            if (playerT == null) return;
            Vector3 pos = playerT.position;

            // Show a subtle beacon at the goal so the player can find it.
            if (_goal?.Center != null && Timer != TimerState.Finished)
            {
                var bounds = GoalBounds();
                _goalBeacon.ShowBeacon(bounds, new Color(0.3f, 1f, 0.5f, 0.8f), Mathf.Max(bounds.size.y, 6f));
            }

            switch (Timer)
            {
                case TimerState.Armed:
                    // Start on the first real displacement (robust vs. physics settling).
                    if (Vector3.Distance(pos, _spawnPos) > 0.05f)
                    {
                        Timer = TimerState.Running;
                        _startTime = Time.unscaledTimeAsDouble;
                    }
                    break;

                case TimerState.Running:
                    ElapsedSeconds = Time.unscaledTimeAsDouble - _startTime;

                    if (_goal?.Center != null && GoalBounds().Contains(pos))
                    {
                        Timer = TimerState.Finished;
                        GoalReachedThisRun = true;
                        _goalBeacon.Hide();
                        SaveBestTime();
                    }
                    break;
            }

            // Fall respawn (blank canvas has no ground outside the map)
            if (_blankMode && Timer != TimerState.Finished && pos.y < _spawnPos.y - 100f)
                RestartRun();
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
                if (BestTime == null || ElapsedSeconds < BestTime.Value)
                {
                    NewBest = true;
                    BestTime = ElapsedSeconds;
                    EditorConfig.Settings.BestTimes[_mapName] = ElapsedSeconds;
                    EditorConfig.Save();
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[PLAY] Error saving best time: {ex.Message}");
            }
        }

        public void TeleportToSpawn()
        {
            try
            {
                var playerT = _finder.FindPlayerTransform();
                var rb = _finder.GetCachedPlayerRigidbody();
                var rotation = Quaternion.Euler(0f, _spawnYaw, 0f);

                if (rb != null)
                {
                    rb.position = _spawnPos;
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
                    playerT.position = _spawnPos;
                    playerT.rotation = rotation;
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[PLAY] Teleport error: {ex}");
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
