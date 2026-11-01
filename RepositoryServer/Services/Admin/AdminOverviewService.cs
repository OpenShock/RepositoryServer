using Microsoft.EntityFrameworkCore;
using OpenShock.RepositoryServer.Enums;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.Utils;

namespace OpenShock.RepositoryServer.Services.Admin;

public enum AttentionSeverity
{
    /// <summary>Publishing or serving is broken right now.</summary>
    Blocking,

    /// <summary>Something will not work as an admin probably expects.</summary>
    Warning,

    /// <summary>Worth knowing, nothing is wrong.</summary>
    Notice
}

public sealed record AttentionItem(AttentionSeverity Severity, string Title, string Detail, string Href, string ActionLabel);

/// <summary>What a hub on <paramref name="Channel"/> is offered, which may have come from a stabler channel.</summary>
public sealed record ChannelStatus(ReleaseChannel Channel, string? Version, ReleaseChannel? PublishedOn, DateTimeOffset? ReleasedAt);

public sealed record InFlightRelease(Guid Id, string Version, ReleaseChannel Channel, ReleaseStatus Status, DateTimeOffset CreatedAt);

public sealed record AdminOverview
{
    public required IReadOnlyList<AttentionItem> Attention { get; init; }
    public required IReadOnlyList<ChannelStatus> Channels { get; init; }
    public required IReadOnlyList<InFlightRelease> InFlight { get; init; }

    public required int Chips { get; init; }
    public required int Boards { get; init; }
    public required int DiscontinuedBoards { get; init; }
    public required int UsbDevices { get; init; }
    public required int SerialFilters { get; init; }
    public required int Advisories { get; init; }
    public required int CriticalAdvisories { get; init; }
    public required int FirmwareVersions { get; init; }
    public required int Modules { get; init; }
    public required int ModuleVersions { get; init; }
    public required int Publishers { get; init; }
    public required int FirmwarePublishers { get; init; }
    public required int Webhooks { get; init; }
    public required int EnabledWebhooks { get; init; }

    /// <summary>
    /// Whether CI can publish firmware at all: release init rejects a board it does not know, and a
    /// repository needs the firmware scope. Until then this is the one thing to say about the instance,
    /// so it is reported here rather than as three separate attention items.
    /// </summary>
    public bool SetupComplete => Chips > 0 && Boards > 0 && FirmwarePublishers > 0;
}

/// <summary>
/// Reads the state of the whole server for the overview page, and decides what about it needs a person.
/// </summary>
/// <remarks>
/// The judgement lives here rather than in the page so it can be tested. An item belongs in
/// <see cref="AdminOverview.Attention"/> only if there is something to do about it, because a list
/// that always has entries in it is one that stops being read.
/// </remarks>
public sealed class AdminOverviewService
{
    private readonly RepoServerContext _db;

    public AdminOverviewService(RepoServerContext db)
    {
        _db = db;
    }

    public async Task<AdminOverview> GetAsync(CancellationToken ct = default)
    {
        // These tables hold tens of rows and every one of them is needed to judge them, so they are
        // read as small projections and judged in memory rather than as a conditional aggregate each.
        var publisherScopes = await _db.Repositories.AsNoTracking().Select(r => r.Scopes).ToArrayAsync(ct);
        var webhooks = await _db.DiscordWebhooks.AsNoTracking()
            .Select(w => new { w.Enabled, w.Events })
            .ToArrayAsync(ct);
        var modules = await _db.Modules.AsNoTracking()
            .Select(m => new { m.Id, m.RepositoryId, Versions = m.Versions.Select(v => v.VersionName).ToArray() })
            .ToArrayAsync(ct);
        var inFlight = await _db.FirmwareReleases.AsNoTracking()
            .Where(r => r.Status == ReleaseStatus.Editing || r.Status == ReleaseStatus.Staging)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new InFlightRelease(r.Id, r.Version, r.Channel, r.Status, r.CreatedAt))
            .ToArrayAsync(ct);

        var channels = new List<ChannelStatus>();
        foreach (var channel in Enum.GetValues<ReleaseChannel>())
        {
            // The same query a hub's update check runs, so this shows what hubs are really offered.
            var visible = ReleaseChannels.VisibleTo(channel);
            var latest = await _db.FirmwareVersions.AsNoTracking()
                .Where(v => visible.Contains(v.Channel))
                .OrderByNewest()
                .Select(v => new { v.Version, v.Channel, v.ReleaseDate })
                .FirstOrDefaultAsync(ct);

            channels.Add(new ChannelStatus(channel, latest?.Version, latest?.Channel, latest?.ReleaseDate));
        }

