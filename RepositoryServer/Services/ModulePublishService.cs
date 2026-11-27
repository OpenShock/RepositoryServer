using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OneOf;
using OpenShock.Internal.Common.Problems;
using OpenShock.RepositoryServer.Errors;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.Utils;
using Semver;
using Version = OpenShock.RepositoryServer.RepoServerDb.Models.Version;

namespace OpenShock.RepositoryServer.Services;

/// <summary>
/// Publishes a desktop module version from an uploaded zip: validates the archive, hashes it, writes
/// it to module storage and records the version. Shared by the CI/CD endpoint and the admin UI, so a
/// version published by hand is held to exactly the same rules as one published by CI.
/// </summary>
/// <remarks>
/// Who may publish is the caller's decision. The CI/CD endpoint checks that the calling repository
/// owns the module; the admin UI is already restricted to admins.
/// </remarks>
public sealed class ModulePublishService
{
    /// <summary>
    /// Directories allowed at the root of a module zip: <c>wwwroot</c> for static assets, and
    /// <c>libs</c> for the dependencies the desktop app's module load context resolves from, managed
    /// and native, per runtime identifier.
    /// </summary>
    private static readonly string[] AllowedRootDirectories = ["wwwroot", "libs"];

    /// <summary>Upper bound on an uploaded module zip, enforced by both callers.</summary>
    public const long MaxZipSize = 256 * 1024 * 1024;

    private readonly RepoServerContext _db;
    private readonly IStorageService _storage;
    private readonly IDiscordNotificationService _discord;

    public ModulePublishService(
        RepoServerContext db,
        [FromKeyedServices(StorageKeys.Modules)] IStorageService storage,
        IDiscordNotificationService discord)
    {
        _db = db;
        _storage = storage;
        _discord = discord;
    }

    /// <summary>
    /// Publishes <paramref name="moduleVersion"/> of <paramref name="moduleId"/>. Published versions
    /// are immutable: an existing version is refused rather than replaced, because clients verify the
    /// zip against the digest they were shown. <c>commitHash</c> is the commit CI built the zip from,
    /// for the Discord notification; null for an upload by hand.
    /// </summary>
    /// <returns>The new version, or the problem that stopped it. Nothing is written on a problem.</returns>
    public async Task<OneOf<Version, OpenShockProblem>> PublishAsync(
        string moduleId, string moduleVersion, Stream zip, Uri? changelogUrl, Uri? releaseUrl,
        string? commitHash = null, CancellationToken ct = default)
    {
        moduleId = moduleId.ToLowerInvariant();
        moduleVersion = moduleVersion.Trim().ToLowerInvariant();

        if (!SemVersion.TryParse(moduleVersion, SemVersionStyles.Strict, out _, maxLength: 64))
        {
            return VersionError.VersionInvalidSemver;
        }

        if (!await _db.Modules.AnyAsync(m => m.Id == moduleId, ct))
        {
            return ModuleError.ModuleNotFound;
        }

        if (await _db.Versions.AnyAsync(v => v.Module == moduleId && v.VersionName == moduleVersion, ct))
        {
            return VersionError.VersionAlreadyExists;
        }

        // Read into memory for validation, hashing and the storage upload. Bounded by MaxZipSize at
        // both callers.
        using var buffer = new MemoryStream();
        await zip.CopyToAsync(buffer, ct);
        var fileBytes = buffer.ToArray();

        if (fileBytes.Length == 0)
        {
            return ModuleError.ZipMissing;
        }

        if (ValidateZip(fileBytes) is { } zipProblem)
        {
            return zipProblem;
        }

        var hash = SHA256.HashData(fileBytes);

        var storagePath = ModuleZip.StoragePath(moduleId, moduleVersion);
        using (var upload = new MemoryStream(fileBytes))
        {
            await _storage.UploadFileAsync(storagePath, upload, ct);
        }

        var version = new Version
        {
            Module = moduleId,
            VersionName = moduleVersion,
            // Hosted here: the URL is built from Modules:CdnBaseUrl when served, not frozen in the row.
            ZipUrl = null,
            HashSha256 = hash,
            ChangelogUrl = changelogUrl,
            ReleaseUrl = releaseUrl
        };

        _db.Versions.Add(version);
        await _db.SaveChangesAsync(ct);

        await _discord.NotifyDesktopModuleVersionPublishedAsync(moduleId, moduleVersion, commitHash, ct);

        return version;
    }

    /// <summary>
    /// Checks the archive is a module the desktop app can load: a zip with only .dll, .pdb and .json
    /// files at the root, plus optional wwwroot and libs directories, and no entry escaping its folder.
    /// </summary>
    private static OpenShockProblem? ValidateZip(byte[] fileBytes)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(fileBytes), ZipArchiveMode.Read);

            if (archive.Entries.Count == 0)
            {
                return ModuleError.ZipEmpty;
            }

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Contains("..") || Path.IsPathRooted(entry.FullName))
                {
                    return ModuleError.ZipPathTraversal;
                }

                var segments = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length == 0)
                {
                    continue;
                }

                // Directories at the root: only wwwroot and libs
                if (segments.Length >= 2 &&
                    !AllowedRootDirectories.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
                {
                    return ModuleError.ZipDisallowedDirectory(segments[0]);
                }

                // Files at the root: only .dll, .pdb, .json
                if (segments.Length == 1 && !entry.FullName.EndsWith('/'))
                {
                    var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (extension is not (".dll" or ".pdb" or ".json"))
                    {
                        return ModuleError.ZipDisallowedRootFile(entry.Name);
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
            return ModuleError.ZipInvalid;
        }

        return null;
    }
}
