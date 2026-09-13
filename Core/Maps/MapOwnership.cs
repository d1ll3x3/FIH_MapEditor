namespace FIHMapEditor
{
    /// <summary>
    /// Who may author owner-only content (voice lines). Kept pure so the rules are testable
    /// and identical for the local UI and for validating co-edit operations from peers.
    /// </summary>
    public static class MapOwnership
    {
        /// <param name="ownerSteamId">MapFile.OwnerSteamId; 0 when unknown.</param>
        /// <param name="authorSteamId">MapFile.AuthorSteamId; 0 until first upload.</param>
        /// <param name="selfSteamId">Local Steam account; 0 when Steam is unavailable.</param>
        /// <param name="holdsOwnerToken">Whether this machine has the map's upload token.</param>
        public static bool IsOwner(long ownerSteamId, long authorSteamId, long selfSteamId, bool holdsOwnerToken)
        {
            // A known owner is authoritative: nobody else — not even an offline session with
            // no Steam id — may author for it.
            if (ownerSteamId != 0) return selfSteamId != 0 && selfSteamId == ownerSteamId;
            // Maps from before OwnerSteamId: whoever uploaded it, or nobody yet (a local map).
            return holdsOwnerToken || authorSteamId == 0
                || (selfSteamId != 0 && authorSteamId == selfSteamId);
        }

        /// <summary>
        /// Whether a peer's change to owner-only content should be applied. An unknown owner
        /// (0) can't be checked, so it is trusted like every other co-edit change.
        /// </summary>
        public static bool AcceptsPeerChange(long ownerSteamId, ulong editorSteamId)
            => ownerSteamId == 0 || (ulong)ownerSteamId == editorSteamId;
    }
}
