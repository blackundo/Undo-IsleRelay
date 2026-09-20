using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace UndoIsle.TeamRelay.Server;

public sealed class RelayStore
{
    private const string InviteAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TeamEntry> _teams = [];
    private readonly Dictionary<string, MemberEntry> _membersByToken =
        new(StringComparer.Ordinal);
    private readonly RelayOptions _options;

    public RelayStore(IOptions<RelayOptions> options)
    {
        _options = options.Value;
        if (_options.MaxMembersPerTeam is < 2 or > 64
            || _options.HeartbeatIntervalSeconds is < 1 or > 60
            || _options.MemberExpirySeconds <= _options.HeartbeatIntervalSeconds
            || _options.CleanupIntervalSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException("Cấu hình Relay không hợp lệ.");
        }
    }

    public TeamSession CreateTeam(string displayName)
    {
        displayName = NormalizeDisplayName(displayName);
        lock (_gate)
        {
            var team = new TeamEntry(Guid.NewGuid(), NewInviteCode());
            _teams.Add(team.Id, team);
            return AddMember(team, displayName);
        }
    }

    public TeamSession JoinTeam(string inviteCode, string displayName)
    {
        displayName = NormalizeDisplayName(displayName);
        inviteCode = (inviteCode ?? string.Empty).Trim().ToUpperInvariant();
        lock (_gate)
        {
            var team = _teams.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.InviteCode, inviteCode, StringComparison.Ordinal));
            if (team is null)
            {
                throw new RelayException(
                    "invite_not_found",
                    "Không tìm thấy mã mời hoặc nhóm đã hết hạn.",
                    StatusCodes.Status404NotFound);
            }

            if (team.Members.Count >= _options.MaxMembersPerTeam)
            {
                throw new RelayException(
                    "team_full",
                    "Nhóm đã đủ thành viên.",
                    StatusCodes.Status409Conflict);
            }

