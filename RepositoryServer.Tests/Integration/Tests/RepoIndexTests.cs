using System.Text.Json;
using OpenShock.RepositoryServer.RepoServerDb.Models;
using Version = OpenShock.RepositoryServer.RepoServerDb.Models.Version;

namespace OpenShock.RepositoryServer.Tests.Integration.Tests;

/// <summary>
/// The desktop module index: a version hosted in module storage gets its URL built from
/// Modules:CdnBaseUrl, one registered with an explicit URL keeps it.
/// </summary>
[NotInParallel("repo-server-integration")]
public class RepoIndexTests
{
    private const string ModuleId = "index-module";

    [ClassDataSource<WebApplicationFactory>(Shared = SharedType.PerTestSession)]
    public required WebApplicationFactory Factory { get; init; }

    [Before(Test)]
    public Task Setup() => Factory.ResetDatabaseAsync();

    [Test]
    public async Task Index_BuildsHostedUrls_AndKeepsExplicitOnes()
    {
        await Factory.UseDbAsync(async db =>
        {
            db.Modules.Add(new Module { Id = ModuleId, Name = "Index Module", Description = "Fixture" });
            db.Versions.Add(new Version
            {
                Module = ModuleId, VersionName = "1.0.0", HashSha256 = new byte[32],
                ZipUrl = new Uri("https://download.example.com/legacy/index-module_1.0.0.zip")
            });
            db.Versions.Add(new Version
            {
                Module = ModuleId, VersionName = "2.0.0-rc.1", HashSha256 = new byte[32], ZipUrl = null
            });
            await db.SaveChangesAsync();
        });

        using var client = Factory.CreateClient();
        using var index = JsonDocument.Parse(await client.GetStringAsync("/1"));
        var versions = index.RootElement.GetProperty("modules").GetProperty(ModuleId).GetProperty("versions");

        await Assert.That(versions.GetProperty("1.0.0").GetProperty("url").GetString())
            .IsEqualTo("https://download.example.com/legacy/index-module_1.0.0.zip");
        await Assert.That(versions.GetProperty("2.0.0-rc.1").GetProperty("url").GetString())
            .IsEqualTo($"https://cdn-test.openshock.example/repo/{ModuleId}/2.0.0-rc.1/module.zip");
    }
}
