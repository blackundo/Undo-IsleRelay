using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using UndoIsle.TeamRelay.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection("Relay"));
builder.Services.AddSingleton<RelayStore>();
builder.Services.AddHostedService<RelayCleanupService>();
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = false;
    options.MaximumReceiveMessageSize = 32 * 1024;
});

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "undo-isle-team-relay",
    protocol = 2,
    utc = DateTimeOffset.UtcNow
}));

app.MapPost("/api/v1/teams", (CreateTeamRequest request, RelayStore store) =>
{
    try
    {
        return Results.Ok(store.CreateTeam(
            request.DisplayName,
            request.Tier,
            request.RequestedMaxMembers,
            request.EntitlementProof));
    }
    catch (RelayException exception)
    {
        return ApiError(exception);
    }
});

app.MapPost("/api/v1/teams/join", (JoinTeamRequest request, RelayStore store) =>
{
    try
    {
        return Results.Ok(store.JoinTeam(request.InviteCode, request.DisplayName));
    }
    catch (RelayException exception)
    {
        return ApiError(exception);
    }
});

app.MapDelete("/api/v1/teams/me", async (
    HttpContext context,
    RelayStore store,
    IHubContext<TeamHub> hub,
    CancellationToken cancellationToken) =>
{
    var token = BearerToken(context.Request);
    if (token is null)
    {
        return Results.Json(
            new TeamApiError("member_expired", "Phiên nhóm không hợp lệ."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    try
    {
        var removal = store.Leave(token);
        if (removal is not null)
        {
            await hub.Clients.Group(TeamHub.GroupName(removal.TeamId))
                .SendAsync("MemberRemoved", removal.MemberId, cancellationToken);
            await hub.Clients.Group(TeamHub.GroupName(removal.TeamId))
                .SendAsync(
                    "MemberRemovedV2",
                    new TeamMemberRemoval(removal.MemberId, removal.StateRevision),
                    cancellationToken);
            await hub.Clients.Group(TeamHub.GroupName(removal.TeamId))
                .SendAsync("MapPingsChanged", removal.MapPings, cancellationToken);
            await hub.Clients.Group(TeamHub.GroupName(removal.TeamId))
                .SendAsync(
                    "MapPingsChangedV2",
                    new TeamMapPingBatch(removal.MapPings, removal.StateRevision),
                    cancellationToken);
        }

        return Results.NoContent();
    }
    catch (RelayException exception)
    {
        return ApiError(exception);
    }
});

app.MapHub<TeamHub>("/hubs/team");

app.Run();

static IResult ApiError(RelayException exception) => Results.Json(
    new TeamApiError(exception.Code, exception.Message),
    statusCode: exception.StatusCode);

static string? BearerToken(HttpRequest request)
{
    const string prefix = "Bearer ";
    var authorization = request.Headers.Authorization.ToString();
    return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        ? authorization[prefix.Length..].Trim()
        : null;
}
