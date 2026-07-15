using System.Collections.Generic;

namespace FIHMapEditor
{
    /// <summary>Entry point other BepInEx plugins can use to extend the editor.</summary>
    public static class MapEditorApi
    {
        // BepInEx does not guarantee plugin construction order. Registrations made before
        // the map editor exists are queued, then transferred when EditorController attaches.
        private static readonly List<IEditorFeature> Pending = new List<IEditorFeature>();
        private static EditorFeatureRegistry _registry;

        public static void RegisterFeature(IEditorFeature feature)
        {
            if (_registry == null) Pending.Add(feature);
            else _registry.Register(feature);
        }

        internal static void Attach(EditorFeatureRegistry registry)
        {
            // Attach is internal because only the editor owns registry lifetime; third-party
            // plugins should interact solely through RegisterFeature.
            _registry = registry;
            foreach (var feature in Pending) registry.Register(feature);
            Pending.Clear();
        }
    }
}
