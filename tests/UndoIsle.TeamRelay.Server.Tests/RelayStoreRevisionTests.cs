using Microsoft.Extensions.Options;
using UndoIsle.TeamRelay.Server;

namespace UndoIsle.TeamRelay.Server.Tests;

public sealed class RelayStoreRevisionTests
{
    [Fact]
    public void DefaultsKeepMembersForThirtyFiveSecondsAndSweepEveryFiveSeconds()
    {
        var options = new RelayOptions();

        Assert.Equal(35, options.MemberExpirySeconds);
        Assert.Equal(5, options.CleanupIntervalSeconds);
        Assert.True(options.MemberExpirySeconds > options.HeartbeatIntervalSeconds);
    }

    [Fact]
    public void SnapshotAndDeltasCarryMonotonicRoomRevisions()
    {
        var store = CreateStore();
        var session = store.CreateTeam("Alpha");
        var connected = store.Connect(session.MemberToken, "connection-alpha");
        var connectedRevision = connected.Snapshot.StateRevision;

        Assert.True(connectedRevision > 0);
        Assert.Equal(
            connectedRevision,
            Assert.Single(connected.Snapshot.Members).StateRevision);

        var telemetry = store.PublishTelemetry(session.MemberToken, new TeamTelemetryUpdate
        {
            Sequence = 1,
            Source = "isle-live-map",
            ServerKey = "gateway.example:7777",
            ServerEndpoint = "gateway.example:7777",
            ServerName = "Gateway",
            MapId = "gateway",
            Species = "Diabloceratops",
            HealthPercent = 95,
            HungerPercent = 70,
            ThirstPercent = 80,
            MapLeft = 0.4,
            MapTop = 0.6,
            WorldX = 10_000,
            WorldY = -20_000,
            HeadingDegrees = 90
        });

        Assert.True(telemetry.Accepted);
        Assert.True(telemetry.Member.StateRevision > connectedRevision);
        Assert.Equal("gateway.example:7777", telemetry.Member.Telemetry?.ServerEndpoint);

        var afterTelemetry = store.GetSnapshot(session.MemberToken);
        Assert.Equal(telemetry.Member.StateRevision, afterTelemetry.StateRevision);
        Assert.Equal(
            telemetry.Member.StateRevision,
            Assert.Single(afterTelemetry.Members).StateRevision);

        var ping = store.UpsertMapPing(session.MemberToken, new TeamMapPingMutation
        {
            PingId = Guid.NewGuid(),
            MapId = "gateway",
            Kind = 1,
            MapLeft = 0.45,
            MapTop = 0.55,
            WorldX = 12_000,
            WorldY = -21_000
        });
        Assert.True(ping.StateRevision > afterTelemetry.StateRevision);

        var deleted = store.DeleteMapPing(
            session.MemberToken,
            ping.Ping.PingId,
            ping.Ping.Revision);
        Assert.True(deleted.StateRevision > ping.StateRevision);

        var removal = Assert.IsType<MemberRemoval>(store.Leave(session.MemberToken));
        Assert.True(removal.StateRevision > deleted.StateRevision);
    }

    [Fact]
    public void RejectedTelemetryDoesNotAdvanceTheRoomRevision()
    {
        var store = CreateStore();
        var session = store.CreateTeam("Alpha");
        store.Connect(session.MemberToken, "connection-alpha");
        var update = new TeamTelemetryUpdate
        {
            Sequence = 1,
            MapId = "gateway",
            MapLeft = 0.5,
            MapTop = 0.5
        };

        var accepted = store.PublishTelemetry(session.MemberToken, update);
        var rejected = store.PublishTelemetry(session.MemberToken, update);
        var snapshot = store.GetSnapshot(session.MemberToken);

        Assert.True(accepted.Accepted);
        Assert.False(rejected.Accepted);
        Assert.Equal(accepted.Member.StateRevision, rejected.Member.StateRevision);
        Assert.Equal(accepted.Member.StateRevision, snapshot.StateRevision);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(21)]
    [InlineData(25)]
    public void EverySupportedSizeIsAvailableWithoutPro(int size)
    {
        var store = CreateStore();

        var free = store.CreateTeam("Alpha", TeamAccessTier.Free, size);
        var forgedPro = store.CreateTeam("Bravo", TeamAccessTier.Pro, size, "forged.invalid.token");

        Assert.Equal(size, free.MaxMembers);
        Assert.Equal(size, forgedPro.MaxMembers);
    }

    [Fact]
    public void DefaultRoomUsesTwentyFiveMemberLimit()
    {
        var store = CreateStore();

        var session = store.CreateTeam("Alpha");

        Assert.Equal(25, session.MaxMembers);
    }

    private static RelayStore CreateStore() => new(Options.Create(new RelayOptions()));
}
