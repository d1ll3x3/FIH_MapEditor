using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    // Reserves a user physics layer that NOTHING collides with, so the mod can move
    // colliders into it as a "hidden but present" state (vs. c.enabled = false, which
    // makes Unity never fire OnCollisionExit → player stuck "grounded" on air).
    //
    // The layer is picked once at first init: highest empty-named user layer (8-31).
    // A layer with an empty name is almost certainly unused by the game. If the
    // chosen layer turns out to be in use, the user will see ghost collisions /
    // walk-throughs — easy to spot and pick another by renaming the chosen one in
    // the layer matrix.
    //
    // We also call Physics.IgnoreLayerCollision(self, every other layer, true) once
    // so even a shared layer can't break the game's behaviour.
    public static class HiddenLayer
    {
        public static int Layer { get; private set; } = -1;
        private static bool _initialized;
        // Wipe-clean changes the live scene source itself. Unity then copies that layer
        // when the catalog source is instantiated, so remember the source collider's real
        // layer and translate it back onto editor-owned clones.
        private static readonly Dictionary<int, int> _originalLayers = new Dictionary<int, int>();

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            // Prefer the highest user layer with an empty name. Loop top-down so we
            // don't collide with common "first user layer = player" conventions.
            for (int i = 31; i >= 8; i--)
            {
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(i))) continue;
                Layer = i;
                break;
            }

            if (Layer < 0)
            {
                // All 8-31 named → fall back to layer 31 (most likely to be "Misc")
                // and warn. Better than nothing.
                Layer = 31;
                MapEditorPlugin.Logger.LogWarning(
                    $"[HIDDEN] No empty user layer found; falling back to layer 31 ('{LayerMask.LayerToName(31)}'). " +
                    "If hide/unhide breaks gameplay, rename that layer or pick another.");
            }
            else
            {
                MapEditorPlugin.Logger.LogInfo($"[HIDDEN] Reserved layer {Layer} ('{LayerMask.LayerToName(Layer)}') as the no-collision sink.");
            }

            // Ignore against ALL layers. The player and several gameplay bodies use
            // Unity's built-in layers 0-7; omitting those left "hidden" level colliders
            // physically active. Ignore self too, so hidden colliders cannot interact.
            for (int other = 0; other < 32; other++)
                Physics.IgnoreLayerCollision(Layer, other, true);
        }

        // Move a collider to the reserved layer (no-op if not initialised or null).
        // Returns the original layer so callers can restore it.
        public static int MoveTo(Collider c)
        {
            if (c == null || Layer < 0) return -1;
            int original = c.gameObject.layer;
            _originalLayers[c.GetInstanceID()] = original;
            c.gameObject.layer = Layer;
            return original;
        }

        public static void Restore(Collider c, int originalLayer)
        {
            if (c == null || originalLayer < 0) return;
            c.gameObject.layer = originalLayer;
            _originalLayers.Remove(c.GetInstanceID());
        }

        public static int RestoreInheritedColliderLayers(GameObject source, GameObject clone)
        {
            if (source == null || clone == null || Layer < 0) return 0;
            var sourceColliders = source.GetComponentsInChildren<Collider>(true);
            var cloneColliders = clone.GetComponentsInChildren<Collider>(true);
            int restored = 0;
            int count = Mathf.Min(sourceColliders.Length, cloneColliders.Length);
            for (int i = 0; i < count; i++)
            {
                var sourceCollider = sourceColliders[i];
                var cloneCollider = cloneColliders[i];
                if (sourceCollider == null || cloneCollider == null
                    || cloneCollider.gameObject.layer != Layer) continue;
                if (!_originalLayers.TryGetValue(sourceCollider.GetInstanceID(), out int original)) continue;
                cloneCollider.gameObject.layer = original;
                restored++;
            }
            return restored;
        }

        public static void ClearTrackedOriginals()
        {
            _originalLayers.Clear();
        }
    }
}
