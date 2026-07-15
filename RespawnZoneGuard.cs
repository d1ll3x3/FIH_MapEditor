using System;
using UnityEngine;

namespace FIHMapEditor
{
    // Touch-respawn zones created or restored during custom-map loading miss the
    // game's original scene-initialization pass. Rebind all of them to the current
    // respawn services whenever a map is applied or play mode begins.
    public static class RespawnZoneGuard
    {
        public static Action OnRespawnFinished;
        // Name retained because older feature extensions may call it. Its corrected
        // behavior is initialization, not suppression.
        public static void DisableAll()
        {
            try
            {
                var post = UnityEngine.Object.FindObjectOfType<EHS.Bootstraps.PostBootstrapGame>();
                var refs = post?.GameRefs;
                var pipes = refs?.RespawnPipesZones;
                if (post == null || refs == null || pipes == null)
                {
                    MapEditorPlugin.Logger.LogWarning(
                        "[RESPAWN] Cannot bind touch zones: live respawn services unavailable.");
                    return;
                }

                int count = 0;
                foreach (var zone in UnityEngine.Object.FindObjectsOfType<EHS.RespawnZones.RespawnOnTouch>(true))
                {
                    if (zone == null) continue;
                    zone.postBootstrapGame = post;
                    zone.respawnZones = pipes;
                    zone.enabled = true;
                    count++;
                }
                MapEditorPlugin.Logger.LogInfo(
                    $"[RESPAWN] Bound {count} touch-respawn zone(s) to live respawn services.");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[RESPAWN] Zone binding error: {ex.Message}");
            }
        }

        private static bool _lastRespawning;
        private static float _respawnSince;

        public static void Watch()
        {
            try
            {
                bool respawning = EHS.GameManager.IsBeingRespawned;
                if (respawning == _lastRespawning) return;
                _lastRespawning = respawning;
                if (respawning)
                {
                    _respawnSince = Time.unscaledTime;
                    MapEditorPlugin.Logger.LogInfo("[RESPAWN] Game respawn STARTED.");
                }
                else
                {
                    MapEditorPlugin.Logger.LogInfo(
                        $"[RESPAWN] Game respawn finished ({Time.unscaledTime - _respawnSince:0.0}s).");
                    try { OnRespawnFinished?.Invoke(); }
                    catch (Exception ex)
                    {
                        MapEditorPlugin.Logger.LogWarning($"[RESPAWN] Custom destination handoff failed: {ex.Message}");
                    }
                }
            }
            catch { }
        }

        public static void OnSceneChanged() => _lastRespawning = false;
    }
}
