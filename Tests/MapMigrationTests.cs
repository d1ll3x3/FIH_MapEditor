using Xunit;

namespace FIHMapEditor.Tests
{
    public class MapMigrationTests
    {
        [Fact]
        public void NormalizeBackfillsCollectionsAndStableIds()
        {
            var map = new MapFile { MapId = null, Objects = null, Checkpoints = null,
                ResetZones = null, SoccerGoals = null, Ball = new BallData(), Scoreboard = new ScoreboardData() };
            MapMigrations.Normalize(map);
            Assert.False(string.IsNullOrWhiteSpace(map.MapId));
            Assert.NotNull(map.Objects);
            Assert.NotNull(map.Checkpoints);
            Assert.NotNull(map.ResetZones);
            Assert.NotNull(map.SoccerGoals);
            Assert.False(string.IsNullOrWhiteSpace(map.Ball.Uid));
            Assert.False(string.IsNullOrWhiteSpace(map.Scoreboard.Uid));
        }
    }
}
