using Xunit;

namespace FIHMapEditor.Tests
{
    public class MapOwnershipTests
    {
        [Fact]
        public void KnownOwnerIsTheOnlyOwner()
        {
            Assert.True(MapOwnership.IsOwner(ownerSteamId: 7, authorSteamId: 0, selfSteamId: 7, holdsOwnerToken: false));
            // Holding the token or being the listed author never overrides a different owner.
            Assert.False(MapOwnership.IsOwner(7, authorSteamId: 9, selfSteamId: 9, holdsOwnerToken: true));
            // Without Steam you can't prove you are the owner of a map that has one.
            Assert.False(MapOwnership.IsOwner(7, 0, selfSteamId: 0, holdsOwnerToken: false));
        }

        [Fact]
        public void LegacyMapsFallBackToTokenOrUnpublished()
        {
            Assert.True(MapOwnership.IsOwner(0, authorSteamId: 0, selfSteamId: 0, holdsOwnerToken: false));
            Assert.True(MapOwnership.IsOwner(0, authorSteamId: 5, selfSteamId: 3, holdsOwnerToken: true));
            Assert.True(MapOwnership.IsOwner(0, authorSteamId: 5, selfSteamId: 5, holdsOwnerToken: false));
            Assert.False(MapOwnership.IsOwner(0, authorSteamId: 5, selfSteamId: 3, holdsOwnerToken: false));
        }

        [Fact]
        public void PeerChangesAreAcceptedOnlyFromTheOwner()
        {
            Assert.True(MapOwnership.AcceptsPeerChange(ownerSteamId: 7, editorSteamId: 7));
            Assert.False(MapOwnership.AcceptsPeerChange(ownerSteamId: 7, editorSteamId: 8));
            Assert.True(MapOwnership.AcceptsPeerChange(ownerSteamId: 0, editorSteamId: 8));
        }
    }
}
