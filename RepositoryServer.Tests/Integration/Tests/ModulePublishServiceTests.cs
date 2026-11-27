using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.RepoServerDb.Models;
using OpenShock.RepositoryServer.Services;

namespace OpenShock.RepositoryServer.Tests.Integration.Tests;

/// <summary>
/// The publish path the admin UI's upload uses. It is the same one the CI/CD endpoint uses, minus
/// the ownership check, which is the endpoint's own.
/// </summary>
[NotInParallel("repo-server-integration")]
public class ModulePublishServiceTests
{
    private const string ModuleId = "upload-module";

    [ClassDataSource<WebApplicationFactory>(Shared = SharedType.PerTestSession)]
    public required WebApplicationFactory Factory { get; init; }

    [Before(Test)]
    public async Task Setup()
    {
        await Factory.ResetDatabaseAsync();
        await Factory.UseDbAsync(async db =>
        {
            // No owning repository: an admin upload must not depend on the module being wired to CI.
            db.Modules.Add(new Module { Id = ModuleId, Name = "Upload Module", Description = "Fixture" });
            await db.SaveChangesAsync();
        });
    }

    [Test]
    public async Task Publish_ValidZip_StoresItAndRecordsTheVersion()
    {
        var zip = BuildZip(("module.dll", "assembly"), ("wwwroot/app.css", "body{}"));

        var result = await PublishAsync("1.2.0", zip, new Uri("https://example.com/release"));

        await Assert.That(result.IsT0).IsTrue();
        await Assert.That(Factory.StoredModuleFileExists($"{ModuleId}/1.2.0/module.zip")).IsTrue();

        var version = await Factory.UseDbAsync(db => db.Versions.SingleAsync(v => v.Module == ModuleId));
        await Assert.That(version.VersionName).IsEqualTo("1.2.0");
        await Assert.That(version.HashSha256).IsEquivalentTo(SHA256.HashData(zip));
        // Hosted here, so no URL is stored: it is built from Modules:CdnBaseUrl when served
        await Assert.That(version.ZipUrl).IsNull();
        await Assert.That(version.ReleaseUrl).IsEqualTo(new Uri("https://example.com/release"));
    }

    [Test]
    public async Task Publish_ZipWithLibsFolder_IsAccepted()
    {
        // The layout ShockOSC and LocalRelay ship: dependencies under libs/, native ones per RID.
        var zip = BuildZip(
            ("OpenShock.ShockOSC.dll", "assembly"),
            ("module.json", "{}"),
            ("libs/CircularBuffer.dll", "dependency"),
            ("libs/linux-x64/libSystem.IO.Ports.Native.so", "native"));

        var result = await PublishAsync("3.3.0", zip);

        await Assert.That(result.IsT0).IsTrue();
    }

    [Test]
    public async Task Publish_OtherRootFolder_IsRefused()
    {
        var result = await PublishAsync("1.0.0", BuildZip(("module.dll", "assembly"), ("runtimes/win/x.dll", "x")));

        await Assert.That(result.IsT1).IsTrue();
        await Assert.That(result.AsT1.Type).IsEqualTo("Module.ZipDisallowedDirectory");
    }

    [Test]
    public async Task Publish_ExistingVersion_IsRefused()
    {
        var zip = BuildZip(("module.dll", "assembly"));
        await PublishAsync("1.0.0", zip);

        var second = await PublishAsync("1.0.0", BuildZip(("module.dll", "different")));

        await Assert.That(second.IsT1).IsTrue();
        await Assert.That(second.AsT1.Type).IsEqualTo("Version.AlreadyExists");
    }

    [Test]
    [Arguments("not-a-version", "Version.InvalidSemVersion")]
    [Arguments("1.0", "Version.InvalidSemVersion")]
    public async Task Publish_InvalidVersion_IsRefused(string version, string expectedType)
    {
        var result = await PublishAsync(version, BuildZip(("module.dll", "assembly")));

        await Assert.That(result.IsT1).IsTrue();
        await Assert.That(result.AsT1.Type).IsEqualTo(expectedType);
    }

    [Test]
    public async Task Publish_DisallowedRootFile_IsRefusedAndNothingIsStored()
    {
        // A version no other test uploads: storage is not wiped between tests, only the database.
        var result = await PublishAsync("0.0.1-rejected", BuildZip(("module.dll", "assembly"), ("run.exe", "nope")));

        await Assert.That(result.IsT1).IsTrue();
        await Assert.That(result.AsT1.Type).IsEqualTo("Module.ZipDisallowedRootFile");
        await Assert.That(Factory.StoredModuleFileExists($"{ModuleId}/0.0.1-rejected/module.zip")).IsFalse();
        await Assert.That(await Factory.UseDbAsync(db => db.Versions.CountAsync())).IsEqualTo(0);
    }

    [Test]
    public async Task Publish_NotAZip_IsRefused()
    {
        var result = await PublishAsync("1.0.0", Encoding.UTF8.GetBytes("definitely not a zip"));

        await Assert.That(result.IsT1).IsTrue();
        await Assert.That(result.AsT1.Type).IsEqualTo("Module.ZipInvalid");
    }

    [Test]
    public async Task Publish_UnknownModule_IsRefused()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ModulePublishService>()
            .PublishAsync("no.such.module", "1.0.0", new MemoryStream(BuildZip(("module.dll", "x"))), null, null);

        await Assert.That(result.IsT1).IsTrue();
        await Assert.That(result.AsT1.Type).IsEqualTo("Module.NotFound");
    }

    private async Task<OneOf.OneOf<RepoServerDb.Models.Version, Internal.Common.Problems.OpenShockProblem>> PublishAsync(
        string version, byte[] zip, Uri? releaseUrl = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ModulePublishService>()
            .PublishAsync(ModuleId, version, new MemoryStream(zip), null, releaseUrl);
    }

    private static byte[] BuildZip(params (string Path, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var stream = archive.CreateEntry(path).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return memory.ToArray();
    }
}
