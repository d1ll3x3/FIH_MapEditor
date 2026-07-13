using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    // The base level is sprinkled with EHS.RespawnZones.RespawnOnTouch volumes —
    // invisible triggers with OnTriggerEnter/OnCollisionEnter that start the game's
    // pipe-respawn sequence on contact. On custom maps the MOD owns respawning
    // (spawn / checkpoints / reset zones / R); if the player lands in or falls through
    // one of these zones (easy after a cross-map teleport with a huge Y delta — the
    // trainer never hits them because it only teleports to places you already stood),
    // the game starts a pipe respawn that the custom map then interrupts, leaving
    // GameManager.IsBeingRespawned stuck true → jump permanently blocked.
    //
    // Fix: while a custom map is applied, disable every touch-respawn zone at the
    // component level so its trigger/collision callbacks never run. A scene reload
    // recreates them enabled, so vanilla play is untouched.
    public static class RespawnZoneGuard
    {
        public static void DisableAll()
        {
            try
            {
                int n = 0;
                foreach (var z in UnityEngine.Object.FindObjectsOfType<EHS.RespawnZones.RespawnOnTouch>(true))
                {
                    if (z == null || !z.enabled) continue;
                    z.enabled = false;
                    n++;
                }
                if (n > 0)
                    MapEditorPlugin.Logger.LogInfo(
                        $"[RESPAWN] Disabled {n} touch-respawn zone(s) — the custom map owns respawning.");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[RESPAWN] Zone disable error: {ex.Message}");
            }
        }

        // Transition-only watch of the game's respawn state — at most 2 log lines per
        // respawn, so it can stay on. If the jump ever locks up again, the log shows
        // whether a game respawn started and whether it ever finished.
        private static bool _lastRespawning;
        private static float _respawnSince;

        public static void Watch()
        {
            try
            {
                bool r = EHS.GameManager.IsBeingRespawned;
                if (r == _lastRespawning) return;
                _lastRespawning = r;
                if (r)
                {
                    _respawnSince = Time.unscaledTime;
                    MapEditorPlugin.Logger.LogInfo("[RESPAWN] Game respawn STARTED.");
                }
                else
                {
                    MapEditorPlugin.Logger.LogInfo(
                        $"[RESPAWN] Game respawn finished ({Time.unscaledTime - _respawnSince:0.0}s).");
                }
            }
            catch { }
        }

        public static void OnSceneChanged() => _lastRespawning = false;
    }
}
