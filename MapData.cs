using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    public enum MapBaseMode { Overlay, Blank }

    public enum TintColor { None, Red, Blue, Green, Yellow }

    public class SpawnPointData
    {
        public float[] Pos { get; set; }
        public float Yaw { get; set; }
    }

    public class GoalZoneData
    {
        public float[] Center { get; set; }
        public float[] Size { get; set; }
    }

    public class MapObjectData
    {
        // Stable hierarchy path of the scene object this was cloned from (see ObjectCatalog).
        public string Source { get; set; }
        // Redundant leaf name, used as a load fallback and for human readability.
        public string SourceName { get; set; }
        public float[] Pos { get; set; }
        public float[] Rot { get; set; }   // euler angles
        public float[] Scale { get; set; }
        public TintColor Tint { get; set; } = TintColor.None;
    }

    public class MapFile
    {
        public const int CURRENT_FORMAT_VERSION = 1;

        public int FormatVersion { get; set; } = CURRENT_FORMAT_VERSION;
        public string Name { get; set; } = "Untitled";
        public MapBaseMode BaseMode { get; set; } = MapBaseMode.Overlay;
        public string GameScene { get; set; } = "";
        public SpawnPointData Spawn { get; set; }
        public GoalZoneData Goal { get; set; }
        public List<MapObjectData> Objects { get; set; } = new List<MapObjectData>();
    }

    public static class VecUtil
    {
        public static float[] ToArray(Vector3 v) => new[] { v.x, v.y, v.z };

        public static Vector3 ToVector3(float[] a, Vector3 fallback = default)
        {
            if (a == null || a.Length < 3) return fallback;
            return new Vector3(a[0], a[1], a[2]);
        }
    }
}
