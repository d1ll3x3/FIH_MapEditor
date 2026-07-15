using System;

namespace FIHMapEditor
{
    /// <summary>Pure map mutations for built-in markers and soccer layout.</summary>
    public sealed class MarkerEditingService
    {
        private readonly MapSession _session;

        public MarkerEditingService(MapSession session)
            => _session = session ?? throw new ArgumentNullException(nameof(session));

        public void SetSpawn(float[] position, float yaw)
            => _session.Spawn = new SpawnPointData { Pos = position, Yaw = yaw };

        public void SetGoal(float[] center)
            // Moving/replacing a goal preserves its edited dimensions.
            => _session.Goal = new GoalZoneData { Center = center,
                Size = _session.Goal?.Size ?? new[] { 4f, 4f, 4f } };

        public CheckpointData AddCheckpoint(float[] position, float yaw, bool box)
        {
            // Coin checkpoints use Radius; box checkpoints use Size. A null Size is therefore
            // meaningful and must survive serialization.
            var item = new CheckpointData { Uid = Guid.NewGuid().ToString("N"), Pos = position,
                Yaw = yaw, Radius = 1.5f, Size = box ? new[] { 4f, 4f, 4f } : null };
            _session.Checkpoints.Add(item);
            return item;
        }

        public ResetZoneData AddResetZone(float[] center)
        {
            var item = new ResetZoneData { Uid = Guid.NewGuid().ToString("N"), Center = center,
                Size = new[] { 4f, 4f, 4f } };
            _session.ResetZones.Add(item);
            return item;
        }

        public void PlaceBall(float[] center)
            // There is one ball marker per map. Repositioning it keeps identity and radius so
            // multiplayer treats the operation as an update rather than delete-plus-create.
            => _session.Ball = new BallData { Uid = _session.Ball?.Uid ?? Guid.NewGuid().ToString("N"),
                Center = center, Radius = _session.Ball?.Radius ?? 0.5f };

        public SoccerGoalData AddSoccerGoal(float[] center, int team)
        {
            var item = new SoccerGoalData { Uid = Guid.NewGuid().ToString("N"), Center = center,
                Size = new[] { 4f, 4f, 4f }, Team = Math.Clamp(team, 0, 1) };
            _session.SoccerGoals.Add(item);
            return item;
        }

        public void PlaceScoreboard(float[] position, float[] rotation)
            // As with the ball, preserve stable identity and user-edited scale on reposition.
            => _session.Scoreboard = new ScoreboardData {
                Uid = _session.Scoreboard?.Uid ?? Guid.NewGuid().ToString("N"), Pos = position,
                Rot = rotation, Scale = _session.Scoreboard?.Scale ?? 2f };

        public void ToggleSoccerGoalTeam(int index)
        {
            if (index < 0 || index >= _session.SoccerGoals.Count) return;
            _session.SoccerGoals[index].Team = 1 - _session.SoccerGoals[index].Team;
        }

        public void ClearSpawn() => _session.Spawn = null;
        public void ClearGoal() => _session.Goal = null;
        public void RemoveBall() => _session.Ball = null;
    }
}
