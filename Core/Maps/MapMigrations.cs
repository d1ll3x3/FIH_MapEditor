using System;
using System.Collections.Generic;

namespace FIHMapEditor
{
    /// <summary>
    /// Central compatibility gate for map files received from disk, online storage, or peers.
    /// Keep schema backfills here so every input path sees the same normalized model.
    /// </summary>
    public static class MapMigrations
    {
        /// <summary>
        /// Makes optional collections safe to enumerate and supplies stable identities needed
        /// by undo and multiplayer upsert/delete operations. Existing IDs are never replaced.
        /// </summary>
        public static MapFile Normalize(MapFile map)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            map.Objects ??= new List<MapObjectData>();
            map.LevelEdits ??= new List<LevelEditData>();
            map.Checkpoints ??= new List<CheckpointData>();
            map.ResetZones ??= new List<ResetZoneData>();
            map.SoccerGoals ??= new List<SoccerGoalData>();
            map.Name ??= "Untitled";
            if (string.IsNullOrEmpty(map.MapId)) map.MapId = Guid.NewGuid().ToString("N");
            // Pre-refactor maps identified entries by list position. Generate IDs once on
            // import so later saves and network diffs can address individual entries safely.
            foreach (var obj in map.Objects) obj.Uid ??= Guid.NewGuid().ToString("N");
            foreach (var item in map.Checkpoints) item.Uid ??= Guid.NewGuid().ToString("N");
            foreach (var item in map.ResetZones) item.Uid ??= Guid.NewGuid().ToString("N");
            foreach (var item in map.SoccerGoals) item.Uid ??= Guid.NewGuid().ToString("N");
            if (map.Ball != null) map.Ball.Uid ??= Guid.NewGuid().ToString("N");
            if (map.Scoreboard != null) map.Scoreboard.Uid ??= Guid.NewGuid().ToString("N");
            return map;
        }
    }
}
