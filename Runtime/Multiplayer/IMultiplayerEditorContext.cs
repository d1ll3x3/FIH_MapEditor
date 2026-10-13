using System;
using System.Collections.Generic;

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
        // Owner of the co-edited map; voice-line ops from anyone else are rejected.
        long OwnerSteamId { get; }
        BallData Ball { get; }
        PlayModeController PlayMode { get; }

        void RunOnMainThread(Action action);
        void ShowToast(string message, float seconds = 3f);
        bool HasMapContent();
        MapFile BuildMapFile();
        // Host-assigned FishNet ObjectId of each placed network object, by object Uid.
        // Not part of the map file: ids are only valid within the current session.
        Dictionary<string, int> NetworkObjectIds();

        // Transport callbacks may run off-thread; implementations marshal mutations through
        // RunOnMainThread before touching Unity objects or the working session.
        void ApplyRemoteObjectUpsert(MapObjectData data);
        void ApplyRemoteObjectDelete(string uid);
        void ApplyRemoteNetworkObjectId(string uid, int? objectId);
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
        void ApplyRemoteOwner(long ownerSteamId);
        void ApplyRemoteVoiceTriggerUpsert(VoiceTriggerData data);
        void ApplyRemoteVoiceTriggerDelete(string uid);
    }
}
