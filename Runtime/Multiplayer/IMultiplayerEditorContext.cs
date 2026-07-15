using System;

namespace FIHMapEditor
{
    /// <summary>
    /// Narrow host API used by Steam synchronization. Keeping transport code behind
    /// this boundary prevents it from reaching arbitrary editor and GUI internals.
    /// </summary>
    public interface IMultiplayerEditorContext
    {
        EditorMode Mode { get; }
        string MapId { get; }
        BallData Ball { get; }
        PlayModeController PlayMode { get; }

        void RunOnMainThread(Action action);
        void ShowToast(string message, float seconds = 3f);
        bool HasMapContent();
        MapFile BuildMapFile();

        // Transport callbacks may run off-thread; implementations marshal mutations through
        // RunOnMainThread before touching Unity objects or the working session.
        void ApplyRemoteObjectUpsert(MapObjectData data);
        void ApplyRemoteObjectDelete(string uid);
        void ApplyRemoteCheckpointUpsert(CheckpointData data);
        void ApplyRemoteCheckpointDelete(string uid);
        void ApplyRemoteResetZoneUpsert(ResetZoneData data);
        void ApplyRemoteResetZoneDelete(string uid);
        void ApplyRemoteLevelEditUpsert(LevelEditData data);
        void ApplyRemoteLevelEditRevert(string path);
        void ApplyRemoteSpawn(SpawnPointData data);
        void ApplyRemoteGoal(GoalZoneData data);
        void ApplyRemoteBaseMode(MapBaseMode mode);
        void ApplyRemoteMapName(string name);
        void ApplyRemoteMapId(string mapId);
        void ApplyRemoteEditable(bool editable);
        void ApplyRemoteBallMarker(BallData data);
        void ApplyRemoteSoccerGoalUpsert(SoccerGoalData data);
        void ApplyRemoteSoccerGoalDelete(string uid);
        void ApplyRemoteScoreboard(ScoreboardData data);
    }
}
