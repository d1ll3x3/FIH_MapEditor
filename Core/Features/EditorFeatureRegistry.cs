using System;
using System.Collections.Generic;

namespace FIHMapEditor
{
    /// <summary>
    /// Owns optional feature instances and fans editor lifecycle events out to them.
    /// Feature failures are isolated here so an extension cannot break the main editor loop.
    /// </summary>
    public sealed class EditorFeatureRegistry
    {
        private readonly Dictionary<string, IEditorFeature> _features = new Dictionary<string, IEditorFeature>();
        private EditorFeatureContext _context;

        public IEnumerable<IEditorFeature> All => _features.Values;

        public void Initialize(EditorFeatureContext context)
        {
            // Features may register before EditorController has finished constructing its
            // Unity services. Holding the context here lets both early and late registration
            // follow the same one-time initialization path.
            _context = context;
            foreach (var feature in _features.Values) InitializeOne(feature);
        }

        public void Register(IEditorFeature feature)
        {
            if (feature == null) throw new ArgumentNullException(nameof(feature));
            if (string.IsNullOrWhiteSpace(feature.Id)) throw new ArgumentException("Feature Id is required.", nameof(feature));
            if (_features.ContainsKey(feature.Id)) throw new InvalidOperationException($"Feature '{feature.Id}' is already registered.");
            _features.Add(feature.Id, feature);
            if (_context != null) InitializeOne(feature); // late registration
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
            // Extensions are a public integration surface. Never allow their exceptions to
            // escape into EditorController.Update, where they could disable the whole mod.
            try { action(); }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[FEATURE:{feature.Id}] {stage} failed: {ex}");
            }
        }
    }
}
