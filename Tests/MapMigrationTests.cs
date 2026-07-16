using System.Text.Json;
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

        [Fact]
        public void NetworkMechanicSpawnerSettingsRoundTripThroughJson()
        {
            var original = new MapFile();
            original.Objects.Add(new MapObjectData
            {
                SourceName = "NetworkedInteractable_Cannon",
                NetworkBoostForce = 31f,
                NetworkBoostAngle = 22f,
                NetworkCannonForce = 47f,
                NetworkCannonAngle = 38f,
                NetworkCannonAirControlBlock = 1.25f,
            });

            string json = JsonSerializer.Serialize(original);
            var loaded = JsonSerializer.Deserialize<MapFile>(json);
            var mechanic = Assert.Single(loaded.Objects);

            Assert.Equal(31f, mechanic.NetworkBoostForce);
            Assert.Equal(22f, mechanic.NetworkBoostAngle);
            Assert.Equal(47f, mechanic.NetworkCannonForce);
            Assert.Equal(38f, mechanic.NetworkCannonAngle);
            Assert.Equal(1.25f, mechanic.NetworkCannonAirControlBlock);
        }
    }
}
