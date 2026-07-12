using System;
using UnityEngine;

namespace FIHMapEditor
{
    // The game's surface system (slippery ground, footstep surfaces, landing behavior)
    // keys off a GroundRegistry: every walkable collider maps to a Ground component
    // describing its surface. The level registers its own colliders at load time; OUR
    // clones start unregistered, so a placed GroundBlock loses its slipperiness (and
    // the game spams "Missing ground registration for collider ..." errors when the
    // player steps on it). Instantiate copies the Ground component onto the clone —
    // this registers the clone's colliders against it, restoring the original surface
    // behavior. When the clone carries no Ground of its own (the source kept it on a
    // parent outside the cloned root), the SOURCE's Ground is used as the descriptor.
    //
    // Everything is reflection (type names may move across game builds); any failure
    // degrades to today's behavior: default surface + cosmetic errors.
    public static class GroundRegistrar
    {
        private static Il2CppSystem.Object _registry;
        private static Il2CppSystem.Reflection.MethodInfo _register;
        private static bool _unavailable;

        // Scene reload kills the cached registry instance.
        public static void Reset()
        {
            _registry = null;
            _register = null;
            _unavailable = false;
        }

        public static void RegisterClone(GameObject clone, GameObject source)
        {
            if (_unavailable || clone == null) return;
            try
            {
                var cols = clone.GetComponentsInChildren<Collider>(true);
                if (cols.Length == 0) return;

                // Fallback surface descriptor: the source's own Ground (may sit on a
                // parent chunk above the cloned root).
                MonoBehaviour sourceGround = source != null
                    ? FindGroundUpward(source.transform, null)
                    : null;

                int registered = 0;
                foreach (var col in cols)
                {
                    if (col == null) continue;
                    var ground = FindGroundUpward(col.transform, clone.transform) ?? sourceGround;
                    if (ground == null) continue;
                    if (!EnsureRegistry()) return;
                    var args = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(2);
                    args[0] = col;
                    args[1] = ground;
                    _register.Invoke(_registry, args);
                    registered++;
                }
                if (registered > 0)
                    MapEditorPlugin.Logger.LogInfo(
                        $"[GROUND] '{clone.name}': {registered} collider(s) registered with their Ground surface.");
            }
            catch (Exception ex)
            {
                // A stale cached registry (scene reload) throws here — drop the cache so
                // the next spawn re-resolves; never take the whole feature down for one
                // failed clone.
                _registry = null;
                _register = null;
                MapEditorPlugin.Logger.LogWarning($"[GROUND] Register failed for '{clone.name}': {ex.Message}");
            }
        }

        // Nearest Ground component on t or its parents; stops after stopAt (inclusive).
        // stopAt == null climbs to the scene root (used for the source-side fallback).
        private static MonoBehaviour FindGroundUpward(Transform t, Transform stopAt)
        {
            for (; t != null; t = t.parent)
            {
                foreach (var mb in t.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    string n = null;
                    try { n = mb.GetIl2CppType()?.Name; } catch { }
                    if (n == "Ground") return mb;
                }
                if (stopAt != null && t == stopAt) break;
            }
            return null;
        }

        private static bool EnsureRegistry()
        {
            if (_register != null) return true;
            try
            {
                // The registry hangs off a GroundRegistry property on some manager —
                // match by capability, not type name (same pattern as lobby discovery).
                foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    Il2CppSystem.Reflection.MethodInfo getter = null;
                    try { getter = mb.GetIl2CppType()?.GetProperty("GroundRegistry")?.GetGetMethod(); }
                    catch { }
                    if (getter == null) continue;
                    var reg = getter.Invoke(mb, null);
                    if (reg == null) continue;
                    var m = reg.GetIl2CppType().GetMethod("Register");
                    if (m == null) continue;
                    _registry = reg;
                    _register = m;
                    MapEditorPlugin.Logger.LogInfo("[GROUND] GroundRegistry found — placed objects keep their surface behavior.");
                    return true;
                }
                _unavailable = true;
                MapEditorPlugin.Logger.LogWarning("[GROUND] GroundRegistry not found — placed ground keeps the default surface.");
                return false;
            }
            catch (Exception ex)
            {
                _unavailable = true;
                MapEditorPlugin.Logger.LogWarning($"[GROUND] Registry lookup failed: {ex.Message}");
                return false;
            }
        }
    }
}
