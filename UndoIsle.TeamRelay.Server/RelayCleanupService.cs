using Microsoft.AspNetCore.SignalR;

namespace UndoIsle.TeamRelay.Server;

public sealed class RelayCleanupService(
    RelayStore store,
    IHubContext<TeamHub> hubContext,
    ILogger<RelayCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var removals = store.RemoveExpired();
            foreach (var removal in removals)
            {
                await hubContext.Clients.Group(TeamHub.GroupName(removal.TeamId))
                    .SendAsync("MemberRemoved", removal.MemberId, stoppingToken);
                await hubContext.Clients.Group(TeamHub.GroupName(removal.TeamId))
                    .SendAsync(
                        "MemberRemovedV2",
                        new TeamMemberRemoval(removal.MemberId, removal.StateRevision),
                        stoppingToken);
                await hubContext.Clients.Group(TeamHub.GroupName(removal.TeamId))
                    .SendAsync("MapPingsChanged", removal.MapPings, stoppingToken);
                await hubContext.Clients.Group(TeamHub.GroupName(removal.TeamId))
                    .SendAsync(
                        "MapPingsChangedV2",
                        new TeamMapPingBatch(removal.MapPings, removal.StateRevision),
                        stoppingToken);
            }

            if (removals.Count > 0)
            {
                logger.LogInformation("Removed {Count} expired relay members.", removals.Count);
            }
        }
    }
}
