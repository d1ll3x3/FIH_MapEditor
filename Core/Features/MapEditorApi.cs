using System.Collections.Generic;

namespace FIHMapEditor
{
    /// <summary>Entry point other BepInEx plugins can use to extend the editor.</summary>
    public static class MapEditorApi
    {
        private static readonly List<IEditorFeature> Pending = new List<IEditorFeature>();
        private static EditorFeatureRegistry _registry;

        public static void RegisterFeature(IEditorFeature feature)
        {
            if (_registry == null) Pending.Add(feature);
            else _registry.Register(feature);
        }

        internal static void Attach(EditorFeatureRegistry registry)
        {
            _registry = registry;
            foreach (var feature in Pending) registry.Register(feature);
            Pending.Clear();
        }
    }
}
