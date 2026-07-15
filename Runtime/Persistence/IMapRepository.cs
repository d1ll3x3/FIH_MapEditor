using System.Collections.Generic;

namespace FIHMapEditor
{
    /// <summary>Storage boundary for local map files and recovery snapshots.</summary>
    public interface IMapRepository
    {
        IReadOnlyList<MapFileInfo> List();
        MapFile Load(string fileName);
        void Save(MapFile map, string fileName);
        void SaveAutosave(MapFile map);
        void Delete(string fileName);
        bool Exists(string fileName);
        string CreateAvailableName(string mapName, string currentFileName = null);
    }
}
