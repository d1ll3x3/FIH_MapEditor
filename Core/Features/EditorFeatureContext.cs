using System;

namespace FIHMapEditor
{
    /// <summary>Stable, intentionally small service surface exposed to extensions.</summary>
    public sealed class EditorFeatureContext
    {
        public MapSession Session { get; }
        public GameObjectFinder Finder { get; }
        public PlacedObjectManager PlacedObjects { get; }
        public SelectionSystem Selection { get; }
        public PlayModeController PlayMode { get; }
        public Action<string, float> Notify { get; }
        public Action MarkDirty { get; }

        internal EditorFeatureContext(EditorController controller)
        {
            Session = controller.Session;
            Finder = controller.Finder;
            PlacedObjects = controller.PlacedManager;
            Selection = controller.SelectionSys;
            PlayMode = controller.PlayMode;
            Notify = controller.ShowToast;
            MarkDirty = controller.SetDirty;
        }
    }
}
