using System;
using System.Collections.Generic;

namespace FIHMapEditor
{
    public static class MapMigrations
    {
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
