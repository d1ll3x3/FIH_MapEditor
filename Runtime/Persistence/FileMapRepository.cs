using System.Collections.Generic;

namespace FIHMapEditor
{
    /// <summary>
    /// Production repository adapter over the legacy static serializer. The adapter keeps
    /// filesystem decisions out of editor orchestration and permits an in-memory test double.
    /// </summary>
    public sealed class FileMapRepository : IMapRepository
    {
        public IReadOnlyList<MapFileInfo> List() => MapSerializer.ListMaps();
        public MapFile Load(string fileName) => MapSerializer.Load(fileName);
        public void Save(MapFile map, string fileName) => MapSerializer.Save(map, fileName);
        public void SaveAutosave(MapFile map) => MapSerializer.SaveAutosave(map);
        public void Delete(string fileName) => MapSerializer.Delete(fileName);
        public bool Exists(string fileName) => MapSerializer.Exists(fileName);

        public string CreateAvailableName(string mapName, string currentFileName = null)
        {
            string fileName = MapSerializer.SanitizeFileName(mapName);
            // Saving the currently-open slot is an overwrite. A different map with the same
            // display name receives a deterministic numeric suffix instead of being replaced.
            if (!Exists(fileName) || fileName == currentFileName) return fileName;

            int suffix = 2;
            while (Exists($"{fileName}_{suffix}")) suffix++;
            return $"{fileName}_{suffix}";
        }
    }
}
