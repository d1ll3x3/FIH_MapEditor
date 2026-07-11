using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using System;

namespace FIHMapEditor
{
    [BepInPlugin("com.flippingishard.mapeditor", "FIH Custom Map Editor", "0.10.0")]
    public class MapEditorPlugin : BasePlugin
    {
        internal static ManualLogSource Logger { get; private set; }

        public override void Load()
        {
            Logger = Log;
            Logger.LogInfo("FIH Custom Map Editor loaded! F6 opens the editor in the game level.");

            try
            {
                // Register our custom MonoBehaviour with IL2CPP before using AddComponent
                ClassInjector.RegisterTypeInIl2Cpp<MapEditorBehaviour>();

                var go = new GameObject("FIHMapEditor");
                GameObject.DontDestroyOnLoad(go);
                go.AddComponent<MapEditorBehaviour>();

                Logger.LogInfo("Map editor behaviour attached successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error loading map editor: {ex}");
            }
        }
    }

    /// <summary>
    /// Main MonoBehaviour for the map editor.
    /// Must have the IntPtr constructor for IL2CPP interop.
    /// </summary>
    public class MapEditorBehaviour : MonoBehaviour
    {
        // Required by IL2CPP interop
        public MapEditorBehaviour(IntPtr ptr) : base(ptr) { }

        private EditorController _controller;
        private bool _initialized = false;
        private float _timer = 0f;
        private float _nextAttempt = 2f;   // first attempt after 2s
        private const float RETRY_INTERVAL = 3f;

        void Awake()
        {
            MapEditorPlugin.Logger.LogInfo("MapEditorBehaviour awake, waiting for game scene...");
        }

        void Update()
        {
            if (_initialized)
            {
                _controller?.Update();
                return;
            }

            _timer += Time.deltaTime;
            if (_timer >= _nextAttempt)
            {
                _timer = 0f;
                _nextAttempt = RETRY_INTERVAL;
                TryInitialize();
            }
        }

        void OnGUI()
        {
            if (_initialized)
                _controller?.OnGUI();
        }

        private void TryInitialize()
        {
            try
            {
                _controller = new EditorController();
                _controller.Initialize();
                _initialized = true;
                MapEditorPlugin.Logger.LogInfo("Map editor initialized (dormant until the game level loads).");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"Error during initialization: {ex}");
            }
        }
    }
}
