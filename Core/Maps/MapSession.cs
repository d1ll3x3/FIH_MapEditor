using System.Collections.Generic;

namespace FIHMapEditor
{
    /// <summary>Unity-independent state for the map currently being edited.</summary>
    public sealed class MapSession
    {
        // This is working state, not a serialized DTO. EditorController exposes compatibility
        // forwarding properties while callers are gradually migrated to this owner.
        public string Name { get; set; } = "Untitled";
        public string MapId { get; set; }
        public bool Editable { get; set; } = true;
        public string AuthorName { get; set; }
        public long AuthorSteamId { get; set; }
        public string CurrentFileName { get; set; }
        public MapBaseMode BaseMode { get; set; } = MapBaseMode.Overlay;
        public SpawnPointData Spawn { get; set; }
        public GoalZoneData Goal { get; set; }
        public List<CheckpointData> Checkpoints { get; set; } = new List<CheckpointData>();
        public List<ResetZoneData> ResetZones { get; set; } = new List<ResetZoneData>();
        public BallData Ball { get; set; }
        public List<SoccerGoalData> SoccerGoals { get; set; } = new List<SoccerGoalData>();
        public ScoreboardData Scoreboard { get; set; }
        public bool Dirty { get; set; }
        public string LoadReport { get; set; } = "";

        public void Reset(string mapId)
        {
            // Allocate fresh lists rather than clearing old ones. Play mode and sync code may
            // still hold a snapshot/list reference while a whole-map transition completes.
            Name = "Untitled";
            MapId = mapId;
            Editable = true;
            AuthorName = null;
            AuthorSteamId = 0;
            CurrentFileName = null;
            BaseMode = MapBaseMode.Overlay;
            Spawn = null;
            Goal = null;
            Checkpoints = new List<CheckpointData>();
            ResetZones = new List<ResetZoneData>();
            Ball = null;
            SoccerGoals = new List<SoccerGoalData>();
            Scoreboard = null;
            Dirty = false;
            LoadReport = "";
        }

        public void ApplyMetadata(MapFile map)
        {
            // The caller must normalize the map first. This method deliberately adopts the
            // supplied marker lists: it represents replacing the entire working session, not
            // merging a partial multiplayer update.
            Name = map.Name ?? "Untitled";
            MapId = map.MapId;
            Editable = map.Editable;
            AuthorName = map.AuthorName;
            AuthorSteamId = map.AuthorSteamId;
            BaseMode = map.BaseMode;
            Spawn = map.Spawn;
            Goal = map.Goal;
            Checkpoints = map.Checkpoints ?? new List<CheckpointData>();
            ResetZones = map.ResetZones ?? new List<ResetZoneData>();
            Ball = map.Ball;
            SoccerGoals = map.SoccerGoals ?? new List<SoccerGoalData>();
            Scoreboard = map.Scoreboard;
        }
    }
}
