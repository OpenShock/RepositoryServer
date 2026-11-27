using System.Net.Mime;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenShock.Internal.Common;
using OpenShock.Internal.Common.Problems;
using OpenShock.RepositoryServer.ApiVersioning;
using OpenShock.RepositoryServer.Errors;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.Services;

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
    private readonly ModulePublishService _publisher;

    public CiCdController(RepoServerContext db, ModulePublishService publisher)
    {
        _db = db;
        _publisher = publisher;
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
    [RequestSizeLimit(ModulePublishService.MaxZipSize)]
    [RequestFormLimits(MultipartBodyLengthLimit = ModulePublishService.MaxZipSize)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)] // VersionInvalidSemver, ZipMissing, ZipInvalid, ZipEmpty, ZipPathTraversal, ZipDisallowedDirectory, ZipDisallowedRootFile
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status403Forbidden, MediaTypeNames.Application.ProblemJson)] // ModuleNotOwned
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)] // ModuleNotFound
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status409Conflict, MediaTypeNames.Application.ProblemJson)] // VersionAlreadyExists
    public async Task<IActionResult> PublishVersion(
        [FromRoute] string moduleId,
        [FromRoute] string moduleVersion,
        [FromQuery] Uri? changelogUrl,
        [FromQuery] Uri? releaseUrl,
        CancellationToken ct)
    {
        var normalizedModuleId = moduleId.ToLowerInvariant();

        var module = await _db.Modules.FirstOrDefaultAsync(x => x.Id == normalizedModuleId, ct);
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

        var file = Request.Form.Files.GetFile("zip");
        if (file is null)
        {
            return Problem(ModuleError.ZipMissing);
        }

        await using var zip = file.OpenReadStream();
        var result = await _publisher.PublishAsync(
            normalizedModuleId, moduleVersion, zip, changelogUrl, releaseUrl,
            User.FindFirstValue(AuthSchemas.CiCdClaims.CommitHash), ct);

        return result.Match<IActionResult>(
            _ => Created(),
            problem => Problem(problem));
    }
}
