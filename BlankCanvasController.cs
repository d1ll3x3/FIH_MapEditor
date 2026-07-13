using System;
using System.Collections.Generic;
using UnityEngine;

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
        // B2: original layers so Restore can put each collider back on the layer it
        // was on before we moved it to HiddenLayer.
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
                    // B2: move to HiddenLayer instead of c.enabled = false. Unity fires
                    // OnCollisionExit when the collider leaves the player's collision
                    // matrix, so the game's grounded tracker properly forgets the touch.
                    int original = HiddenLayer.MoveTo(c);
                    if (original < 0)
                    {
                        // HiddenLayer not initialised — fall back to the old path.
                        GroundRegistrar.Unregister(c);
                        c.enabled = false;
                        original = -1;
                    }
                    _disabledColliders.Add(c);
                    _disabledColliderLayers.Add(original);
                }

                IsActive = true;
                MapEditorPlugin.Logger.LogInfo(
                    $"[BLANK] Hidden {_disabledRenderers.Count} renderers / {_disabledColliders.Count} colliders → HiddenLayer.");
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
                for (int i = 0; i < _disabledColliders.Count; i++)
                {
                    var c = _disabledColliders[i];
                    if (c == null) continue;
                    int original = (i < _disabledColliderLayers.Count) ? _disabledColliderLayers[i] : -1;
                    if (original >= 0)
                        HiddenLayer.Restore(c, original);
                    else
                    {
                        // Legacy fallback.
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

        // Scene reload destroyed everything; the stored lists are stale.
        public void OnSceneChanged()
        {
            _disabledRenderers.Clear();
            _disabledColliders.Clear();
            _disabledColliderLayers.Clear();
            IsActive = false;
        }

    }
}
