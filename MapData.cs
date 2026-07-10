using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    public enum MapBaseMode { Overlay, Blank }

    public enum TintColor { None, Red, Blue, Green, Yellow }

    // Game mechanic a placed clone re-implements (the original EHS.Interactables
    // components are stripped from clones; the mod simulates the behavior instead).
    public enum MechanicType { None, BoostPad, Cannon }

    public class SpawnPointData
    {
        public float[] Pos { get; set; }
        public float Yaw { get; set; }
    }

    public class GoalZoneData
    {
        public float[] Center { get; set; }
        public float[] Size { get; set; }
        public float[] Rot { get; set; }   // euler angles; null = axis-aligned (v5)
    }

    // Checkpoint: touching it makes it the active respawn point. Two shapes share this
    // record: the classic coin (sphere of Radius around Pos) and, when Size is set, a
    // goal-style oriented BOX (scalable/rotatable) centered on Pos — v8.
    public class CheckpointData
    {
        // Stable identity for multiplayer sync (per-item last-writer-wins); missing on
        // old files — backfilled on load.
        public string Uid { get; set; }
        public float[] Pos { get; set; }      // respawn position (box: also its center)
        public float Yaw { get; set; }        // respawn facing
        public float Radius { get; set; } = 1.5f;
        // Box variant (null = coin). Older mod versions ignore these and fall back to
        // treating it as a coin of Radius — degraded but functional.
        public float[] Size { get; set; }
        public float[] Rot { get; set; }      // euler angles; null = axis-aligned
    }

    // Reset trigger: entering it teleports the player back to the last checkpoint
    // (or the spawn when none is active). Only visible in the editor.
    public class ResetZoneData
    {
        public string Uid { get; set; }
        public float[] Center { get; set; }
        public float[] Size { get; set; }
        public float[] Rot { get; set; }   // euler angles; null = axis-aligned (v5)
    }

    // An edit applied to an ORIGINAL level object (not one of our clones): a transform
    // override and/or "deleted" (renderers+colliders disabled). Identified by the same
    // stable hierarchy path used for clone sources.
    public class LevelEditData
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public bool Hidden { get; set; }
        // Null when the edit is hide-only.
        public float[] Pos { get; set; }
        public float[] Rot { get; set; }   // euler angles
        public float[] Scale { get; set; }
    }

    public class MapObjectData
    {
        // Stable identity for multiplayer sync and for grouping. Missing on old files —
        // backfilled on load.
        public string Uid { get; set; }
        // Objects sharing a GroupId are selected/edited together; null = ungrouped.
        public string GroupId { get; set; }

        // Stable hierarchy path of the scene object this was cloned from (see ObjectCatalog).
        public string Source { get; set; }
        // Redundant leaf name, used as a load fallback and for human readability.
        public string SourceName { get; set; }
        public float[] Pos { get; set; }
        public float[] Rot { get; set; }   // euler angles
        public float[] Scale { get; set; }
        public TintColor Tint { get; set; } = TintColor.None;
        // Arbitrary RGB (0..1), set via the color picker; overrides Tint when present.
        public float[] CustomColor { get; set; }

        // Mechanics (v4). Null/None on plain scenery and on older files.
        public MechanicType Mechanic { get; set; } = MechanicType.None;
        public float? BoostForce { get; set; }
        public float? CannonTimer { get; set; }
        public float[] CannonTarget { get; set; }    // landing point (cannons + pads)
        public float[] CannonLaunchPos { get; set; } // where the cannon holds/launches from; null = auto
    }

    public class MapFile
    {
        // v2: added LevelEdits. v3: added Checkpoints + ResetZones. v4: mechanics
        // fields on objects. v5: goal/reset-zone rotation. v6: stable MapId (leaderboard
        // key). v7: stable Uid + GroupId on objects (grouping, multiplayer sync) and Uid
        // on checkpoints/reset zones; CustomColor. v8: box-shaped checkpoints
        // (CheckpointData.Size/Rot). Older files load fine — missing fields stay at
        // defaults and Uids are backfilled on load.
        public const int CURRENT_FORMAT_VERSION = 8;

        public int FormatVersion { get; set; } = CURRENT_FORMAT_VERSION;
        // Stable per-map identity for the leaderboard and the online map library. Minted
        // on creation, kept through save/load/sync; legacy maps get one minted on load.
        public string MapId { get; set; }
        public string Name { get; set; } = "Untitled";

        // Online sharing metadata. Editable=false means "play only" — the editor locks
        // when this map is loaded. Author is stamped on upload. All optional/back-compat.
        public bool Editable { get; set; } = true;
        public string AuthorName { get; set; }
        public long AuthorSteamId { get; set; }
        public MapBaseMode BaseMode { get; set; } = MapBaseMode.Overlay;
        public string GameScene { get; set; } = "";
        public SpawnPointData Spawn { get; set; }
        public GoalZoneData Goal { get; set; }
        public List<MapObjectData> Objects { get; set; } = new List<MapObjectData>();
        public List<LevelEditData> LevelEdits { get; set; } = new List<LevelEditData>();
        public List<CheckpointData> Checkpoints { get; set; } = new List<CheckpointData>();
        public List<ResetZoneData> ResetZones { get; set; } = new List<ResetZoneData>();
    }

    public static class VecUtil
    {
        public static float[] ToArray(Vector3 v) => new[] { v.x, v.y, v.z };

        public static Vector3 ToVector3(float[] a, Vector3 fallback = default)
        {
            if (a == null || a.Length < 3) return fallback;
            return new Vector3(a[0], a[1], a[2]);
        }

        public static Quaternion ToRotation(float[] euler)
            => euler == null || euler.Length < 3
                ? Quaternion.identity
                : Quaternion.Euler(euler[0], euler[1], euler[2]);

        // Point-in-oriented-box: transform into the box's local frame, compare against
        // the half extents (+ optional uniform margin).
        public static bool ObbContains(Vector3 point, Vector3 center, Vector3 size, Quaternion rot, float margin = 0f)
        {
            Vector3 local = Quaternion.Inverse(rot) * (point - center);
            Vector3 h = size * 0.5f;
            return Mathf.Abs(local.x) <= h.x + margin
                && Mathf.Abs(local.y) <= h.y + margin
                && Mathf.Abs(local.z) <= h.z + margin;
        }

        // The player's body vs an oriented box: a few samples along the capsule with a
        // horizontal pad, so a zone fires the moment the hitbox grazes it — not only
        // once the pivot is deep inside. Sample offsets cover both feet- and
        // center-pivot rigs.
        private static readonly float[] BodySamples = { -0.7f, 0f, 0.8f, 1.6f };

        public static bool PlayerTouchesObb(Vector3 playerPos, Vector3 center, Vector3 size, Quaternion rot)
        {
            foreach (float dy in BodySamples)
                if (ObbContains(playerPos + Vector3.up * dy, center, size, rot, 0.35f))
                    return true;
            return false;
        }
    }
}
