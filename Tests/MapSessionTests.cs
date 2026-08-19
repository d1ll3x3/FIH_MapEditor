using System.Collections.Generic;
using Xunit;

namespace FIHMapEditor.Tests
{
    public class MapSessionTests
    {
        [Fact]
        public void ResetClearsWorkingStateAndKeepsRequestedIdentity()
        {
            var session = new MapSession { Name = "Old", Dirty = true,
                Checkpoints = new List<CheckpointData> { new CheckpointData() } };
            session.Reset("new-id");
            Assert.Equal("Untitled", session.Name);
            Assert.Equal("new-id", session.MapId);
            Assert.False(session.Dirty);
            Assert.Empty(session.Checkpoints);
        }

        [Fact]
        public void ApplyMetadataCopiesMapFacingState()
        {
            var checkpoints = new List<CheckpointData> { new CheckpointData { Uid = "cp" } };
            var map = new MapFile
            {
                Name = "Course",
                MapId = "map",
                Editable = false,
                AuthorName = "OriginalCreator",
                AuthorSteamId = 123456789,
                Checkpoints = checkpoints
            };
            var session = new MapSession();
            session.ApplyMetadata(map);
            Assert.Equal("Course", session.Name);
            Assert.Equal("map", session.MapId);
            Assert.False(session.Editable);
            Assert.Equal("OriginalCreator", session.AuthorName);
            Assert.Equal(123456789, session.AuthorSteamId);
            Assert.Same(checkpoints, session.Checkpoints);
        }
    }
}
