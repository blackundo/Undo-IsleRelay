namespace UndoIsle.TeamRelay.Server;

public sealed class RelayOptions
{
    public int MaxMembersPerTeam { get; init; } = 8;
    public int MaxPingsPerTeam { get; init; } = 64;
    public int MaxPingsPerMember { get; init; } = 20;
    public int HeartbeatIntervalSeconds { get; init; } = 10;
    public int MemberExpirySeconds { get; init; } = 45;
}
