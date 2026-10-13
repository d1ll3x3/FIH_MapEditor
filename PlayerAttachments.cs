using EHS;
using UnityEngine;

namespace FIHMapEditor
{
    // Ziplines, swing bars, swing hooks and suck pipes hold the player while attached:
    // they block movement and jumping, switch collisions off and drive the body. The
    // game lets go of all of them before it teleports the player (the sequence below
    // mirrors PlayerTeleportController.TeleportTo). The editor teleports and freezes
    // things on every mode change, so it must let go the same way first, or the player
    // stays locked in the attached state once the mechanic stops driving it.
    public static class PlayerAttachments
    {
        public static void ReleaseAll(GameObject player)
        {
            if (player == null) return;
            var playerRef = player.GetComponentInParent<PlayerRef>() ?? player.GetComponentInChildren<PlayerRef>();
            var context = playerRef?.PhoneContext;
            if (context == null) return;

            if (context.IsSuckPipeAttached && context.activeSuckPipe != null) context.activeSuckPipe.ReleaseForTeleport();
            if (context.IsHooked && context.activeHook != null) context.activeHook.Detach();
            if (context.IsZiplineAttached && context.activeZipline != null) context.activeZipline.Detach();
            if (context.IsSwingBarAttached && context.activeSwingBar != null) context.activeSwingBar.Detach();
        }
    }
}
