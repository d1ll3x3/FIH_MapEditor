using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx;

namespace FIHMapEditor
{
    public class MapFileInfo
    {
        public string FileName;      // file name without extension
        public string MapName;       // Name field inside the JSON
        public int ObjectCount;
        public DateTime LastWrite;
        public bool IsAutosave;
    }

    public static class MapSerializer
    {
        public const string EXTENSION = ".fihmap.json";
        public const string AUTOSAVE_NAME = "_autosave";
        public const string AUTOSAVE_PREV_NAME = "_autosave_prev";

        public static string MapsDir => Path.Combine(Paths.PluginPath, "FIHMapEditor", "Maps");

        // One shared instance: System.Text.Json caches its reflection metadata PER
        // options object, so recreating it per call would re-derive everything each time.
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unnamed";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            return name.Length == 0 ? "unnamed" : name;
        }

        public static string PathFor(string fileName) => Path.Combine(MapsDir, fileName + EXTENSION);

        // In-memory (de)serialization for the online map library (upload/download).
        public static string ToJson(MapFile map) => JsonSerializer.Serialize(map, Options);

        public static MapFile FromJson(string json)
        {
            var map = JsonSerializer.Deserialize<MapFile>(json, Options);
            if (map == null) throw new InvalidDataException("Map JSON deserialized to null");
            map.Objects ??= new List<MapObjectData>();
            map.LevelEdits ??= new List<LevelEditData>();
            map.Checkpoints ??= new List<CheckpointData>();
            map.ResetZones ??= new List<ResetZoneData>();
            // Legacy maps (pre-v6) carry no MapId — mint and keep one so the leaderboard
            // has a stable key from now on.
            if (string.IsNullOrEmpty(map.MapId))
                map.MapId = System.Guid.NewGuid().ToString("N");
            return map;
        }

        public static void Save(MapFile map, string fileName)
        {
            Directory.CreateDirectory(MapsDir);
            string json = JsonSerializer.Serialize(map, Options);
            File.WriteAllText(PathFor(fileName), json);
        }

        // Two-slot autosave: the previous autosave is kept as *_prev so a fresh session
        // with one stray edit can never wipe out yesterday's work in a single write.
        public static void SaveAutosave(MapFile map)
        {
            Directory.CreateDirectory(MapsDir);
            try
            {
                if (File.Exists(PathFor(AUTOSAVE_NAME)))
                    File.Copy(PathFor(AUTOSAVE_NAME), PathFor(AUTOSAVE_PREV_NAME), overwrite: true);
            }
            catch { }
            Save(map, AUTOSAVE_NAME);
        }

        public static MapFile Load(string fileName)
        {
            string json = File.ReadAllText(PathFor(fileName));
            var map = JsonSerializer.Deserialize<MapFile>(json, Options);
            if (map == null) throw new InvalidDataException("Map file deserialized to null");
            if (map.FormatVersion > MapFile.CURRENT_FORMAT_VERSION)
                MapEditorPlugin.Logger.LogWarning(
                    $"Map '{fileName}' has format v{map.FormatVersion} (newer than v{MapFile.CURRENT_FORMAT_VERSION}); loading anyway.");
            map.Objects ??= new List<MapObjectData>();
            map.LevelEdits ??= new List<LevelEditData>();
            map.Checkpoints ??= new List<CheckpointData>();
            map.ResetZones ??= new List<ResetZoneData>();
            return map;
        }

        public static void Delete(string fileName)
        {
            string path = PathFor(fileName);
            if (File.Exists(path)) File.Delete(path);
        }

        public static bool Exists(string fileName) => File.Exists(PathFor(fileName));

        public static List<MapFileInfo> ListMaps()
        {
            var result = new List<MapFileInfo>();
            try
            {
                if (!Directory.Exists(MapsDir)) return result;
                foreach (var file in Directory.GetFiles(MapsDir, "*" + EXTENSION))
                {
                    var info = new MapFileInfo
                    {
                        FileName = Path.GetFileName(file).Replace(EXTENSION, ""),
                        LastWrite = File.GetLastWriteTime(file),
                    };
                    info.IsAutosave = info.FileName.StartsWith(AUTOSAVE_NAME);
                    try
                    {
                        var map = JsonSerializer.Deserialize<MapFile>(File.ReadAllText(file), Options);
                        info.MapName = map?.Name ?? info.FileName;
                        info.ObjectCount = map?.Objects?.Count ?? 0;
                    }
                    catch
                    {
                        info.MapName = info.FileName + " (corrupt?)";
                        info.ObjectCount = -1;
                    }
                    result.Add(info);
                }
                // Newest first, autosave pinned on top
                result.Sort((a, b) =>
                {
                    if (a.IsAutosave != b.IsAutosave) return a.IsAutosave ? -1 : 1;
                    return b.LastWrite.CompareTo(a.LastWrite);
                });
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"Error listing maps: {ex.Message}");
            }
            return result;
        }
    }
}
