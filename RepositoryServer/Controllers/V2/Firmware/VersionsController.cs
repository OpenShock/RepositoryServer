using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenShock.Internal.Common;
using OpenShock.Internal.Common.Problems;
using OpenShock.RepositoryServer.ApiVersioning;
using OpenShock.RepositoryServer.Config;
using OpenShock.RepositoryServer.Enums;
using OpenShock.RepositoryServer.Models.Firmware;
using OpenShock.RepositoryServer.Errors;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.Utils;
using System.Net.Mime;
using OpenShock.RepositoryServer.RepoServerDb.Models;

namespace OpenShock.RepositoryServer.Controllers.V2.Firmware;

/// <summary>
/// Published firmware versions and their per-board artifacts.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("/{version:apiVersion}/firmware/versions")]
[Consumes(MediaTypeNames.Application.Json)]
public sealed class VersionsController : OpenShockControllerBase
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    private readonly RepoServerContext _db;
    private readonly ApiConfig _apiConfig;

    public VersionsController(RepoServerContext db, ApiConfig apiConfig)
    {
        _db = db;
        _apiConfig = apiConfig;
    }

    /// <summary>
    /// Lists published firmware versions, newest first.
    /// </summary>
    /// <param name="channel">Optional release channel filter, e.g. <c>stable</c> or <c>beta</c>.</param>
    /// <param name="limit">Page size. Clamped to 1-100, defaults to 20.</param>
    /// <param name="offset">Number of versions to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The matching page of versions.</response>
    /// <response code="400">The channel is not a known release channel.</response>
    [HttpGet]
    [CacheControl(3600)]
    [ProducesResponseType<VersionListResponse>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)] // FirmwareInvalidChannel
    public async Task<IActionResult> ListVersions(
        [FromQuery] string? channel,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct)
    {
        IQueryable<FirmwareVersion> query = _db.FirmwareVersions
            .Include(v => v.RepositoryNavigation)
            .Include(v => v.ReleaseNotes);

        if (!string.IsNullOrWhiteSpace(channel))
        {
            if (!Enum.TryParse<ReleaseChannel>(channel, true, out var firmwareChannel))
            {
                return Problem(FirmwareError.FirmwareInvalidChannel);
            }
            // Cascading, so a beta subscriber's history includes the stable releases they can install.
            var visibleChannels = ReleaseChannels.VisibleTo(firmwareChannel);
            query = query.Where(v => visibleChannels.Contains(v.Channel));
        }

        var total = await query.CountAsync(ct);

        var effectiveLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var effectiveOffset = Math.Max(offset ?? 0, 0);

        var rows = await query
            .OrderByNewest()
            .Skip(effectiveOffset)
            .Take(effectiveLimit)
            .ToListAsync(ct);

        var summaries = rows
            .Select(v => new FirmwareVersionSummary
            {
                Version = v.Version,
                Channel = v.Channel.ToString().ToLowerInvariant(),
                ReleaseDate = v.ReleaseDate,
                Source = FirmwareSourceDto.From(v),
                ReleaseNotes = v.ReleaseNotes
                    .OrderBy(n => n.Index)
                    .Select(n => new FirmwareReleaseNoteDto
                    {
                        Type = n.SectionType.ToString().ToLowerInvariant(),
                        Title = n.Title,
                        Content = n.Content
                    })
                    .ToList()
            })
            .ToList();

        return Ok(new VersionListResponse { Versions = summaries, Total = total });
    }

    /// <summary>
    /// Gets a single published firmware version.
    /// </summary>
    /// <param name="firmwareVersion">Published firmware version, e.g. <c>1.4.0</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The requested version.</response>
    /// <response code="404">The version has not been published.</response>
    [HttpGet("{firmwareVersion}")]
    [CacheControl(86400, immutable: true)]
    [ProducesResponseType<FirmwareReleaseDto>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)] // FirmwareVersionNotFound
    public async Task<IActionResult> GetVersion([FromRoute] string firmwareVersion, CancellationToken ct)
    {
        var version = await _db.FirmwareVersions
            .Include(v => v.RepositoryNavigation)
            .Include(v => v.Artifacts)
            .Include(v => v.ReleaseNotes)
            .FirstOrDefaultAsync(v => v.Version == firmwareVersion, ct);

        if (version is null)
        {
            return Problem(FirmwareError.FirmwareVersionNotFound);
        }

        var boardIds = version.Artifacts.Select(a => a.BoardId).Distinct().ToList();
        var boards = await _db.FirmwareBoards
            .Include(b => b.ChipNavigation)
            .Where(b => boardIds.Contains(b.Id))
            .ToListAsync(ct);

        var cdnBase = _apiConfig.Firmware.CdnBaseUrl.TrimEnd('/');
        return Ok(FirmwareResponseMapper.ToReleaseDto(version, boards, cdnBase));
    }

    /// <summary>
    /// Gets a published firmware version's artifacts for a single board.
    /// </summary>
    /// <param name="firmwareVersion">Published firmware version, e.g. <c>1.4.0</c>.</param>
    /// <param name="board">Board name (e.g. <c>"Wemos-D1-Mini-ESP32"</c>) or board id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The board's artifacts for the version.</response>
    /// <response code="404">The version has not been published, or the board is unknown or has no artifacts for it.</response>
    [HttpGet("{firmwareVersion}/{board}")]
    [CacheControl(86400, immutable: true)]
    [ProducesResponseType<FirmwareBoardReleaseResponseDto>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)] // FirmwareVersionNotFound, FirmwareBoardNotFound
    public async Task<IActionResult> GetVersionForBoard(
        [FromRoute] string firmwareVersion,
        [FromRoute] string board,
        CancellationToken ct)
    {
        var exists = await _db.FirmwareVersions.AnyAsync(v => v.Version == firmwareVersion, ct);
        if (!exists)
        {
            return Problem(FirmwareError.FirmwareVersionNotFound);
        }

        var resolved = await _db.ResolveBoardAsync(board, ct);
        if (resolved is not { } boardRef)
        {
            return Problem(FirmwareError.FirmwareBoardNotFound);
        }

        var artifacts = await _db.FirmwareArtifacts
            .Where(a => a.Version == firmwareVersion && a.BoardId == boardRef.Id)
            .ToListAsync(ct);

        if (artifacts.Count == 0)
        {
            return Problem(FirmwareError.FirmwareBoardNotFound);
        }

        var cdnBase = _apiConfig.Firmware.CdnBaseUrl.TrimEnd('/');
        return Ok(new FirmwareBoardReleaseResponseDto
        {
            Version = firmwareVersion,
            BoardId = boardRef.Name,
            Artifacts = artifacts
                .Select(a => FirmwareResponseMapper.ToArtifactDto(a, firmwareVersion, cdnBase))
                .ToList()
        });
    }
}
