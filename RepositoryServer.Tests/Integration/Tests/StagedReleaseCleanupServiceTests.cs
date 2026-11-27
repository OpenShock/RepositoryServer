using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using OpenShock.RepositoryServer.Config;
using OpenShock.RepositoryServer.Enums;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.RepoServerDb.Models;
using OpenShock.RepositoryServer.Services;

namespace OpenShock.RepositoryServer.Tests.Integration.Tests;

/// <summary>
/// Exercises the TTL cleanup job directly. The hosted service is suppressed in the test harness, so
/// this constructs it against the same real Postgres and drives it with a <see cref="FakeTimeProvider"/>
/// rather than waiting on wall-clock time.
/// </summary>
[NotInParallel("repo-server-integration")]
public class StagedReleaseCleanupServiceTests
{
    [ClassDataSource<WebApplicationFactory>(Shared = SharedType.PerTestSession)]
    public required WebApplicationFactory Factory { get; init; }

    [Before(Test)]
    public Task Setup() => Factory.ResetDatabaseAsync();

    [Test]
    public async Task Tick_AbortsStagingReleasePastItsTtl()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var releaseId = await SeedReleaseAsync(ReleaseStatus.Staging, createdAt: now.AddHours(-2));

        // StagedReleaseTtl is 1h in the test harness, so a 2h-old staging release is expired.
        await RunTickAsync(now);

        await Assert.That(await GetStatusAsync(releaseId)).IsEqualTo(ReleaseStatus.Aborted);
    }

    [Test]
    public async Task Tick_LeavesStagingReleaseInsideItsTtl()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var releaseId = await SeedReleaseAsync(ReleaseStatus.Staging, createdAt: now.AddMinutes(-30));

        await RunTickAsync(now);

        await Assert.That(await GetStatusAsync(releaseId)).IsEqualTo(ReleaseStatus.Staging);
    }

    [Test]
    public async Task Tick_UsesLongerTtlForEditingReleases()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        // Two hours old: past the 1h staging TTL, well inside the 7d editing TTL. An editing release
        // is waiting on a human, so it must not be swept up on the CI timescale.
        var editingId = await SeedReleaseAsync(ReleaseStatus.Editing, createdAt: now.AddHours(-2));
        var stagingId = await SeedReleaseAsync(ReleaseStatus.Staging, createdAt: now.AddHours(-2), version: "1.6.0");

        await RunTickAsync(now);

        await Assert.That(await GetStatusAsync(editingId)).IsEqualTo(ReleaseStatus.Editing);
        await Assert.That(await GetStatusAsync(stagingId)).IsEqualTo(ReleaseStatus.Aborted);
    }

    [Test]
    public async Task Tick_AbortsEditingReleasePastTheEditingTtl()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var releaseId = await SeedReleaseAsync(ReleaseStatus.Editing, createdAt: now.AddDays(-8));

        await RunTickAsync(now);

        await Assert.That(await GetStatusAsync(releaseId)).IsEqualTo(ReleaseStatus.Aborted);
    }

    [Test]
    public async Task Tick_IgnoresPublishedReleases()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var releaseId = await SeedReleaseAsync(ReleaseStatus.Published, createdAt: now.AddDays(-365));

        await RunTickAsync(now);

        await Assert.That(await GetStatusAsync(releaseId)).IsEqualTo(ReleaseStatus.Published);
    }

    // ---- Helpers ----

    /// <summary>
    /// Runs exactly one cleanup pass with the clock pinned to <paramref name="now"/>.
    /// </summary>
    private async Task RunTickAsync(DateTimeOffset now)
    {
        var timeProvider = new FakeTimeProvider(now);

        await using var scope = Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var service = new StagedReleaseCleanupService(
            sp.GetRequiredService<IDbContextFactory<RepoServerContext>>(),
            sp.GetRequiredKeyedService<IStorageService>(StorageKeys.Firmware),
            sp.GetRequiredService<ApiConfig>(),
            sp.GetRequiredService<IDiscordNotificationService>(),
            timeProvider,
            sp.GetRequiredService<ILogger<StagedReleaseCleanupService>>());

        await service.TickAsync(CancellationToken.None);
    }

    private async Task<Guid> SeedReleaseAsync(
        ReleaseStatus status, DateTimeOffset createdAt, string version = "1.5.1")
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RepoServerContext>();

        var repo = await db.Repositories.FirstOrDefaultAsync();
        if (repo is null)
        {
            repo = new SourceRepository
            {
                Id = Guid.NewGuid(),
                Provider = RepositoryProvider.Github,
                Owner = "openshock",
                Repo = "firmware"
            };
            db.Repositories.Add(repo);
        }

        var release = new FirmwareRelease
        {
            Id = Guid.NewGuid(),
            Version = version,
            Channel = ReleaseChannel.Stable,
            RepositoryId = repo.Id,
            CommitHash = "abc1234567890abcdef1234567890abcdef12345",
            ReleaseDate = createdAt,
            Status = status,
            DeclaredBoards = [],
            CreatedAt = createdAt
        };

        db.FirmwareReleases.Add(release);
        await db.SaveChangesAsync();
        return release.Id;
    }

    private async Task<ReleaseStatus> GetStatusAsync(Guid releaseId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RepoServerContext>();
        return await db.FirmwareReleases
            .Where(r => r.Id == releaseId)
            .Select(r => r.Status)
            .FirstAsync();
    }
}
