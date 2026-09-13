using Microsoft.AspNetCore.SignalR;

namespace UndoIsle.TeamRelay.Server;

public sealed class TeamHub(RelayStore store) : Hub
{
    private const string TokenItem = "member-token";

    public override async Task OnConnectedAsync()
    {
        var token = AccessToken();
        try
        {
            var member = store.Connect(token, Context.ConnectionId);
            Context.Items[TokenItem] = token;
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(member.TeamId));
            await Clients.Group(GroupName(member.TeamId))
                .SendAsync("ReceiveSnapshot", member.Snapshot);
            await base.OnConnectedAsync();
        }
        catch (RelayException exception)
        {
            Context.Abort();
            throw new HubException(exception.Code);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var status = store.Disconnect(Token(), Context.ConnectionId);
        if (status is not null)
        {
            await Clients.Group(GroupName(status.TeamId))
                .SendAsync("MemberUpdated", status.Member);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task<bool> PublishTelemetry(TeamTelemetryUpdate update)
    {
        try
        {
            var result = store.PublishTelemetry(Token(), update);
            if (result.Accepted)
            {
                await Clients.OthersInGroup(GroupName(result.TeamId))
                    .SendAsync("MemberUpdated", result.Member);
            }

            return result.Accepted;
        }
        catch (RelayException exception)
        {
            throw new HubException(exception.Code);
        }
    }

    public Task Heartbeat()
    {
        try
        {
            store.Heartbeat(Token());
            return Task.CompletedTask;
        }
        catch (RelayException exception)
        {
            throw new HubException(exception.Code);
        }
    }

    public async Task Leave()
    {
        var removal = store.Leave(Token());
        if (removal is null)
        {
            return;
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(removal.TeamId));
        await Clients.Group(GroupName(removal.TeamId))
            .SendAsync("MemberRemoved", removal.MemberId);
        await Clients.Group(GroupName(removal.TeamId))
            .SendAsync("MapPingsChanged", removal.MapPings);
    }

    public async Task<TeamMapPingSnapshot> UpsertMapPing(TeamMapPingMutation mutation)
    {
        try
        {
            var result = store.UpsertMapPing(Token(), mutation);
            await Clients.Group(GroupName(result.TeamId))
                .SendAsync("MapPingsChanged", result.MapPings);
            return result.Ping;
        }
        catch (RelayException exception)
        {
            throw new HubException(exception.Code);
        }
    }

    public async Task<bool> DeleteMapPing(Guid pingId, long expectedRevision)
    {
        try
        {
            var result = store.DeleteMapPing(Token(), pingId, expectedRevision);
            await Clients.Group(GroupName(result.TeamId))
                .SendAsync("MapPingsChanged", result.MapPings);
            return true;
        }
        catch (RelayException exception)
        {
            throw new HubException(exception.Code);
        }
    }

    public static string GroupName(Guid teamId) => $"team:{teamId:N}";

    private string Token() => Context.Items.TryGetValue(TokenItem, out var token)
        ? token as string ?? string.Empty
        : AccessToken();

    private string AccessToken()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext is null)
        {
            return string.Empty;
        }

        var queryToken = httpContext.Request.Query["access_token"].ToString();
        if (!string.IsNullOrWhiteSpace(queryToken))
        {
            return queryToken;
        }

        const string prefix = "Bearer ";
        var authorization = httpContext.Request.Headers.Authorization.ToString();
        return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim()
            : string.Empty;
    }
}
