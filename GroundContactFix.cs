using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    // The "infinite jump" bug: destroying a collider the player is standing on (a map
    // swap, deleting the object, undo, a co-edit delete, a network object respawned or
    // despawned by the host) never fires OnCollisionExit, so EHS.GroundContact keeps
    // that contact in its contactCacheByCollider forever → IsTouchingGround stays true
    // → the game keeps re-arming the jump mid-air.
    //
    // The cleanup is a SURGICAL purge: remove only cache entries whose collider is
    // dead, then let the game's own public APIs recompute. It runs a couple of frames
    // after a map swap and otherwise checks the cache a few times a second, since many
    // of those deletions happen outside the mod (FishNet despawns). Never Clear() the
    // whole cache — the game's collision callbacks assume live contacts stay cached,
    // and wiping those crashes/corrupts it.
    public static class GroundContactFix
    {
        private static int _purgeAtFrame = -1;
        private static float _nextCheckAt;
        private const float CHECK_INTERVAL = 0.25f;
        private static GameObject _cachedRoot;
        private static EHS.GroundContact _cachedContact;

        // Call at the end of a map swap. Destroy() is deferred to end of frame, so the
        // stale colliders only become null a frame later — purge on frame+2.
        public static void ScheduleAfterMapSwap()
        {
            _purgeAtFrame = Time.frameCount + 2;
        }

        // Called every frame from EditorController.Update.
        public static void Tick(GameObject localPlayer)
        {
            bool scheduled = _purgeAtFrame >= 0 && Time.frameCount >= _purgeAtFrame;
            if (!scheduled && Time.unscaledTime < _nextCheckAt) return;
            _nextCheckAt = Time.unscaledTime + CHECK_INTERVAL;
            if (scheduled) _purgeAtFrame = -1;
            if (localPlayer == null) return;
            var root = localPlayer.transform.root != null
                ? localPlayer.transform.root.gameObject : localPlayer;
            try
            {
                if (_cachedRoot != root || _cachedContact == null)
                {
                    _cachedRoot = root;
                    _cachedContact = root.GetComponentInChildren<EHS.GroundContact>(true);
                }
                var gc = _cachedContact;
                if (gc == null)
                {
                    if (scheduled)
                        MapEditorPlugin.Logger.LogWarning("[JUMP] GroundContact not found; stale contacts NOT purged.");
                    return;
                }

                int stale = 0;
                var cache = gc.contactCacheByCollider;
                if (cache != null)
                {
                    var dead = new List<Collider>();
                    foreach (var kv in cache)
                    {
                        // Blank mode keeps vanilla colliders alive for lossless restore,
                        // but moves them to the no-collision sink. Their old contact can
                        // remain cached just like a destroyed collider and block jumping.
                        bool hidden = kv.Key != null && HiddenLayer.Layer >= 0
                            && kv.Key.gameObject.layer == HiddenLayer.Layer;
                        if (kv.Key == null || hidden) dead.Add(kv.Key);
                    }
                    foreach (var k in dead)
                        if (cache.Remove(k)) stale++;
                }

                if (stale > 0)
                {
                    gc.RecalculateIsTouching();
                    var jump = root.GetComponentInChildren<EHS.PlayerMovementJump>(true);
                    if (jump != null) jump.ResetJumpStateImmediate();
                    MapEditorPlugin.Logger.LogInfo(
                        $"[JUMP] Purged {stale} stale/hidden ground contact(s), touching recalculated, jump reset.");
                }
            }
            catch (Exception ex)
            {
                // The periodic check would repeat a persistent failure four times a second.
                if (scheduled || !_failureLogged)
                    MapEditorPlugin.Logger.LogWarning($"[JUMP] Stale-contact purge failed: {ex.Message}");
                _failureLogged = true;
            }
        }

        private static bool _failureLogged;

        // F9 diagnostic: one compact dump of every flag involved in the jump/ground
        // state, so a live repro tells us exactly which one is stuck.
        public static void Dump(GameObject localPlayer)
        {
            try
            {
                if (localPlayer == null)
                {
                    MapEditorPlugin.Logger.LogInfo("[JUMPDUMP] No player found.");
                    return;
                }
                var root = localPlayer.transform.root != null
                    ? localPlayer.transform.root.gameObject : localPlayer;

                var gc = root.GetComponentInChildren<EHS.GroundContact>(true);
                var jump = root.GetComponentInChildren<EHS.PlayerMovementJump>(true);
                var fd = root.GetComponentInChildren<EHS.PlayerFallData>(true);

                string gcInfo = "GroundContact: <missing>";
                if (gc != null)
                {
                    int cacheCount = -1;
                    int staleKeys = 0;
                    try
                    {
                        var cache = gc.contactCacheByCollider;
                        if (cache != null)
                        {
                            cacheCount = cache.Count;
                            foreach (var kv in cache)
                                if (kv.Key == null) staleKeys++;   // destroyed collider still cached
                        }
                    }
                    catch { }
                    gcInfo = $"GroundContact: touchingGround={gc.IsTouchingGround} touchingWall={gc.IsTouchingWall} "
                           + $"cache={cacheCount} (stale={staleKeys}) lastGroundTime={gc.lastGroundTime:0.##}";
                }

                string jumpInfo = jump != null
                    ? $"Jump: canJump={jump.canJump} blockExternal={jump.BlockJumpExternal} lastJumpTime={jump.lastJumpTime:0.##}"
                    : "Jump: <missing>";

                string fallInfo = fd != null
                    ? $"Fall: isGrounded={fd.isGrounded} wasGrounded={fd.wasGrounded} fallStartPoint={fd.fallStartPoint:0.##}"
                    : "Fall: <missing>";

                bool respawning = false;
                try { respawning = EHS.GameManager.IsBeingRespawned; } catch { }

                MapEditorPlugin.Logger.LogInfo(
                    $"[JUMPDUMP] t={Time.time:0.##} pos={root.transform.position} respawning={respawning}\n"
                    + $"  {gcInfo}\n  {jumpInfo}\n  {fallInfo}");
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[JUMPDUMP] error: {ex.Message}");
            }
        }
    }
}
