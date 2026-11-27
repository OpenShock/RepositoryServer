using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenShock.RepositoryServer.Enums;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.RepoServerDb.Models;

namespace OpenShock.RepositoryServer.Tests.Integration.Tests;

/// <summary>
/// Desktop module publishing shares the CI/CD principal with firmware ingestion, so it needs the same
/// authorization boundary: a module belongs to one repository, and nobody else may write to it.
/// </summary>
[NotInParallel("repo-server-integration")]
public class DesktopCiCdControllerTests
{
    private const string ModuleId = "test-module";

    [ClassDataSource<WebApplicationFactory>(Shared = SharedType.PerTestSession)]
    public required WebApplicationFactory Factory { get; init; }

    [Before(Test)]
    public Task Setup() => Factory.ResetDatabaseAsync();

    [Test]
    public async Task Publish_ByOwningRepository_Succeeds()
    {
        var ownerId = await RegisterRepositoryAsync("openshock", "desktop");
        await SeedModuleAsync(ownerId);

        using var client = Factory.CreateCiCdClient(ownerId);
        var response = await PublishAsync(client, "1.0.0");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Module zips go to the module storage, at its root, and are advertised under Modules:CdnBaseUrl
        await Assert.That(Factory.StoredModuleFileExists($"{ModuleId}/1.0.0/module.zip")).IsTrue();
        await Assert.That(Factory.StoredFileExists($"{ModuleId}/1.0.0/module.zip")).IsFalse();

        var zipUrl = await Factory.UseDbAsync(db => db.Versions
            .Where(v => v.Module == ModuleId && v.VersionName == "1.0.0")
            .Select(v => v.ZipUrl)
            .SingleAsync());
        await Assert.That(zipUrl.ToString())
            .IsEqualTo($"https://cdn-test.openshock.example/repo/{ModuleId}/1.0.0/module.zip");
    }

    [Test]
    public async Task Publish_FromDifferentRepository_IsForbidden()
    {
        var ownerId = await RegisterRepositoryAsync("openshock", "desktop");
        var intruderId = await RegisterRepositoryAsync("attacker", "evil-repo");
        await SeedModuleAsync(ownerId);

        using var client = Factory.CreateCiCdClient(intruderId);
        var response = await PublishAsync(client, "1.0.0");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Publish_ToModuleWithNoOwner_IsForbidden()
    {
        var repoId = await RegisterRepositoryAsync("openshock", "desktop");
        await SeedModuleAsync(owningRepositoryId: null);

        // Unassigned modules are closed to everyone rather than open to anyone.
        using var client = Factory.CreateCiCdClient(repoId);
        var response = await PublishAsync(client, "1.0.0");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Publish_ExistingVersion_IsRejectedAsImmutable()
    {
        var ownerId = await RegisterRepositoryAsync("openshock", "desktop");
        await SeedModuleAsync(ownerId);

        using var client = Factory.CreateCiCdClient(ownerId);
        var first = await PublishAsync(client, "1.0.0");
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // The upsert is keyed on (module, version), so without a guard this would silently replace the
        // published zip URL and hash in place.
        var second = await PublishAsync(client, "1.0.0");
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Publish_WithOnlyTheFirmwareScope_IsForbidden()
    {
        var ownerId = await RegisterRepositoryAsync("openshock", "desktop");
        await SeedModuleAsync(ownerId);

        // The mirror of the firmware check: a firmware-only grant must not reach module publishing.
        using var client = Factory.CreateCiCdClient(ownerId, scopes: "publish_firmware");
        var response = await PublishAsync(client, "1.0.0");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    // ---- Helpers ----

    private async Task<Guid> RegisterRepositoryAsync(string owner, string repo)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RepoServerContext>();

        var row = new SourceRepository
        {
            Id = Guid.NewGuid(),
            Provider = RepositoryProvider.Github,
            Owner = owner,
            Repo = repo
        };
        db.Repositories.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private async Task SeedModuleAsync(Guid? owningRepositoryId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RepoServerContext>();

        db.Modules.Add(new Module
        {
            Id = ModuleId,
            Name = "Test Module",
            Description = "Fixture",
            RepositoryId = owningRepositoryId
        });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> PublishAsync(HttpClient client, string version)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(BuildModuleZip()), "zip", "module.zip");

        return await client.PutAsync($"/1/cicd/modules/{ModuleId}/versions/{version}", content);
    }

    /// <summary>Minimal zip passing the controller's root-entry validation (.dll/.pdb/.json only).</summary>
    private static byte[] BuildModuleZip()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("module.dll");
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes("not-a-real-assembly"));
        }
        return memory.ToArray();
    }
}
