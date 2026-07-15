using System.Collections.Generic;

namespace FIHMapEditor
{
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
            if (!Exists(fileName) || fileName == currentFileName) return fileName;

            int suffix = 2;
            while (Exists($"{fileName}_{suffix}")) suffix++;
            return $"{fileName}_{suffix}";
        }
    }
}
