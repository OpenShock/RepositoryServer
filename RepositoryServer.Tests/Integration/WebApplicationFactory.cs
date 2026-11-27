using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.Services;
using OpenShock.RepositoryServer.Tests.Integration.Docker;
using TUnit.Core.Interfaces;

namespace OpenShock.RepositoryServer.Tests.Integration;

/// <summary>
/// Custom <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// wired up to a Testcontainers Postgres instance. The harness:
///   * overlays in-memory configuration on top of whatever Program.cs loads from disk
///     (overrides db connection, admin token, local storage path, and forces skip-migration)
///   * removes the <see cref="StagedReleaseCleanupService"/> hosted service so it doesn't
///     race the schema creation inside <see cref="InitializeAsync"/>
///   * applies the real EF migrations (via <see cref="MigrationOpenShockContext"/>, the
///     context the migrations are attributed to) so the suite validates them
/// </summary>
public sealed class WebApplicationFactory
    : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>, IAsyncInitializer
{
    [ClassDataSource<InMemoryDatabase>(Shared = SharedType.PerTestSession)]
    public required InMemoryDatabase PostgreSql { get; init; }

    private readonly string _cdnStoragePath = Path.Combine(
        Path.GetTempPath(), $"repo-server-tests-cdn-{Guid.NewGuid():N}");

    private readonly string _moduleStoragePath = Path.Combine(
        Path.GetTempPath(), $"repo-server-tests-modules-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        // Schema first, host second. Npgsql reads PostgreSQL's type catalogue once, on the first
        // connection a data source opens, and caches it for that data source's lifetime. The enum
        // types are created by the migrations, so anything that connects before they have run leaves
        // the host's data source permanently unable to bind release_channel, repository_provider and
        // friends — the schema is correct and every write of an enum still fails. Building the host
        // here rather than below is what put a startup connection ahead of the migrations.
        await using (var migrationContext = new MigrationOpenShockContext(
                         PostgreSql.Container.GetConnectionString(), debug: false,
                         NullLoggerFactory.Instance))
        {
            await migrationContext.Database.MigrateAsync();
        }

        _ = Server; // force the host to build
    }

    /// <summary>
    /// Returns an <see cref="HttpClient"/> carrying an admin session, as if the caller had completed
    /// a GitHub login. Pass <paramref name="team"/> to model an account that is signed in but outside
    /// the admin team.
    /// </summary>
    public HttpClient CreateAdminClient(string username = "test-admin", string? team = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            TestAdminAuthHandler.UserHeader, username);
        if (team is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                TestAdminAuthHandler.TeamHeader, team);
        }
        return client;
    }

    /// <summary>
    /// Returns an <see cref="HttpClient"/> authenticated as the CI/CD principal of
    /// <paramref name="repositoryId"/>, which must be a registered repository.
    /// </summary>
    public HttpClient CreateCiCdClient(Guid repositoryId, string? commitHash = null, string? scopes = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            TestCiCdAuthHandler.RepositoryIdHeader, repositoryId.ToString());
        if (scopes is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                TestCiCdAuthHandler.ScopesHeader, scopes);
        }
        if (commitHash is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                TestCiCdAuthHandler.CommitHashHeader, commitHash);
        }
        return client;
    }

    /// <summary>
    /// Resolves a scoped service and runs an operation against it.
    /// </summary>
    /// <remarks>
    /// Administration has no HTTP surface: the management UI calls these services directly, so tests
    /// exercise the same entry point the UI does rather than a transport in front of it. Annotate the
    /// lambda parameter to pick the service, e.g.
    /// <c>Factory.UseAsync((CatalogAdminService s) =&gt; s.ListChipsAsync())</c>.
    /// </remarks>
    public async Task<TResult> UseAsync<TService, TResult>(Func<TService, Task<TResult>> operation)
        where TService : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <inheritdoc cref="UseAsync{TService,TResult}"/>
    public async Task UseAsync<TService>(Func<TService, Task> operation)
        where TService : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        await operation(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>
    /// Runs an operation against a scoped <see cref="RepoServerContext"/>, for arranging fixtures and
    /// asserting on what actually reached the database.
    /// </summary>
    public async Task<TResult> UseDbAsync<TResult>(Func<RepoServerContext, Task<TResult>> operation)
    {
        await using var scope = Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<RepoServerContext>());
    }

    /// <inheritdoc cref="UseDbAsync{TResult}"/>
    public async Task UseDbAsync(Func<RepoServerContext, Task> operation)
    {
        await using var scope = Services.CreateAsyncScope();
        await operation(scope.ServiceProvider.GetRequiredService<RepoServerContext>());
    }

    /// <summary>
    /// True when a path exists in the local CDN storage backing this factory. Lets tests assert on
    /// what actually reached storage, rather than inferring it from response bodies.
    /// </summary>
    public bool StoredFileExists(string relativePath) =>
        File.Exists(Path.Combine(_cdnStoragePath, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// True when a path exists in the module storage backing this factory, which is separate from the
    /// firmware storage <see cref="StoredFileExists"/> reads.
    /// </summary>
    public bool StoredModuleFileExists(string relativePath) =>
        File.Exists(Path.Combine(_moduleStoragePath, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Wipes all mutable test data between tests without tearing down the container.
    /// Firmware tables first (FK order), then repositories, then catalog.
    /// </summary>
    public async Task ResetDatabaseAsync(CancellationToken ct = default)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RepoServerContext>();

        await db.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE
                firmware_staged_release_notes,
                firmware_staged_artifacts,
                firmware_releases,
                firmware_release_notes,
                firmware_artifacts,
                firmware_versions,
                firmware_board_usb_devices,
                firmware_chip_usb_devices,
                firmware_boards,
                firmware_chips,
                firmware_advisories,
                usb_serial_filters,
                usb_devices,
                discord_webhooks,
                versions,
                modules,
                repositories
            RESTART IDENTITY CASCADE;
            """, ct);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Program.cs reads and validates ApiConfig BEFORE builder.Build(). For minimal-hosting
        // apps, ConfigureAppConfiguration overlays are only applied at Build() time — too late.
        // UseSetting goes through host configuration, which the deferred host builder flattens
        // into command-line args for Program.Main, so Program's `.AddCommandLine(args)` picks
        // these up before the config is validated.
        var settings = new Dictionary<string, string?>
        {
            ["Db:Conn"] = PostgreSql.Container.GetConnectionString(),
            ["Db:SkipMigration"] = "true",
            ["Db:Debug"] = "false",

            // Admin auth is swapped for a test handler below, so these only have to satisfy config
            // validation. Nothing here is ever contacted: a login is never performed in-process.
            ["GitHub:ClientId"] = "repository-server-tests",
            ["GitHub:ClientSecret"] = "test-client-secret",
            ["GitHub:Organization"] = "OpenShockTests",
            ["GitHub:Team"] = TestAdminAuthHandler.AdminTeam,

            ["CiCd:Audience"] = "openshock-repository-server-test",

            ["Modules:Id"] = "openshock-desktop-modules-test",
            ["Modules:Name"] = "OpenShock Modules (test)",
            ["Modules:Author"] = "OpenShockTests",
            ["Modules:CdnBaseUrl"] = "https://cdn-test.openshock.example/repo",
            ["Modules:Storage:Type"] = "Local",
            ["Modules:Storage:Local:BasePath"] = _moduleStoragePath,

            ["Firmware:CdnBaseUrl"] = "https://cdn-test.openshock.example/firmware",
            ["Firmware:Storage:Type"] = "Local",
            ["Firmware:Storage:Local:BasePath"] = _cdnStoragePath,
            ["Firmware:StagedReleaseTtl"] = "01:00:00",
            ["Firmware:EditingReleaseTtl"] = "7.00:00:00",

            // cdn-test.openshock.example does not resolve, and is not meant to: storage here is a
            // Local backend under _cdnStoragePath, and the assertions read it off disk. Leaving the
            // publish-time reachability probe on would make every publish test fail on a DNS lookup
            // for a hostname invented to be unreachable. The probe has its own tests, which point
            // Firmware:CdnBaseUrl at the test server and check both outcomes.
            ["Firmware:VerifyPublishedArtifacts"] = "false",
        };
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            // Repoint the CI/CD scheme at the test handler — see TestCiCdAuthHandler for what this
            // does and does not substitute. The scheme is already registered as JwtBearer by
            // Program.cs, and registering the same name twice fails host startup, so swap the handler
            // type on the existing registration rather than adding a second one.
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                if (options.SchemeMap.TryGetValue(AuthSchemas.CiCdToken, out var ciCdScheme))
                {
                    ciCdScheme.HandlerType = typeof(TestCiCdAuthHandler);
                }

                // Same swap for the admin session. The cookie handler itself is not under test; what
                // matters is that the policy sees the claims a real login would have left behind.
                if (options.SchemeMap.TryGetValue(AuthSchemas.AdminCookie, out var adminScheme))
                {
                    adminScheme.HandlerType = typeof(TestAdminAuthHandler);
                }
            });

            // The cleanup hosted service fires on startup and queries the DB. In tests
            // we build schema AFTER the host starts, so suppress it to avoid noisy
            // failure logs. Individual tests exercise its logic directly if needed.
            var hostedDescriptor = services.FirstOrDefault(
                d => d.ImplementationType == typeof(StagedReleaseCleanupService));
            if (hostedDescriptor is not null)
            {
                services.Remove(hostedDescriptor);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            try
            {
                foreach (var path in new[] { _cdnStoragePath, _moduleStoragePath })
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                }
            }
            catch
            {
                // best effort — tests shouldn't fail on cleanup
            }
        }
    }
}
