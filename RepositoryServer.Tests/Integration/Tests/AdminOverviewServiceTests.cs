using OpenShock.RepositoryServer.Enums;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.RepoServerDb.Models;
using OpenShock.RepositoryServer.Services.Admin;
using Module = OpenShock.RepositoryServer.RepoServerDb.Models.Module;
using Version = OpenShock.RepositoryServer.RepoServerDb.Models.Version;

namespace OpenShock.RepositoryServer.Tests.Integration.Tests;

/// <summary>
/// What the overview tells an admin needs them. The list is only worth reading if it is empty when
/// nothing is wrong, so these check both directions.
/// </summary>
[NotInParallel("repo-server-integration")]
public class AdminOverviewServiceTests
{
    private const string Commit = "abc1234567890abcdef1234567890abcdef12345";

    [ClassDataSource<WebApplicationFactory>(Shared = SharedType.PerTestSession)]
    public required WebApplicationFactory Factory { get; init; }

    [Before(Test)]
    public Task Setup() => Factory.ResetDatabaseAsync();

    private Task<AdminOverview> GetAsync() => Factory.UseAsync((AdminOverviewService s) => s.GetAsync());

    private static SourceRepository Publisher(params RepositoryScope[] scopes) => new()
    {
        Id = Guid.NewGuid(),
        Provider = RepositoryProvider.Github,
        Owner = "OpenShock",
        Repo = $"repo-{Guid.NewGuid():N}",
        Scopes = scopes
    };

    /// <summary>A catalog CI can publish against, so tests see only what they add themselves.</summary>
    private static Guid SeedComplete(RepoServerContext db)
    {
        var chip = new FirmwareChip { Id = Guid.NewGuid(), Name = "ESP32", Architecture = null };
        db.FirmwareChips.Add(chip);
        db.FirmwareBoards.Add(new FirmwareBoard { Id = Guid.NewGuid(), Name = "Board", ChipId = chip.Id, RequiredArtifactTypes = [] });

        var publisher = Publisher(RepositoryScope.PublishFirmware, RepositoryScope.PublishModules);
        db.Repositories.Add(publisher);

        db.DiscordWebhooks.Add(new DiscordWebhook
        {
            Id = Guid.NewGuid(),
            Name = "Releases",
            Url = "https://discord.com/api/webhooks/1/token",
            Events = [DiscordNotificationEvent.FirmwareReleasePublished],
            Enabled = true
        });

        return publisher.Id;
    }

    [Test]
    public async Task EmptyInstance_IsNotSetUp()
    {
        var overview = await GetAsync();

        await Assert.That(overview.SetupComplete).IsFalse();
        await Assert.That(overview.Channels.All(c => c.Version is null)).IsTrue();
    }

    [Test]
    public async Task CompleteInstance_NeedsNothing()
    {
        await Factory.UseDbAsync(async db =>
        {
            SeedComplete(db);
            await db.SaveChangesAsync();
        });

        var overview = await GetAsync();

        await Assert.That(overview.SetupComplete).IsTrue();
        await Assert.That(overview.Attention).IsEmpty();
    }

    [Test]
    public async Task EditingRelease_IsFlagged_ButStagingIsOnlyInFlight()
    {
        await Factory.UseDbAsync(async db =>
        {
            var publisher = SeedComplete(db);
            db.FirmwareReleases.Add(Release(publisher, "1.0.0", ReleaseStatus.Editing));
            db.FirmwareReleases.Add(Release(publisher, "1.1.0", ReleaseStatus.Staging));
            db.FirmwareReleases.Add(Release(publisher, "0.9.0", ReleaseStatus.Aborted));
            await db.SaveChangesAsync();
        });

        var overview = await GetAsync();

        await Assert.That(overview.InFlight.Count).IsEqualTo(2);
        await Assert.That(overview.Attention.Count).IsEqualTo(1);
        await Assert.That(overview.Attention[0].Title).Contains("1 release");
    }

    [Test]
    public async Task BrokenModuleVersion_SortsAheadOfWarnings()
    {
        await Factory.UseDbAsync(async db =>
        {
            SeedComplete(db);
            db.Repositories.Add(Publisher());
            db.Modules.Add(new Module { Id = "openshock.overlay", Name = "Overlay", Description = "" });
            db.Versions.Add(new Version
            {
                VersionName = "not-semver",
                Module = "openshock.overlay",
                ZipUrl = new Uri("https://cdn.example.com/module.zip"),
                HashSha256 = new byte[32]
            });
            await db.SaveChangesAsync();
        });

        var overview = await GetAsync();

        await Assert.That(overview.Attention[0].Severity).IsEqualTo(AttentionSeverity.Blocking);
        await Assert.That(overview.Attention[0].Href).IsEqualTo("/admin/desktop/modules/openshock.overlay");
        await Assert.That(overview.Attention.Any(a => a.Title.Contains("no scopes"))).IsTrue();
        await Assert.That(overview.Attention.Any(a => a.Title.Contains("no owner"))).IsTrue();
    }

    [Test]
    public async Task ModulesWithNoModulePublisher_AreFlagged()
    {
        await Factory.UseDbAsync(async db =>
        {
            db.Repositories.Add(Publisher(RepositoryScope.PublishFirmware));
            db.Modules.Add(new Module { Id = "openshock.overlay", Name = "Overlay", Description = "" });
            await db.SaveChangesAsync();
        });

        var overview = await GetAsync();

        await Assert.That(overview.Attention.Any(a => a.Title == "No publisher may publish desktop modules")).IsTrue();
    }

    [Test]
    public async Task Channels_ShowWhatAHubIsOffered_IncludingFromStablerChannels()
    {
        await Factory.UseDbAsync(async db =>
        {
            var publisher = SeedComplete(db);
            db.FirmwareVersions.Add(Published(publisher, "1.0.0", ReleaseChannel.Stable, daysAgo: 10));
            db.FirmwareVersions.Add(Published(publisher, "1.1.0-beta.1", ReleaseChannel.Beta, daysAgo: 1));
            await db.SaveChangesAsync();
        });

        var channels = (await GetAsync()).Channels.ToDictionary(c => c.Channel);

        await Assert.That(channels[ReleaseChannel.Stable].Version).IsEqualTo("1.0.0");
        await Assert.That(channels[ReleaseChannel.Beta].Version).IsEqualTo("1.1.0-beta.1");
        await Assert.That(channels[ReleaseChannel.Develop].Version).IsEqualTo("1.1.0-beta.1");
        await Assert.That(channels[ReleaseChannel.Develop].PublishedOn).IsEqualTo(ReleaseChannel.Beta);
    }

    private static FirmwareRelease Release(Guid publisher, string version, ReleaseStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Version = version,
        Channel = ReleaseChannel.Stable,
        RepositoryId = publisher,
        CommitHash = Commit,
        ReleaseDate = DateTimeOffset.UtcNow,
        Status = status,
        DeclaredBoards = [],
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static FirmwareVersion Published(Guid publisher, string version, ReleaseChannel channel, int daysAgo) => new()
    {
        Version = version,
        Channel = channel,
        ReleaseDate = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        RepositoryId = publisher,
        CommitHash = Commit
    };
}