            return AddMember(team, displayName);
        }
    }

    public MemberContext Connect(string token, string connectionId)
    {
        lock (_gate)
        {
            var member = RequireMember(token);
            member.ConnectionIds.Add(connectionId);
            member.LastSeenAt = DateTimeOffset.UtcNow;
            member.StateRevision = NextRevision(member.Team);
            return new MemberContext(member.Team.Id, member.Id, Snapshot(member.Team));
        }
    }

    public MemberStatus? Disconnect(string token, string connectionId)
    {
        lock (_gate)
        {
            if (!_membersByToken.TryGetValue(token, out var member))
            {
                return null;
            }

            member.ConnectionIds.Remove(connectionId);
            member.LastSeenAt = DateTimeOffset.UtcNow;
            member.StateRevision = NextRevision(member.Team);
            return new MemberStatus(member.Team.Id, ToSnapshot(member));
        }
    }

    public void Heartbeat(string token)
    {
        lock (_gate)
        {
            RequireMember(token).LastSeenAt = DateTimeOffset.UtcNow;
        }
    }

    public TeamSnapshot GetSnapshot(string token)
    {
        lock (_gate)
        {
            return Snapshot(RequireMember(token).Team);
        }
    }

    public TelemetryResult PublishTelemetry(string token, TeamTelemetryUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            var member = RequireMember(token);
            member.LastSeenAt = DateTimeOffset.UtcNow;
            if (update.Sequence <= member.LastSequence)
            {
                return new TelemetryResult(member.Team.Id, false, ToSnapshot(member));
            }

            ValidateTelemetry(update);
            member.LastSequence = update.Sequence;
            member.Telemetry = new TeamMemberTelemetry
            {
                Sequence = update.Sequence,
                Source = Limit(update.Source, 64),
                ServerKey = Limit(update.ServerKey, 160),
                ServerEndpoint = Limit(update.ServerEndpoint, 160),
                ServerName = Limit(update.ServerName, 160),
                MapId = Limit(update.MapId, 64),
                Species = Limit(update.Species, 80),
                HealthPercent = update.HealthPercent,
                HungerPercent = update.HungerPercent,
                ThirstPercent = update.ThirstPercent,
                WorldX = update.WorldX,
                WorldY = update.WorldY,
                MapLeft = update.MapLeft,
                MapTop = update.MapTop,
                HeadingDegrees = update.HeadingDegrees,
                UpdatedAt = member.LastSeenAt
            };
            member.StateRevision = NextRevision(member.Team);
            return new TelemetryResult(member.Team.Id, true, ToSnapshot(member));
        }
    }

    public MapPingResult UpsertMapPing(string token, TeamMapPingMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ValidatePing(mutation);
        lock (_gate)
        {
            var member = RequireMember(token);
            member.LastSeenAt = DateTimeOffset.UtcNow;
            var team = member.Team;
            var now = member.LastSeenAt;
            TeamMapPingSnapshot ping;
            if (team.MapPings.TryGetValue(mutation.PingId, out var current))
            {
                if (current.OwnerMemberId != member.Id)
                {
                    throw new RelayException("ping_not_owned", "Chỉ chủ ping mới được sửa hoặc xóa.");
                }

                if (mutation.ExpectedRevision != current.Revision)
                {
                    throw new RelayException("stale_ping_revision", "Phiên bản ping đã cũ.");
                }

                ping = CreatePing(member, mutation, current.Revision + 1, current.CreatedAt, now);
            }
            else
            {
                if (mutation.PingId == Guid.Empty || mutation.ExpectedRevision != 0)
                {
                    throw new RelayException("invalid_ping", "Ping mới không hợp lệ.");
                }

                var memberPingCount = team.MapPings.Values.Count(value =>
                    value.OwnerMemberId == member.Id);
                if (team.MapPings.Count >= _options.MaxPingsPerTeam
                    || memberPingCount >= _options.MaxPingsPerMember)
                {
                    throw new RelayException("ping_limit_reached", "Đã đạt giới hạn ping của nhóm.");
                }

                ping = CreatePing(member, mutation, 1, now, now);
            }

            team.MapPings[ping.PingId] = ping;
            var stateRevision = NextRevision(team);
            return new MapPingResult(
                team.Id,
                ping,
                team.MapPings.Values.ToArray(),
                stateRevision);
        }
    }

    public MapPingListResult DeleteMapPing(string token, Guid pingId, long expectedRevision)
    {
        lock (_gate)
        {
            var member = RequireMember(token);
            member.LastSeenAt = DateTimeOffset.UtcNow;
            if (!member.Team.MapPings.TryGetValue(pingId, out var ping))
            {
                throw new RelayException("ping_not_found", "Ping không còn tồn tại.");
            }

            if (ping.OwnerMemberId != member.Id)
            {
                throw new RelayException("ping_not_owned", "Chỉ chủ ping mới được sửa hoặc xóa.");
            }

            if (ping.Revision != expectedRevision)
            {
                throw new RelayException("stale_ping_revision", "Phiên bản ping đã cũ.");
            }

            member.Team.MapPings.Remove(pingId);
            var stateRevision = NextRevision(member.Team);
            return new MapPingListResult(
                member.Team.Id,
                member.Team.MapPings.Values.ToArray(),
                stateRevision);
        }
    }

    public MemberRemoval? Leave(string token)
    {
        lock (_gate)
        {
            if (!_membersByToken.TryGetValue(token, out var member))
            {
                return null;
            }

            var stateRevision = RemoveMember(member);
            return new MemberRemoval(
                member.Team.Id,
                member.Id,
                member.Team.MapPings.Values.ToArray(),
                stateRevision);
        }
    }

    public IReadOnlyList<MemberRemoval> RemoveExpired()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-_options.MemberExpirySeconds);
        lock (_gate)
        {
            var expired = _membersByToken.Values
                .Where(member => member.LastSeenAt < cutoff)
                .ToArray();
            var removals = new List<MemberRemoval>(expired.Length);
            foreach (var member in expired)
            {
                var stateRevision = RemoveMember(member);
                removals.Add(new MemberRemoval(
                    member.Team.Id,
                    member.Id,
                    member.Team.MapPings.Values.ToArray(),
                    stateRevision));
            }

            return removals;
        }
    }

    private TeamSession AddMember(TeamEntry team, string displayName)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var member = new MemberEntry(Guid.NewGuid(), displayName, token, team);
        team.Members.Add(member.Id, member);
        member.StateRevision = NextRevision(team);
        _membersByToken.Add(token, member);
        return new TeamSession(
            team.Id,
            member.Id,
            team.InviteCode,
            token,
            _options.MaxMembersPerTeam,
            _options.HeartbeatIntervalSeconds);
    }

    private long RemoveMember(MemberEntry member)
    {
        var stateRevision = NextRevision(member.Team);
        _membersByToken.Remove(member.Token);
        member.Team.Members.Remove(member.Id);
        foreach (var pingId in member.Team.MapPings.Values
                     .Where(ping => ping.OwnerMemberId == member.Id)
                     .Select(ping => ping.PingId)
                     .ToArray())
        {
            member.Team.MapPings.Remove(pingId);
        }

        if (member.Team.Members.Count == 0)
        {
            _teams.Remove(member.Team.Id);
        }

        return stateRevision;
    }

    private MemberEntry RequireMember(string token)
    {
        if (string.IsNullOrWhiteSpace(token)
            || !_membersByToken.TryGetValue(token, out var member))
        {
            throw new RelayException(
                "member_expired",
                "Phiên nhóm đã hết hạn.",
                StatusCodes.Status401Unauthorized);
        }

        return member;
    }

    private TeamSnapshot Snapshot(TeamEntry team) => new(
        team.Id,
        team.InviteCode,
        team.Members.Values.Select(ToSnapshot).ToArray(),
        team.MapPings.Values.OrderBy(ping => ping.CreatedAt).ToArray(),
        team.StateRevision);

    private static TeamMemberSnapshot ToSnapshot(MemberEntry member) => new(
        member.Id,
        member.DisplayName,
        member.ConnectionIds.Count > 0,
        member.LastSeenAt,
        member.Telemetry)
    {
        StateRevision = member.StateRevision
    };

    private static long NextRevision(TeamEntry team) => ++team.StateRevision;

    private string NewInviteCode()
    {
        Span<char> code = stackalloc char[6];
        string candidate;
        do
        {
            for (var index = 0; index < code.Length; index++)
            {
                code[index] = InviteAlphabet[RandomNumberGenerator.GetInt32(InviteAlphabet.Length)];
            }

            candidate = new string(code);
        }
        while (_teams.Values.Any(team => team.InviteCode == candidate));

        return candidate;
    }

    private static string NormalizeDisplayName(string? value)
    {
        var displayName = value?.Trim() ?? string.Empty;
        if (displayName.Length is < 1 or > 32 || displayName.Any(char.IsControl))
        {
            throw new RelayException("invalid_display_name", "Tên hiển thị phải có từ 1 đến 32 ký tự.");
        }

        return displayName;
    }

    private static void ValidateTelemetry(TeamTelemetryUpdate update)
    {
        if (update.Sequence <= 0
            || Invalid(update.HealthPercent, 0, 100)
            || Invalid(update.HungerPercent, 0, 100)
            || Invalid(update.ThirstPercent, 0, 100)
            || Invalid(update.MapLeft, -0.25, 1.25)
            || Invalid(update.MapTop, -0.25, 1.25)
            || Invalid(update.HeadingDegrees, -360, 720)
            || NotFinite(update.WorldX)
            || NotFinite(update.WorldY))
        {
            throw new RelayException("invalid_telemetry", "Dữ liệu vị trí không hợp lệ.");
        }
    }

    private static void ValidatePing(TeamMapPingMutation mutation)
    {
        if (string.IsNullOrWhiteSpace(mutation.MapId)
            || mutation.MapId.Length > 64
            || mutation.Kind is < 0 or > 32
            || !double.IsFinite(mutation.MapLeft)
            || !double.IsFinite(mutation.MapTop)
            || mutation.MapLeft is < 0 or > 1
            || mutation.MapTop is < 0 or > 1
            || !double.IsFinite(mutation.WorldX)
            || !double.IsFinite(mutation.WorldY))
        {
            throw new RelayException("invalid_ping", "Vị trí ping không hợp lệ.");
        }
    }

    private static TeamMapPingSnapshot CreatePing(
        MemberEntry member,
        TeamMapPingMutation mutation,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt) => new(
            mutation.PingId,
            member.Id,
            member.DisplayName,
            revision,
            mutation.MapId.Trim(),
            mutation.Kind,
            mutation.MapLeft,
            mutation.MapTop,
            mutation.WorldX,
            mutation.WorldY,
            createdAt,
            updatedAt);

    private static bool Invalid(double? value, double minimum, double maximum) =>
        value is { } number && (!double.IsFinite(number) || number < minimum || number > maximum);

    private static bool NotFinite(double? value) => value is { } number && !double.IsFinite(number);

    private static string? Limit(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximumLength)];

    private sealed class TeamEntry(Guid id, string inviteCode)
    {
        public Guid Id { get; } = id;
        public string InviteCode { get; } = inviteCode;
        public Dictionary<Guid, MemberEntry> Members { get; } = [];
        public Dictionary<Guid, TeamMapPingSnapshot> MapPings { get; } = [];
        public long StateRevision { get; set; }
    }

    private sealed class MemberEntry(Guid id, string displayName, string token, TeamEntry team)
    {
        public Guid Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public string Token { get; } = token;
        public TeamEntry Team { get; } = team;
        public HashSet<string> ConnectionIds { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
        public long LastSequence { get; set; }
        public long StateRevision { get; set; }
        public TeamMemberTelemetry? Telemetry { get; set; }
    }
}

public sealed record MemberContext(Guid TeamId, Guid MemberId, TeamSnapshot Snapshot);
public sealed record MemberStatus(Guid TeamId, TeamMemberSnapshot Member);
public sealed record MemberRemoval(
    Guid TeamId,
    Guid MemberId,
    IReadOnlyList<TeamMapPingSnapshot> MapPings,
    long StateRevision);
public sealed record TelemetryResult(Guid TeamId, bool Accepted, TeamMemberSnapshot Member);
public sealed record MapPingResult(
    Guid TeamId,
    TeamMapPingSnapshot Ping,
    IReadOnlyList<TeamMapPingSnapshot> MapPings,
    long StateRevision);
public sealed record MapPingListResult(
    Guid TeamId,
    IReadOnlyList<TeamMapPingSnapshot> MapPings,
    long StateRevision);
