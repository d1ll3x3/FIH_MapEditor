using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FIHMapEditor
{
    // "Blank canvas" base mode: hides the original level geometry without touching
    // anything gameplay-critical. Never SetActive(false) on unknown roots — only
    // Renderer and Collider components are disabled, and the exact lists are kept
    // so restoration is lossless.
    public class BlankCanvasController
    {
        private readonly GameObjectFinder _finder;

        private readonly List<Renderer> _disabledRenderers = new List<Renderer>();
        private readonly List<Collider> _disabledColliders = new List<Collider>();

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

                var playerRoot = _finder.FindPlayer()?.transform?.root;
                var scene = SceneManager.GetActiveScene();
                var roots = scene.GetRootGameObjects();

                foreach (var root in roots)
                {
                    if (root == null) continue;
                    if (!IsGeometryRoot(root, playerRoot)) continue;

                    foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                    {
                        if (r == null || !r.enabled) continue;
                        if (r.transform.name.StartsWith("FIH_Line")) continue;
                        r.enabled = false;
                        _disabledRenderers.Add(r);
                    }
                    foreach (var c in root.GetComponentsInChildren<Collider>(false))
                    {
                        if (c == null || !c.enabled) continue;
                        c.enabled = false;
                        _disabledColliders.Add(c);
                    }
                }

                IsActive = true;
                MapEditorPlugin.Logger.LogInfo(
                    $"[BLANK] Hidden {_disabledRenderers.Count} renderers / {_disabledColliders.Count} colliders.");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[BLANK] Error applying blank canvas: {ex}");
            }
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
                foreach (var c in _disabledColliders)
                {
                    if (c != null) { c.enabled = true; restored++; }
                }
                _disabledRenderers.Clear();
                _disabledColliders.Clear();
                IsActive = false;
                MapEditorPlugin.Logger.LogInfo($"[BLANK] Restored {restored} components.");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[BLANK] Error restoring: {ex}");
            }
        }

        // Scene reload destroyed everything; the stored lists are stale.
        public void OnSceneChanged()
        {
            _disabledRenderers.Clear();
            _disabledColliders.Clear();
            IsActive = false;
        }

        // A root is hideable geometry when it carries no gameplay-critical machinery.
        private bool IsGeometryRoot(GameObject root, Transform playerRoot)
        {
            if (playerRoot != null && root.transform == playerRoot) return false;
            if (root.name == "FIH_MapObjectsRoot" || root.name == "FIHMapEditor") return false;
            if (root.name.Contains(ObjectCatalog.CLONE_MARKER)) return false;

            if (root.GetComponentInChildren<Camera>(true) != null) return false;
            if (root.GetComponentInChildren<AudioListener>(true) != null) return false;
            if (root.GetComponentInChildren<Canvas>(true) != null) return false;

            try
            {
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    var type = mb.GetIl2CppType();
                    if (type == null) continue;
                    string name = type.Name;
                    string ns = type.Namespace ?? "";

                    if (name == "PlayerNetworked" || name == "NetworkManager" || name == "GameManager"
                        || name == "EventSystem")
                        return false;
                    if (ns.StartsWith("FishNet.Managing"))
                        return false;
                }
            }
            catch { }

            // Roots that contain lights but no renderers stay on (global lighting rigs).
            bool hasRenderer = root.GetComponentInChildren<Renderer>(true) != null;
            bool hasCollider = root.GetComponentInChildren<Collider>(true) != null;
            return hasRenderer || hasCollider;
        }
    }
}
