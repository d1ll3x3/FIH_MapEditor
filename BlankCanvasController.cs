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

                // Component-level sweep instead of per-root gating: the old approach
                // skipped ENTIRE roots that contained a camera or canvas anywhere, which
                // left chunks of the level visible. Now every renderer/collider in the
                // scene goes dark unless it belongs to the player, the mod, or UI.
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (r == null || !r.enabled) continue;
                    if (!IsWipeable(r.transform, playerRoot)) continue;
                    r.enabled = false;
                    _disabledRenderers.Add(r);
                }
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>())
                {
                    if (c == null || !c.enabled) continue;
                    if (!IsWipeable(c.transform, playerRoot)) continue;
                    c.enabled = false;
                    _disabledColliders.Add(c);
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

        // Everything is wipeable except: the player rig, anything the mod created
        // (clones under FIH_MapObjectsRoot, FIH_Line/FIH_Gizmo helpers) and UI.
        private static bool IsWipeable(Transform t, Transform playerRoot)
        {
            var root = t.root;
            if (playerRoot != null && root == playerRoot) return false;
            if (root.name.StartsWith("FIH")) return false;
            if (t.name.StartsWith("FIH_")) return false;
            if (t.GetComponentInParent<Canvas>() != null) return false;
            return true;
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

    }
}
