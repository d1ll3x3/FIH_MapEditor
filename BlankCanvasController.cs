using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FIHMapEditor
{
    // Blank base mode hides only the vanilla level-content roots. Environment,
    // weather, lighting, cameras, networking and manager roots remain untouched.
    public class BlankCanvasController
    {
        private readonly GameObjectFinder _finder;
        private readonly List<Renderer> _disabledRenderers = new List<Renderer>();
        private readonly List<Collider> _disabledColliders = new List<Collider>();
        private readonly List<int> _disabledColliderLayers = new List<int>();

        public bool IsActive { get; private set; }

        public BlankCanvasController(GameObjectFinder finder)
        {
            _finder = finder;
        }

        public void Apply()
        {
            if (IsActive) return;
            try
            {
                _disabledRenderers.Clear();
                _disabledColliders.Clear();
                _disabledColliderLayers.Clear();

                var playerRoot = _finder.FindPlayer()?.transform?.root;
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (r == null || !r.enabled || IsProtected(r.transform, playerRoot)) continue;
                    r.enabled = false;
                    _disabledRenderers.Add(r);
                }
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>())
                {
                    if (c == null || !c.enabled || IsProtected(c.transform, playerRoot)) continue;
                    // A layer transition produces collision exits, unlike destroying
                    // or disabling a collider that the player is currently touching.
                    int original = HiddenLayer.MoveTo(c);
                    if (original < 0)
                    {
                        GroundRegistrar.Unregister(c);
                        c.enabled = false;
                        original = -1;
                    }
                    _disabledColliders.Add(c);
                    _disabledColliderLayers.Add(original);
                }

                IsActive = true;
                GroundContactFix.ScheduleAfterMapSwap();
                MapEditorPlugin.Logger.LogInfo(
                    $"[BLANK] Hidden {_disabledRenderers.Count} renderers / {_disabledColliders.Count} colliders " +
                    "from vanilla content; environment/managers preserved.");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[BLANK] Error applying blank canvas: {ex}");
            }
        }

        private static bool IsProtected(Transform t, Transform playerRoot)
        {
            if (t == null) return false;
            var root = t.root;
            if (playerRoot != null && root == playerRoot) return true;
            if (t.GetComponentInParent<Canvas>() != null) return true;
            if (t.GetComponentInParent<Camera>() != null) return true;

            string rootName = root.name;
            if (rootName.StartsWith("FIH", StringComparison.OrdinalIgnoreCase)) return true;
            // FishNet network objects must remain scene roots, so editor-spawned pads
            // and cannons cannot live under FIH_MapObjectsRoot. They retain [FIH] in
            // their name as the ownership marker instead.
            if (rootName.IndexOf(ObjectCatalog.CLONE_MARKER, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (rootName.StartsWith("NW_EnvironmentVolumes_Area", StringComparison.OrdinalIgnoreCase)) return true;
            if (rootName.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (rootName.IndexOf("Bootstrap", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (rootName.IndexOf("Gateway", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            for (var current = t; current != null; current = current.parent)
            {
                if (current.name.StartsWith("FIH_", StringComparison.OrdinalIgnoreCase)) return true;
                if (current.name.IndexOf(ObjectCatalog.CLONE_MARKER, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        public void Restore()
        {
            if (!IsActive) return;
            try
            {
                int restored = 0;
                foreach (var r in _disabledRenderers)
                {
                    if (r != null) { r.enabled = true; restored++; }
                }
                for (int i = 0; i < _disabledColliders.Count; i++)
                {
                    var c = _disabledColliders[i];
                    if (c == null) continue;
                    int original = i < _disabledColliderLayers.Count ? _disabledColliderLayers[i] : -1;
                    if (original >= 0) HiddenLayer.Restore(c, original);
                    else
                    {
                        c.enabled = true;
                        GroundRegistrar.RegisterLevelCollider(c);
                    }
                    restored++;
                }
                _disabledRenderers.Clear();
                _disabledColliders.Clear();
                _disabledColliderLayers.Clear();
                IsActive = false;
                MapEditorPlugin.Logger.LogInfo($"[BLANK] Restored {restored} components.");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[BLANK] Error restoring: {ex}");
            }
        }

        public void OnSceneChanged()
        {
            _disabledRenderers.Clear();
            _disabledColliders.Clear();
            _disabledColliderLayers.Clear();
            IsActive = false;
        }
    }
}