        var attention = new List<AttentionItem>();

        // A version that does not parse takes down the whole index rather than only itself.
        var unparseable = modules
            .SelectMany(m => m.Versions.Where(v => Utils.ModuleVersions.Parse(v) is null).Select(v => (m.Id, Version: v)))
            .ToArray();
        if (unparseable.Length > 0)
        {
            var (moduleId, version) = unparseable[0];
            attention.Add(new AttentionItem(
                AttentionSeverity.Blocking,
                unparseable.Length == 1
                    ? $"Module version {moduleId} {version} is not valid semver"
                    : $"{unparseable.Length} module versions are not valid semver",
                "The index parses every stored version, so this breaks it for every desktop client.",
                unparseable.Select(u => u.Id).Distinct().Count() == 1
                    ? $"/admin/desktop/modules/{Uri.EscapeDataString(moduleId)}"
                    : "/admin/desktop/modules",
                "Fix versions"));
        }

        var editing = inFlight.Count(r => r.Status == ReleaseStatus.Editing);
        if (editing > 0)
        {
            attention.Add(new AttentionItem(
                AttentionSeverity.Warning,
                editing == 1
                    ? "1 release is blocked on its changelog"
                    : $"{editing} releases are blocked on their changelogs",
                "The changelog failed to parse, so it cannot publish until the notes are fixed.",
                "/admin/firmware/releases",
                "Fix changelog"));
        }

        var unscoped = publisherScopes.Count(s => s.Length == 0);
        if (unscoped > 0)
        {
            attention.Add(new AttentionItem(
                AttentionSeverity.Warning,
                unscoped == 1 ? "1 publisher has no scopes" : $"{unscoped} publishers have no scopes",
                "It authenticates but cannot publish anything. Grant a scope, or revoke it.",
                "/admin/repositories",
                "Review"));
        }

        var unassigned = modules.Count(m => m.RepositoryId is null);
        if (unassigned > 0)
        {
            attention.Add(new AttentionItem(
                AttentionSeverity.Warning,
                unassigned == 1 ? "1 module has no owner" : $"{unassigned} modules have no owner",
                "No CI may publish to a module without an owning repository.",
                "/admin/desktop/modules",
                "Assign"));
        }

        if (modules.Length > 0 && !publisherScopes.Any(s => s.Contains(RepositoryScope.PublishModules)))
        {
            attention.Add(new AttentionItem(
                AttentionSeverity.Warning,
                "No publisher may publish desktop modules",
                "There are modules, but no repository holds the module publishing scope.",
                "/admin/repositories",
                "Review"));
        }

        var silent = webhooks.Count(w => w.Enabled && w.Events.Length == 0);
        if (silent > 0)
        {
            attention.Add(new AttentionItem(
                AttentionSeverity.Warning,
                silent == 1 ? "1 webhook subscribes to nothing" : $"{silent} webhooks subscribe to nothing",
                "It is enabled but has no events, so it will never fire.",
                "/admin/discord-webhooks",
                "Review"));
        }

        if (!webhooks.Any(w => w.Enabled))
        {
            attention.Add(new AttentionItem(
                AttentionSeverity.Notice,
                "Nobody is notified about publishes",
                "There is no enabled Discord webhook.",
                "/admin/discord-webhooks",
                "Add webhook"));
        }

        return new AdminOverview
        {
            Attention = attention.OrderBy(a => a.Severity).ToArray(),
            Channels = channels,
            InFlight = inFlight,
            Chips = await _db.FirmwareChips.CountAsync(ct),
            Boards = await _db.FirmwareBoards.CountAsync(ct),
            DiscontinuedBoards = await _db.FirmwareBoards.CountAsync(b => b.Discontinued, ct),
            UsbDevices = await _db.UsbDevices.CountAsync(ct),
            SerialFilters = await _db.UsbSerialFilters.CountAsync(ct),
            Advisories = await _db.FirmwareAdvisories.CountAsync(ct),
            CriticalAdvisories = await _db.FirmwareAdvisories.CountAsync(a => a.Severity == AdvisorySeverity.Critical, ct),
            FirmwareVersions = await _db.FirmwareVersions.CountAsync(ct),
            Modules = modules.Length,
            ModuleVersions = modules.Sum(m => m.Versions.Length),
            Publishers = publisherScopes.Length,
            FirmwarePublishers = publisherScopes.Count(s => s.Contains(RepositoryScope.PublishFirmware)),
            Webhooks = webhooks.Length,
            EnabledWebhooks = webhooks.Count(w => w.Enabled)
        };
    }
}
