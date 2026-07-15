using Xunit;

namespace FIHMapEditor.Tests
{
    public class MarkerEditingServiceTests
    {
        [Fact]
        public void AddingMarkersAssignsStableIdsAndUpdatesSession()
        {
            var session = new MapSession();
            var markers = new MarkerEditingService(session);
            var checkpoint = markers.AddCheckpoint(new[] { 1f, 2f, 3f }, 90f, box: true);
            var goal = markers.AddSoccerGoal(new[] { 4f, 5f, 6f }, team: 7);
            Assert.Single(session.Checkpoints);
            Assert.NotNull(checkpoint.Size);
            Assert.False(string.IsNullOrWhiteSpace(checkpoint.Uid));
            Assert.Equal(1, goal.Team);
        }

        [Fact]
        public void ReplacingBallPreservesItsIdentityAndRadius()
        {
            var session = new MapSession { Ball = new BallData { Uid = "ball", Radius = 2f } };
            var markers = new MarkerEditingService(session);
            markers.PlaceBall(new[] { 3f, 2f, 1f });
            Assert.Equal("ball", session.Ball.Uid);
            Assert.Equal(2f, session.Ball.Radius);
        }
    }
}
