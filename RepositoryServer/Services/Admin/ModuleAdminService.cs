using Microsoft.EntityFrameworkCore;
using OneOf;
using OneOf.Types;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.RepoServerDb.Models;
using Semver;
using Version = OpenShock.RepositoryServer.RepoServerDb.Models.Version;

namespace OpenShock.RepositoryServer.Services.Admin;

/// <summary>
/// Desktop modules and their published versions.
/// </summary>
/// <remarks>
/// Creating a module is an administrative act, and assigning its owning repository is the
/// authorization decision for publishing to it. A module with no owner is closed to every publisher
/// rather than open to any, so leaving that unset is the safe default for a module not yet wired to
/// its CI.
/// </remarks>
public sealed class ModuleAdminService
{
    private const int Sha256Length = 32;

    private readonly RepoServerContext _db;

    public ModuleAdminService(RepoServerContext db)
    {
        _db = db;
    }

    public Task<Module[]> ListAsync(CancellationToken ct = default) =>
        _db.Modules
            .Include(m => m.Versions)
            .OrderBy(m => m.Id)
            .ToArrayAsync(ct);

    /// <summary>
    /// One module with its published versions, for the page that manages it. Null when there is no
    /// such module, which the page renders rather than treating as an error.
    /// </summary>
    public Task<Module?> FindAsync(string moduleId, CancellationToken ct = default) =>
        _db.Modules
            .Include(m => m.Versions)
            .FirstOrDefaultAsync(m => m.Id == moduleId.ToLowerInvariant(), ct);

    public async Task<OneOf<Module, ReferenceNotFound>> UpsertAsync(
        string moduleId, string name, string description, Uri? sourceUrl, Uri? iconUrl,
        Guid? repositoryId, CancellationToken ct = default)
    {
        if (repositoryId is { } id && !await _db.Repositories.AnyAsync(r => r.Id == id, ct))
        {
            return new ReferenceNotFound("repository");
        }

        var normalizedId = moduleId.ToLowerInvariant();
        var module = await _db.Modules.FirstOrDefaultAsync(m => m.Id == normalizedId, ct);

        if (module is null)
        {
            // Name and Description are required, so they are set here rather than only in the
            // assignments below, which exist to cover the update path.
            module = new Module { Id = normalizedId, Name = name, Description = description };
            _db.Modules.Add(module);
        }

        module.Name = name;
        module.Description = description;
        module.SourceUrl = sourceUrl;
        module.IconUrl = iconUrl;
        module.RepositoryId = repositoryId;

        await _db.SaveChangesAsync(ct);
        return module;
    }

    public async Task<OneOf<Success, NotFound>> DeleteAsync(string moduleId, CancellationToken ct = default)
    {
        var deleted = await _db.Modules
            .Where(m => m.Id == moduleId.ToLowerInvariant())
            .ExecuteDeleteAsync(ct);

        if (deleted <= 0)
        {
            return new NotFound();
        }

        return new Success();
    }

    public Task<Version[]> ListVersionsAsync(string moduleId, CancellationToken ct = default) =>
        _db.Versions
            .Where(v => v.Module == moduleId.ToLowerInvariant())
            .OrderBy(v => v.VersionName)
            .ToArrayAsync(ct);

    /// <summary>
    /// Registers a published version of a module, or corrects one already registered.
    /// </summary>
    /// <remarks>
    /// The CI/CD endpoint refuses to republish a version that exists, because the zip it uploaded is
    /// immutable once clients can see it. This is the deliberate override: an admin correcting a
    /// changelog link, or entering a version whose zip is already hosted somewhere the server never
    /// uploaded to. It writes only the row that advertises the zip — no bytes are moved — so the
    /// digest is the admin's to get right, and it is validated here rather than trusted.
    /// <para>
    /// A null <c>zipUrl</c> means the zip is in module storage at <see cref="Utils.ModuleZip.StoragePath"/>,
    /// with its URL built from <c>Modules:CdnBaseUrl</c> when served.
    /// </para>
    /// </remarks>
    public async Task<OneOf<Version, ReferenceNotFound, InvalidVersion, InvalidHash>> UpsertVersionAsync(
        string moduleId, string versionName, Uri? zipUrl, byte[] hashSha256, Uri? changelogUrl,
        Uri? releaseUrl, CancellationToken ct = default)
    {
        var normalizedModule = moduleId.ToLowerInvariant();
        var normalizedVersion = versionName.ToLowerInvariant();

        // Strict, and matching the length the CI/CD endpoint parses with: the index is built by
        // parsing these back out, so anything this accepts and that cannot has to be caught here.
        if (!SemVersion.TryParse(normalizedVersion, SemVersionStyles.Strict, out _, maxLength: 64))
        {
            return new InvalidVersion(versionName);
        }

        if (hashSha256.Length != Sha256Length)
        {
            return new InvalidHash(hashSha256.Length);
        }

        if (!await _db.Modules.AnyAsync(m => m.Id == normalizedModule, ct))
        {
            return new ReferenceNotFound("module");
        }

        var version = await _db.Versions
            .FirstOrDefaultAsync(v => v.Module == normalizedModule && v.VersionName == normalizedVersion, ct);

        if (version is null)
        {
            version = new Version
            {
                Module = normalizedModule,
                VersionName = normalizedVersion,
                ZipUrl = zipUrl,
                HashSha256 = hashSha256
            };
            _db.Versions.Add(version);
        }

        version.ZipUrl = zipUrl;
        version.HashSha256 = hashSha256;
        version.ChangelogUrl = changelogUrl;
        version.ReleaseUrl = releaseUrl;

        await _db.SaveChangesAsync(ct);
        return version;
    }

    /// <summary>
    /// Removes a published module version. The zip stays in storage; only the row that advertises it
    /// is removed, so a client mid-download is not cut off.
    /// </summary>
    public async Task<OneOf<Success, NotFound>> DeleteVersionAsync(
        string moduleId, string moduleVersion, CancellationToken ct = default)
    {
        var normalizedModule = moduleId.ToLowerInvariant();
        var normalizedVersion = moduleVersion.ToLowerInvariant();

        var deleted = await _db.Versions
            .Where(v => v.Module == normalizedModule && v.VersionName == normalizedVersion)
            .ExecuteDeleteAsync(ct);

        if (deleted <= 0)
        {
            return new NotFound();
        }

        return new Success();
    }
}
