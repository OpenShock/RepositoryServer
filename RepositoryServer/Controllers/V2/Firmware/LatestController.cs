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

namespace OpenShock.RepositoryServer.Controllers.V2.Firmware;

/// <summary>
/// The newest firmware release on a release channel, overall or for one board.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("/{version:apiVersion}/firmware/latest")]
[Consumes(MediaTypeNames.Application.Json)]
public sealed class LatestController : OpenShockControllerBase
{
    private readonly RepoServerContext _db;
    private readonly ApiConfig _apiConfig;

    public LatestController(RepoServerContext db, ApiConfig apiConfig)
    {
        _db = db;
        _apiConfig = apiConfig;
    }

    /// <summary>
    /// Gets the latest firmware release visible to a channel.
    /// </summary>
    /// <param name="channel">Release channel, e.g. <c>stable</c> or <c>beta</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The latest release for the channel.</response>
    /// <response code="400">The channel is not a known release channel.</response>
    /// <response code="404">No version has been published to this channel.</response>
    [HttpGet("{channel}")]
    [CacheControl(300)]
    [ProducesResponseType<FirmwareReleaseDto>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)] // FirmwareInvalidChannel
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)] // FirmwareVersionNotFound
    public async Task<IActionResult> GetLatest([FromRoute] string channel, CancellationToken ct)
    {
        if (!Enum.TryParse<ReleaseChannel>(channel, true, out var firmwareChannel))
        {
            return Problem(FirmwareError.FirmwareInvalidChannel);
        }

        var visibleChannels = ReleaseChannels.VisibleTo(firmwareChannel);
        var latest = await _db.FirmwareVersions
            .Where(v => visibleChannels.Contains(v.Channel))
            .OrderByNewest()
            .Include(v => v.RepositoryNavigation)
            .Include(v => v.Artifacts)
            .Include(v => v.ReleaseNotes)
            .FirstOrDefaultAsync(ct);

        if (latest is null)
        {
            return Problem(FirmwareError.FirmwareVersionNotFound);
        }

        var boardIds = latest.Artifacts.Select(a => a.BoardId).Distinct().ToList();
        var boards = await _db.FirmwareBoards
            .Include(b => b.ChipNavigation)
            .Where(b => boardIds.Contains(b.Id))
            .ToListAsync(ct);

        var cdnBase = _apiConfig.Firmware.CdnBaseUrl.TrimEnd('/');
        return Ok(FirmwareResponseMapper.ToReleaseDto(latest, boards, cdnBase));
    }

    /// <summary>
    /// Gets the latest firmware release for a single board.
    /// </summary>
    /// <param name="channel">Release channel, e.g. <c>stable</c> or <c>beta</c>.</param>
    /// <param name="board">Board name (e.g. <c>"Wemos-D1-Mini-ESP32"</c>) or board id.</param>
    /// <param name="version">Version the caller already has. Answers 204 when it is already the latest.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The latest release for the board.</response>
    /// <response code="204">The caller is already on the latest version.</response>
    /// <response code="400">The channel is not a known release channel.</response>
    /// <response code="404">The board is unknown, has no artifacts, or nothing has been published to the channel.</response>
    [HttpGet("{channel}/{board}")]
    [CacheControl(300)]
    [ProducesResponseType<FirmwareBoardReleaseResponseDto>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)] // FirmwareInvalidChannel
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)] // FirmwareBoardNotFound, FirmwareVersionNotFound
    public async Task<IActionResult> GetLatestForBoard(
        [FromRoute] string channel,
        [FromRoute] string board,
        [FromQuery] string? version,
        CancellationToken ct)
    {
        if (!Enum.TryParse<ReleaseChannel>(channel, true, out var firmwareChannel))
        {
            return Problem(FirmwareError.FirmwareInvalidChannel);
        }

        // Resolved before the up-to-date check so an unknown board always reports 404 rather than
        // being masked as "no update needed".
        var resolved = await _db.ResolveBoardAsync(board, ct);
        if (resolved is not { } boardRef)
        {
            return Problem(FirmwareError.FirmwareBoardNotFound);
        }

        var visibleChannels = ReleaseChannels.VisibleTo(firmwareChannel);
        var latestVersion = await _db.FirmwareVersions
            .Where(v => visibleChannels.Contains(v.Channel))
            .OrderByNewest()
            .Select(v => v.Version)
            .FirstOrDefaultAsync(ct);

        if (latestVersion is null)
        {
            return Problem(FirmwareError.FirmwareVersionNotFound);
        }

        // String equality (not semver) is intentional: rollbacks — if a version is pulled
        // and an older version becomes "latest", the hub's string compare will still differ
        // and trigger an update.
        if (!string.IsNullOrWhiteSpace(version) && string.Equals(version, latestVersion, StringComparison.Ordinal))
        {
            return NoContent();
        }

        var artifacts = await _db.FirmwareArtifacts
            .Where(a => a.Version == latestVersion && a.BoardId == boardRef.Id)
            .ToListAsync(ct);

        if (artifacts.Count == 0)
        {
            return Problem(FirmwareError.FirmwareBoardNotFound);
        }

        var cdnBase = _apiConfig.Firmware.CdnBaseUrl.TrimEnd('/');
        return Ok(new FirmwareBoardReleaseResponseDto
        {
            Version = latestVersion,
            BoardId = boardRef.Name,
            Artifacts = artifacts
                .Select(a => FirmwareResponseMapper.ToArtifactDto(a, latestVersion, cdnBase))
                .ToList()
        });
    }
}
