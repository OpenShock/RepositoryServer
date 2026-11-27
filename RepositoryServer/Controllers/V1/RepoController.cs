using System.Collections.Immutable;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Module = OpenShock.RepositoryServer.Models.Module;
using OpenShock.Internal.Common;
using OpenShock.RepositoryServer.ApiVersioning;
using OpenShock.RepositoryServer.Config;
using OpenShock.RepositoryServer.Models;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.Utils;
using Semver;
using System.Net.Mime;
using Version = OpenShock.RepositoryServer.Models.Version;

namespace OpenShock.RepositoryServer.Controllers.V1;

/// <summary>
/// Desktop module repository: the index the OpenShock Desktop app installs modules from.
/// </summary>
[ApiVersion(1)]
[ApiController]
[Route("/{version:apiVersion}")]
[Consumes(MediaTypeNames.Application.Json)]
public sealed class RepoController : OpenShockControllerBase
{
    private readonly RepoServerContext _db;
    private readonly ApiConfig _apiConfig;

    public RepoController(RepoServerContext db, ApiConfig apiConfig)
    {
        _db = db;
        _apiConfig = apiConfig;
    }

    /// <summary>
    /// Gets the desktop module repository index: every module and its published versions.
    /// </summary>
    /// <response code="200">The repository index.</response>
    [HttpGet]
    [ProducesResponseType<Repository>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
    public async Task<IActionResult> GetRepo()
    {
        var moduleRaw = await _db.Modules.Include(x => x.Versions).ToArrayAsync();

        var modules = moduleRaw.ToImmutableDictionary(x => x.Id, x => new Module()
        {
            Name = x.Name,
            Description = x.Description,
            IconUrl = x.IconUrl,
            SourceUrl = x.SourceUrl,
            Versions = x.Versions.ToImmutableDictionary(
                y => SemVersion.Parse(y.VersionName, SemVersionStyles.Strict, 64), y => new Version
                {
                    Url = ModuleZip.Url(_apiConfig.Modules, y),
                    Sha256Hash = y.HashSha256,
                    ChangelogUrl = y.ChangelogUrl,
                    ReleaseUrl = y.ReleaseUrl
                })
        });

        var repository = new Repository
        {
            Id = _apiConfig.Modules.Id,
            Name = _apiConfig.Modules.Name,
            Author = _apiConfig.Modules.Author,
            Homepage = _apiConfig.Modules.Homepage,
            Modules = modules
        };

        return Ok(repository);
    }
}