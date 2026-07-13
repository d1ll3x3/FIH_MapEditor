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
        private static Il2CppSystem.Reflection.MethodInfo _unregister;
        private static bool _unavailable;
        private static bool _unregisterBroken;

        // Diagnostic counters — see in the log whether Unregister is actually being
        // called and succeeding. If _diagUnregisterCalls is 0 after a wipe, something
        // is bypassing the hide/unhide path. If _diagUnregisterErrors > 0, the
        // reflection is hitting a real exception (and we've given up).
        public static int _diagUnregisterCalls;
        public static int _diagUnregisterSuccess;
        public static int _diagUnregisterErrors;

        // Scene reload kills the cached registry instance.
        public static void Reset()
        {
            _registry = null;
            _register = null;
            _unregister = null;
            _unavailable = false;
            _unregisterBroken = false;
            _fallbackGround = null;
        }

        // Default surface descriptor for clones that carry no Ground of their own.
        // The game HARD-REQUIRES every collider the player steps on to be registered:
        // an unregistered one makes GroundContact.OnCollisionEnter error out mid-
        // processing ("Missing ground registration..."), corrupting the grounded
        // state — one of the faces of the jump bug. Any Ground in the scene works as
        // a generic descriptor; wrong footstep sound at worst.
        private static MonoBehaviour _fallbackGround;

        private static MonoBehaviour FallbackGround()
        {
            if (_fallbackGround != null) return _fallbackGround;
            try
            {
                foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    string n = null;
                    try { n = mb.GetIl2CppType()?.Name; } catch { }
                    if (n == "Ground") { _fallbackGround = mb; break; }
                }
            }
            catch { }
            return _fallbackGround;
        }

        // Take a collider OUT of the game's ground system. CRITICAL before disabling a
        // ground collider: Unity never fires OnCollisionExit/OnTriggerExit when a
        // collider is merely disabled, so the game's grounded tracking stays stuck
        // "touching" it forever (endless jump-reset / slippery loop). The game exposes
        // GroundRegistry.Unregister(Collider) precisely for removing ground — call it so
        // the stuck touch is cleared the proper way. No-op if the collider was never
        // registered.
        public static void Unregister(Collider col)
        {
            if (_unavailable || _unregisterBroken || col == null) return;
            _diagUnregisterCalls++;
            try
            {
                if (!EnsureRegistry() || _unregister == null) { _unregisterBroken = _unregister == null; return; }
                var args = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(1);
                args[0] = col;
                _unregister.Invoke(_registry, args);
                _diagUnregisterSuccess++;
            }
            catch (Exception ex)
            {
                _diagUnregisterErrors++;
                MapEditorPlugin.Logger.LogWarning(
                    $"[DIAG] Unregister failed (call #{_diagUnregisterCalls}, errors #{_diagUnregisterErrors}): {ex.GetType().Name}: {ex.Message}");
                // Called for every wiped collider (mostly not ground) — give up quietly
                // after the first failure instead of spamming per collider.
                _unregisterBroken = true;
                MapEditorPlugin.Logger.LogWarning($"[GROUND] Unregister unavailable ({ex.Message}) — stuck-jump cleanup falls back to ResetJumpState.");
            }
        }

        // Re-register a level collider with its own Ground surface (restore path after a
        // hide/blank-canvas is undone). Finds the Ground on the collider or its parents.
        public static void RegisterLevelCollider(Collider col)
        {
            if (_unavailable || col == null) return;
            try
            {
                var ground = FindGroundUpward(col.transform, null);
                if (ground == null) return;
                if (!EnsureRegistry()) return;
                var args = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(2);
                args[0] = col;
                args[1] = ground;
                _register.Invoke(_registry, args);
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[GROUND] Re-register failed: {ex.Message}");
            }
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

                // Hot-path cheap-out: no Ground anywhere in the clone's tree or source →
                // skip the per-collider reflection climbs and register everything with
                // the scene's generic Ground. NEVER leave a walkable collider
                // unregistered — the game errors out on contact and the grounded state
                // corrupts (jump bug).
                bool useFallbackOnly = sourceGround == null && FindGroundInTree(clone.transform) == null;
                if (useFallbackOnly) sourceGround = FallbackGround();

                int registered = 0;
                foreach (var col in cols)
                {
                    if (col == null || col.isTrigger) continue;
                    var ground = useFallbackOnly
                        ? sourceGround
                        : (FindGroundUpward(col.transform, clone.transform) ?? sourceGround ?? FallbackGround());
                    if (ground == null) continue;
                    if (!EnsureRegistry()) return;
                    var args = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(2);
                    args[0] = col;
                    args[1] = ground;
                    _register.Invoke(_registry, args);
                    registered++;
                }
                if (registered > 0 && EditorConfig.VerboseLogs)
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

        // Cheap pre-check: any Ground component anywhere inside the cloned root. Does
        // not climb past the root. Used to short-circuit mass-apply when the clone
        // carries no surface at all (most props do) and skip the per-collider reflection.
        private static MonoBehaviour FindGroundInTree(Transform root)
        {
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                string n = null;
                try { n = mb.GetIl2CppType()?.Name; } catch { }
                if (n == "Ground") return mb;
            }
            return null;
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
                    try { _unregister = reg.GetIl2CppType().GetMethod("Unregister"); } catch { }
                    // Diagnostic: print the registry class + which methods we found.
                    // If Unregister is missing, the fix for "stuck grounded after a hide"
                    // is partial — we can clear the layer-matrix entry but not the
                    // touching state. The ResetJumpState fallback should still work, but
                    // we want to KNOW.
                    string regClass = reg.GetIl2CppType()?.FullName ?? "?";
                    string unreg = _unregister != null ? "found" : "NOT FOUND (no Unregister method on the registry)";
                    MapEditorPlugin.Logger.LogInfo(
                        $"[DIAG] GroundRegistry resolved: class='{regClass}' " +
                        $"Register=found Unregister={unreg} (host MonoBehaviour='{mb.GetIl2CppType()?.FullName}')");
                    MapEditorPlugin.Logger.LogInfo("[GROUND] GroundRegistry found — placed objects keep their surface behavior.");
                    return true;
                }
                _unavailable = true;
                MapEditorPlugin.Logger.LogWarning("[DIAG] GroundRegistry NOT found on any MonoBehaviour — stuck-jump fix path is dead.");
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
