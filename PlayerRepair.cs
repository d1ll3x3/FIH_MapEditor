using System;
using UnityEngine;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace FIHMapEditor
{
    // Repairs the local player's broken phone — gameplay state, not just visuals. Ported
    // from the FlippingIsHard trainer's PhoneRepair. The phone destruction is driven by
    // EHS.DestructionStateMachine (StateNormal / StateDestroyed); while broken the OWNER
    // runs StateDestroyed locally, so the networked reassemble RPC alone does NOT repair
    // the owner — we force the FSM back to StateNormal, whose Enter() re-arms movement.
    //
    // Always on: called on every teleport/respawn in play mode, gated so it only forces a
    // transition when the phone is actually broken (so intact teleports don't reset moves).
    // Strong-typed game access (EHS.StateNormal / EHS.DestroyedPhoneEffectHandler, both in
    // the referenced Assembly-CSharp.dll) is isolated in try/catch methods; if those types
    // are missing (e.g. a future playtest build), the feature disables itself instead of
    // crashing.
    public static class PlayerRepair
    {
        // Cached per local player. Unity's overloaded == makes a destroyed component
        // compare as null, so these self-invalidate on scene change / respawn.
        private static EHS.DestroyedPhoneEffectHandler _handler;
        private static MonoBehaviour _playerNetworked;
        private static MonoBehaviour _playerRef;
        private static bool _unavailable;

        public static void Reset()
        {
            _handler = null;
            _playerNetworked = null;
            _playerRef = null;
            _unavailable = false;
        }

        private static void Resolve(GameObject localPlayer)
        {
            if (localPlayer == null) return;
            var root = localPlayer.transform.root != null
                ? localPlayer.transform.root.gameObject
                : localPlayer;

            if (_handler == null)
                _handler = root.GetComponentInChildren<EHS.DestroyedPhoneEffectHandler>(true);

            if (_playerNetworked == null || _playerRef == null)
            {
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    var t = mb.GetIl2CppType();
                    if (t == null) continue;
                    if (_playerNetworked == null && t.Name == "PlayerNetworked") _playerNetworked = mb;
                    if (_playerRef == null && t.Name == "PlayerRef") _playerRef = mb;
                }
            }
        }

        public static void Repair(GameObject localPlayer)
        {
            if (_unavailable) return;
            try
            {
                Resolve(localPlayer);

                // 1) The real repair: force the owner's destruction FSM back to Normal.
                ForceStateNormal();

                // 2) Local visual reassemble (in case StateNormal.Enter doesn't repaint).
                if (_handler != null)
                {
                    try { _handler.SetToNormal(false); }
                    catch (Exception ex) { MapEditorPlugin.Logger.LogWarning($"[Repair] SetToNormal failed: {ex.Message}"); }
                }

                // 3) Networked reassemble so other clients see the repair too.
                SendReassembleRpc();
            }
            catch (TypeLoadException ex)
            {
                _unavailable = true;
                MapEditorPlugin.Logger.LogWarning($"[Repair] Phone-repair types missing; auto-repair disabled: {ex.Message}");
            }
            catch (MissingMethodException ex)
            {
                _unavailable = true;
                MapEditorPlugin.Logger.LogWarning($"[Repair] Phone-repair API missing; auto-repair disabled: {ex.Message}");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogError($"[Repair] Repair error: {ex}");
            }
        }

        private static void ForceStateNormal()
        {
            if (_playerRef == null) return;
            try
            {
                var fsmObj = _playerRef.GetIl2CppType().GetProperty("DestructionStateMachine")
                    ?.GetGetMethod()?.Invoke(_playerRef, null);
                if (fsmObj == null) return;

                var fsmType = fsmObj.GetIl2CppType();

                string stateName = null;
                try
                {
                    var n = fsmType.GetProperty("CurrentStateName")?.GetGetMethod()?.Invoke(fsmObj, null);
                    if (n != null) stateName = n.ToString();
                }
                catch { }

                // Only force a transition when actually destroyed — re-entering Normal on
                // every teleport would needlessly reset movement/abilities.
                if (stateName != null && stateName.IndexOf("Destroy", StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                var setState = fsmType.GetMethod("SetState");
                if (setState == null) return;

                var normal = new EHS.StateNormal();
                var args = new Il2CppReferenceArray<Il2CppSystem.Object>(1);
                args[0] = normal;
                setState.Invoke(fsmObj, args);
                MapEditorPlugin.Logger.LogInfo("[Repair] Phone repaired (FSM forced to StateNormal).");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[Repair] ForceStateNormal error: {ex.Message}");
            }
        }

        private static void SendReassembleRpc()
        {
            if (_playerNetworked == null) return;
            try
            {
                var method = _playerNetworked.GetIl2CppType().GetMethod("RequestPhoneReassembleServerRpc");
                if (method == null) return;
                var args = new Il2CppReferenceArray<Il2CppSystem.Object>(1);
                args[0] = null; // NetworkConnection — server fills it
                method.Invoke(_playerNetworked, args);
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[Repair] reassemble RPC failed: {ex.Message}");
            }
        }
    }
}
