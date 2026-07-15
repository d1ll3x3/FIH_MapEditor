namespace FIHMapEditor
{
    /// <summary>Public lifecycle contract for optional editor features.</summary>
    public interface IEditorFeature
    {
        string Id { get; }
        void Initialize(EditorFeatureContext context);
        void Update();
        void OnModeChanged(EditorMode previous, EditorMode current);
        void OnMapApplied(MapFile map);
    }
}
