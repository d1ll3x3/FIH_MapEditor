using System;
using System.Collections.Generic;
using UnityEngine;

namespace FIHMapEditor
{
    // The "infinite jump after changing maps" bug: a map swap (WipeAll) destroys the
    // previous map's ground while the player is standing on it. Unity never fires
    // OnCollisionExit for a destroyed collider, so EHS.GroundContact keeps that
    // contact in its contactCacheByCollider forever → IsTouchingGround stays true →
    // the game keeps re-arming the jump mid-air.
    //
    // The cleanup is a DEFERRED, SURGICAL purge (a couple of frames after the swap,
    // once Destroy() has actually taken effect): remove only cache entries whose
    // collider is dead, then let the game's own public APIs recompute. Never
    // Clear() the whole cache — the game's collision callbacks assume live contacts
    // stay cached, and wiping those crashes/corrupts it.
    public static class GroundContactFix
    {
        private static int _purgeAtFrame = -1;

        // Call at the end of a map swap. Destroy() is deferred to end of frame, so the
        // stale colliders only become null a frame later — purge on frame+2.
        public static void ScheduleAfterMapSwap()
        {
            _purgeAtFrame = Time.frameCount + 2;
        }

        // Called every frame from EditorController.Update.
        public static void Tick(GameObject localPlayer)
        {
            if (_purgeAtFrame < 0 || Time.frameCount < _purgeAtFrame) return;
            _purgeAtFrame = -1;
            if (localPlayer == null) return;
            var root = localPlayer.transform.root != null
                ? localPlayer.transform.root.gameObject : localPlayer;
            try
            {
                var gc = root.GetComponentInChildren<EHS.GroundContact>(true);
                if (gc == null)
                {
                    MapEditorPlugin.Logger.LogWarning("[JUMP] GroundContact not found; stale contacts NOT purged.");
                    return;
                }

                int stale = 0;
                var cache = gc.contactCacheByCollider;
                if (cache != null)
                {
                    var dead = new List<Collider>();
                    foreach (var kv in cache)
                        if (kv.Key == null) dead.Add(kv.Key);   // destroyed collider, entry stuck forever
                    foreach (var k in dead)
                        if (cache.Remove(k)) stale++;
                }

                if (stale > 0)
                {
                    gc.RecalculateIsTouching();
                    var jump = root.GetComponentInChildren<EHS.PlayerMovementJump>(true);
                    if (jump != null) jump.ResetJumpStateImmediate();
                    MapEditorPlugin.Logger.LogInfo(
                        $"[JUMP] Map swap: purged {stale} stale ground contact(s), touching recalculated, jump reset.");
                }
            }
            catch (Exception ex)
            {
                MapEditorPlugin.Logger.LogWarning($"[JUMP] Stale-contact purge failed: {ex.Message}");
            }
        }

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
