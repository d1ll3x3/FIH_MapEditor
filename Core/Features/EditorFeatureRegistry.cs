using System;
using System.Collections.Generic;

namespace FIHMapEditor
{
    public sealed class EditorFeatureRegistry
    {
        private readonly Dictionary<string, IEditorFeature> _features = new Dictionary<string, IEditorFeature>();
        private EditorFeatureContext _context;

        public IEnumerable<IEditorFeature> All => _features.Values;

        public void Initialize(EditorFeatureContext context)
        {
            _context = context;
            foreach (var feature in _features.Values) InitializeOne(feature);
        }

        public void Register(IEditorFeature feature)
        {
            if (feature == null) throw new ArgumentNullException(nameof(feature));
            if (string.IsNullOrWhiteSpace(feature.Id)) throw new ArgumentException("Feature Id is required.", nameof(feature));
            if (_features.ContainsKey(feature.Id)) throw new InvalidOperationException($"Feature '{feature.Id}' is already registered.");
            _features.Add(feature.Id, feature);
            if (_context != null) InitializeOne(feature);
        }

        public bool Unregister(string id) => id != null && _features.Remove(id);

        public void Update()
        {
            foreach (var feature in _features.Values)
                Safe(feature, feature.Update, "update");
        }

        public void ModeChanged(EditorMode previous, EditorMode current)
        {
            foreach (var feature in _features.Values)
                Safe(feature, () => feature.OnModeChanged(previous, current), "mode change");
        }

        public void MapApplied(MapFile map)
        {
            foreach (var feature in _features.Values)
                Safe(feature, () => feature.OnMapApplied(map), "map apply");
        }

        private void InitializeOne(IEditorFeature feature)
            => Safe(feature, () => feature.Initialize(_context), "initialization");

        private static void Safe(IEditorFeature feature, Action action, string stage)
        {
            try { action(); }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[FEATURE:{feature.Id}] {stage} failed: {ex}");
            }
        }
    }
}
