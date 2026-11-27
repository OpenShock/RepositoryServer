using Microsoft.EntityFrameworkCore;
using OpenShock.RepositoryServer.Config;
using OpenShock.RepositoryServer.Enums;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.Utils;

namespace OpenShock.RepositoryServer.Services;

/// <summary>
/// Periodic cleanup of expired staged firmware releases. Runs every 5 minutes. Two TTLs:
/// <c>StagedReleaseTtl</c> (default 1h) for abandoned CI releases, <c>EditingReleaseTtl</c>
/// (default 7d) for releases waiting on janitor review.
/// </summary>
public sealed class StagedReleaseCleanupService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<RepoServerContext> _dbFactory;
    private readonly IStorageService _storage;
    private readonly ApiConfig _apiConfig;
    private readonly IDiscordNotificationService _discord;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StagedReleaseCleanupService> _logger;

    public StagedReleaseCleanupService(
        IDbContextFactory<RepoServerContext> dbFactory,
        [FromKeyedServices(StorageKeys.Firmware)] IStorageService storage,
        ApiConfig apiConfig,
        IDiscordNotificationService discord,
        TimeProvider timeProvider,
        ILogger<StagedReleaseCleanupService> logger)
    {
        _dbFactory = dbFactory;
        _storage = storage;
        _apiConfig = apiConfig;
        _discord = discord;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, _timeProvider);

        // Fire once immediately, then on every tick. The DB may be unreachable at
        // startup (migrations still running, Postgres warming up in tests, etc.) —
        // errors are swallowed so the service keeps retrying on the timer.
        await SafeTickAsync(stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (stoppingToken.IsCancellationRequested) return;
            await SafeTickAsync(stoppingToken);
        }
    }

    private async Task SafeTickAsync(CancellationToken ct)
    {
        try
        {
            await TickAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down — no-op
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Staged-release cleanup tick failed");
        }
    }

    /// <summary>
    /// Runs a single cleanup pass. Internal rather than private so tests can drive it deterministically:
    /// <see cref="BackgroundService.StartAsync"/> returns at the first await inside
    /// <see cref="ExecuteAsync"/>, so a test that started and stopped the service would race the very
    /// tick it means to observe.
    /// </summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var stagedDeadline = now - _apiConfig.Firmware.StagedReleaseTtl;
        var editingDeadline = now - _apiConfig.Firmware.EditingReleaseTtl;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var expired = await db.FirmwareReleases
            .Include(r => r.StagedArtifacts)
            .Where(r =>
                (r.Status == ReleaseStatus.Staging && r.CreatedAt < stagedDeadline) ||
                (r.Status == ReleaseStatus.Editing && r.CreatedAt < editingDeadline))
            .ToListAsync(ct);

        if (expired.Count == 0) return;

        foreach (var release in expired)
        {
            try
            {
                // Claim the release with a conditional update before deleting anything.
                //
                // The rows were read moments ago, and publish is a concurrent operation: a release
                // hitting its TTL exactly as CI publishes it would otherwise end up aborted in the
                // database, live in the public API, and deleted from the CDN. Narrowing the UPDATE to
                // the statuses we decided on makes the claim atomic — if publish won, this affects
                // zero rows and we leave the artifacts alone.
                var claimed = await db.FirmwareReleases
                    .Where(r => r.Id == release.Id &&
                                (r.Status == ReleaseStatus.Staging || r.Status == ReleaseStatus.Editing))
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(r => r.Status, ReleaseStatus.Aborted), ct);

                if (claimed == 0)
                {
                    _logger.LogInformation(
                        "Skipped expired release {ReleaseId} (version={Version}) — it left staging concurrently",
                        release.Id, release.Version);
                    continue;
                }

                // Only ever touches this release's own staging prefix. Published artifacts live under
                // {version}/... and are unreachable from here by construction, so an expiring release
                // can never delete bytes a live version is serving.
                await _storage.DeleteDirectoryAsync(
                    FirmwareArtifactFileNames.BuildStagingPrefix(release.Id), ct);

                // release.Status is still the pre-abort value — ExecuteUpdate bypasses the change
                // tracker — which is what makes it possible to tell from the log which TTL fired.
                _logger.LogInformation(
                    "Aborted expired release {ReleaseId} (version={Version}) after the {PriorStatus} TTL expired",
                    release.Id, release.Version, release.Status);

                await _discord.NotifyStagedReleaseExpiredAsync(
                    release.Id,
                    release.Version,
                    release.Channel.ToString().ToLowerInvariant(),
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clean up expired staged release {ReleaseId}", release.Id);
            }
        }
    }
}
