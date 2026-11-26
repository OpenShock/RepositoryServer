using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenShock.Internal.Common;
using OpenShock.Internal.Common.Problems;
using OpenShock.RepositoryServer.ApiVersioning;
using OpenShock.RepositoryServer.Config;
using OpenShock.RepositoryServer.Errors;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.Services;
using Semver;
using System.IO.Compression;
using System.Net.Mime;
using System.Security.Claims;
using System.Security.Cryptography;
using Version = OpenShock.RepositoryServer.RepoServerDb.Models.Version;

namespace OpenShock.RepositoryServer.Controllers.V1;

[ApiVersion(1)]
[ApiController]
[Route("/{version:apiVersion}/cicd")]
[Consumes(MediaTypeNames.Application.Json)]
// Ingestion is machine-to-machine and authenticated by GitHub OIDC, not something a reader of the
// API reference can call. Hidden so the published surface is only what consumers actually consume.
[ApiExplorerSettings(IgnoreApi = true)]
[Authorize(AuthenticationSchemes = AuthSchemas.CiCdToken, Policy = AuthSchemas.Policies.PublishModules)]
public class CiCdController : OpenShockControllerBase
{
    private readonly RepoServerContext _db;
    private readonly IStorageService _storage;
    private readonly ApiConfig _apiConfig;

    public CiCdController(RepoServerContext db, IStorageService storage, ApiConfig apiConfig)
    {
        _db = db;
        _storage = storage;
        _apiConfig = apiConfig;
    }

    /// <summary>
    /// Publishes a desktop module version by uploading a zip file.
    /// The file is hashed server-side, uploaded to storage, and recorded in the database.
    /// </summary>
    /// <response code="201">The version was published.</response>
    /// <response code="400">The version is not valid semver, or the uploaded zip is missing or rejected.</response>
    /// <response code="403">The calling repository does not own this module.</response>
    /// <response code="404">The module does not exist.</response>
    /// <response code="409">This version has already been published.</response>
    [HttpPut("modules/{moduleId}/versions/{moduleVersion}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(256 * 1024 * 1024)] // 256 MB
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)] // VersionInvalidSemver, ZipMissing, ZipInvalid, ZipEmpty, ZipPathTraversal, ZipDisallowedDirectory, ZipDisallowedRootFile
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status403Forbidden, MediaTypeNames.Application.ProblemJson)] // ModuleNotOwned
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)] // ModuleNotFound
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status409Conflict, MediaTypeNames.Application.ProblemJson)] // VersionAlreadyExists
    public async Task<IActionResult> PublishVersion(
        [FromRoute] string moduleId,
        [FromRoute] string moduleVersion,
        [FromQuery] string? changelogUrl,
        [FromQuery] string? releaseUrl)
    {
        moduleId = moduleId.ToLowerInvariant();
        moduleVersion = moduleVersion.ToLowerInvariant();

        if (!SemVersion.TryParse(moduleVersion, SemVersionStyles.Strict, out _))
        {
            return Problem(VersionError.VersionInvalidSemver);
        }

        var module = await _db.Modules.FirstOrDefaultAsync(x => x.Id == moduleId);
        if (module is null)
        {
            return Problem(ModuleError.ModuleNotFound);
        }

        // Every registered repository presents an equally valid CI/CD principal, so authentication
        // alone does not say which module the caller may publish to. Unassigned modules are closed to
        // all publishers rather than open to any.
        var rawRepoId = User.FindFirstValue(AuthSchemas.CiCdClaims.RepositoryId);
        if (module.RepositoryId is not { } owningRepositoryId
            || !Guid.TryParse(rawRepoId, out var callerRepositoryId)
            || owningRepositoryId != callerRepositoryId)
        {
            return Problem(ModuleError.ModuleNotOwned);
        }

        // Published versions are immutable. The upsert below is keyed on (module, version), so without
        // this an existing version's zip URL and hash would be silently replaced in place — and
        // integrity checks would then pass against the replacement.
        if (await _db.Versions.AnyAsync(v => v.Module == moduleId && v.VersionName == moduleVersion))
        {
            return Problem(VersionError.VersionAlreadyExists);
        }

        var file = Request.Form.Files.GetFile("zip");
        if (file == null)
        {
            return Problem(ModuleError.ZipMissing);
        }

        // Read file into memory for validation + hashing + storage upload
        using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream);
        var fileBytes = memoryStream.ToArray();

        // Validate zip contents
        memoryStream.Position = 0;
        try
        {
            using var zip = new ZipArchive(memoryStream, ZipArchiveMode.Read, leaveOpen: true);

            if (zip.Entries.Count == 0)
                return Problem(ModuleError.ZipEmpty);

            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.Contains("..") ||
                    Path.IsPathRooted(entry.FullName))
                    return Problem(ModuleError.ZipPathTraversal);

                // Determine depth and root segment
                var normalized = entry.FullName.Replace('\\', '/');
                var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length == 0)
                    continue;

                // Directory entries at root: only "wwwroot/" is allowed
                if (segments.Length >= 2 && !segments[0].Equals("wwwroot", StringComparison.OrdinalIgnoreCase))
                    return Problem(ModuleError.ZipDisallowedDirectory(segments[0]));

                // Files at root: only .dll, .pdb, .json
                if (segments.Length == 1 && !entry.FullName.EndsWith('/'))
                {
                    var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (ext is not (".dll" or ".pdb" or ".json"))
                        return Problem(ModuleError.ZipDisallowedRootFile(entry.Name));
                }
            }
        }
        catch (InvalidDataException)
        {
            return Problem(ModuleError.ZipInvalid);
        }

        // Compute SHA256 hash server-side
        var hashBytes = SHA256.HashData(fileBytes);

        // Upload to storage
        var storagePath = $"modules/{moduleId}/{moduleVersion}/module.zip";
        using var uploadStream = new MemoryStream(fileBytes);
        await _storage.UploadFileAsync(storagePath, uploadStream);

        // Construct public download URL
        var cdnBase = _apiConfig.Modules.CdnBaseUrl.TrimEnd('/');
        var zipUrl = new Uri($"{cdnBase}/{storagePath}");

        var version = new Version
        {
            Module = moduleId,
            VersionName = moduleVersion,
            ZipUrl = zipUrl,
            HashSha256 = hashBytes,
            ChangelogUrl = changelogUrl != null ? new Uri(changelogUrl) : null,
            ReleaseUrl = releaseUrl != null ? new Uri(releaseUrl) : null
        };

        var executed = await _db.Versions.Upsert(version).On(x => new { x.Module, x.VersionName }).RunAsync();
        if (executed <= 0) throw new Exception("Failed to upsert version");

        return Created();
    }
}
