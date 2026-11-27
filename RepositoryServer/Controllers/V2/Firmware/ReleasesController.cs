using FlexLabs.EntityFrameworkCore.Upsert;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OneOf;
using OpenShock.Internal.Common;
using OpenShock.Internal.Common.Problems;
using OpenShock.RepositoryServer.ApiVersioning;
using OpenShock.RepositoryServer.Config;
using OpenShock.RepositoryServer.Enums;
using OpenShock.RepositoryServer.Models.Firmware;
using OpenShock.RepositoryServer.Errors;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.Services;
using OpenShock.RepositoryServer.Utils;
using Semver;
using System.Net.Mime;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using OpenShock.RepositoryServer.RepoServerDb.Models;

namespace OpenShock.RepositoryServer.Controllers.V2.Firmware;

[ApiVersion(2)]
[ApiController]
[Route("/{version:apiVersion}/firmware/releases")]
[Consumes(MediaTypeNames.Application.Json)]
// See CiCdController: publishing is a CI-only surface and is kept out of the API reference.
[ApiExplorerSettings(IgnoreApi = true)]
[Authorize(AuthenticationSchemes = AuthSchemas.CiCdToken, Policy = AuthSchemas.Policies.PublishFirmware)]
public class ReleasesController : OpenShockControllerBase
{
    private static readonly Dictionary<string, FirmwareArtifactType> ArtifactFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["app"] = FirmwareArtifactType.App,
        ["staticfs"] = FirmwareArtifactType.StaticFs,
        ["merged"] = FirmwareArtifactType.Merged,
        ["bootloader"] = FirmwareArtifactType.Bootloader,
        ["partitions"] = FirmwareArtifactType.Partitions,
    };

    private readonly RepoServerContext _db;
    private readonly IStorageService _storage;
    private readonly ApiConfig _apiConfig;
    private readonly IDiscordNotificationService _discord;
    private readonly TimeProvider _timeProvider;
    private readonly PublishedArtifactVerifier _artifactVerifier;
    private readonly ILogger<ReleasesController> _logger;

    public ReleasesController(
        RepoServerContext db,
        [FromKeyedServices(StorageKeys.Firmware)] IStorageService storage,
        ApiConfig apiConfig,
        IDiscordNotificationService discord,
        TimeProvider timeProvider,
        PublishedArtifactVerifier artifactVerifier,
        ILogger<ReleasesController> logger)
    {
        _db = db;
        _storage = storage;
        _apiConfig = apiConfig;
        _discord = discord;
        _timeProvider = timeProvider;
        _artifactVerifier = artifactVerifier;
        _logger = logger;
    }

    // ---- Init Release ----

    /// <summary>
    /// Opens a staging release for a version, declaring the boards it will carry.
    /// </summary>
    /// <param name="request">Version, channel, declared boards and changelog.</param>
    /// <param name="nofailQuery">Accept an unparseable changelog and open the release in <c>editing</c> instead of failing.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">The release was opened.</response>
    /// <response code="400">Invalid semver, channel, board list or changelog.</response>
    /// <response code="404">One or more declared boards are unknown.</response>
    /// <response code="409">The version is already published, or another release for it is open.</response>
    [HttpPost]
    [ProducesResponseType<InitReleaseResponse>(StatusCodes.Status201Created, MediaTypeNames.Application.Json)]
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)] // FirmwareInvalidSemver, FirmwareInvalidChannel, FirmwareReleaseBoardsEmpty, FirmwareInvalidChangelog
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)] // FirmwareBoardsNotFound
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status409Conflict, MediaTypeNames.Application.ProblemJson)] // FirmwareVersionAlreadyPublished, FirmwareReleaseAlreadyStaging
    public async Task<IActionResult> InitRelease(
        [FromBody] InitReleaseRequest request,
        [FromQuery(Name = "nofail")] bool? nofailQuery,
        CancellationToken ct)
    {
        var nofail = nofailQuery == true || Request.Query.ContainsKey("nofail");

        if (!SemVersion.TryParse(request.Version, SemVersionStyles.Strict, out _))
        {
            return Problem(FirmwareError.FirmwareInvalidSemver);
        }

        if (!Enum.TryParse<ReleaseChannel>(request.Channel, true, out var channel))
        {
            return Problem(FirmwareError.FirmwareInvalidChannel);
        }

        if (request.Boards.Count == 0)
        {
            return Problem(FirmwareError.FirmwareReleaseBoardsEmpty);
        }

        // Published versions are immutable (spec §4.4). Without this, re-initialising an existing
        // version would let a second release overwrite live artifacts at the same storage keys before
        // any publish call, and an abandoned one would later have them deleted by the TTL job.
        if (await _db.FirmwareVersions.AnyAsync(v => v.Version == request.Version, ct))
        {
            return Problem(FirmwareError.FirmwareVersionAlreadyPublished);
        }

        var existingStaging = await _db.FirmwareReleases
            .AnyAsync(r => r.Version == request.Version &&
                           (r.Status == ReleaseStatus.Staging || r.Status == ReleaseStatus.Editing),
                ct);
        if (existingStaging)
        {
            return Problem(FirmwareError.FirmwareReleaseAlreadyStaging);
        }

        var (declaredBoards, unknownBoards) = await _db.ResolveBoardsAsync(request.Boards, ct);
        if (unknownBoards.Count > 0)
        {
            return Problem(FirmwareError.FirmwareBoardsNotFound(unknownBoards));
        }

        // Source traceability — from OIDC claims (attached by GitHubOidcAuthentication).
        if (!TryReadSourceClaims(out var sourceClaims, out var missing))
        {
            return Problem(FirmwareError.FirmwareInvalidChangelog($"Missing OIDC source claims: {missing}"));
        }

        // Parse changelog.
        var parseResult = ChangelogParser.Parse(request.Changelog);
        ReleaseStatus status;
        IReadOnlyList<FirmwareReleaseNoteDto> notes;

        if (parseResult.TryPickT0(out var parsedNotes, out var error))
        {
            status = ReleaseStatus.Staging;
            notes = parsedNotes;
        }
        else if (nofail)
        {
            status = ReleaseStatus.Editing;
            notes = Array.Empty<FirmwareReleaseNoteDto>();
        }
        else
        {
            return Problem(FirmwareError.FirmwareInvalidChangelog(DescribeParseError(error)));
        }

        var release = new FirmwareRelease
        {
            Id = Guid.NewGuid(),
            Version = request.Version,
            Channel = channel,
            RepositoryId = sourceClaims.RepositoryId,
            CommitHash = sourceClaims.CommitHash,
            Ref = sourceClaims.Ref,
            RunId = sourceClaims.RunId,
            // Npgsql refuses a DateTimeOffset with a non-zero offset for `timestamp with time zone`,
            // which would surface as a generic 500. CI runners in a non-UTC zone emit exactly that.
            ReleaseDate = request.ReleaseDate.ToUniversalTime(),
            Status = status,
            DeclaredBoards = declaredBoards.Select(b => b.Id).ToArray(),
            CreatedAt = _timeProvider.GetUtcNow(),
        };

        _db.FirmwareReleases.Add(release);

        for (var i = 0; i < notes.Count; i++)
        {
            var n = notes[i];
            _db.FirmwareStagedReleaseNotes.Add(new FirmwareStagedReleaseNote
            {
                ReleaseId = release.Id,
                Index = i,
                SectionType = Enum.Parse<ReleaseNoteSectionType>(n.Type, true),
                Title = n.Title,
                Content = n.Content,
            });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsOpenReleaseConflict(ex))
        {
            // Lost the race against a concurrent job for the same tag. The check above catches this in
            // the common case; ix_firmware_releases_open_version closes the read-then-insert window,
            // and both paths surface as the same 409.
            return Problem(FirmwareError.FirmwareReleaseAlreadyStaging);
        }

        if (status == ReleaseStatus.Editing)
        {
            await _discord.NotifyReleaseNotesNeedEditingAsync(release.Id, release.Version, channel.ToString().ToLowerInvariant(), ct);
        }

        return Created((string?)null, new InitReleaseResponse
        {
            Id = release.Id,
            Status = status.ToString().ToLowerInvariant()
        });
    }

    // ---- Upload Board Artifacts ----

    /// <summary>
    /// Uploads one board's artifacts into an open release, verifying them against a SHA-256 manifest.
    /// </summary>
    /// <param name="releaseId">Release the artifacts belong to.</param>
    /// <param name="board">Board name (e.g. <c>"Wemos-D1-Mini-ESP32"</c>) or board UUID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The artifacts that were staged.</response>
    /// <response code="400">Manifest keys, artifact types or hashes do not match the uploaded files.</response>
    /// <response code="403">The release belongs to a different repository.</response>
    /// <response code="404">The release or board does not exist.</response>
    /// <response code="409">The release is no longer accepting uploads.</response>
    [HttpPut("{releaseId:guid}/boards/{board}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    [ProducesResponseType<List<FirmwareArtifactDto>>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)] // FirmwareManifestKeysMismatch, FirmwareInvalidArtifactType, FirmwareMissingRequiredArtifacts, FirmwareSha256Mismatch, FirmwareBoardNotDeclared
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status403Forbidden, MediaTypeNames.Application.ProblemJson)] // FirmwareReleaseNotOwned
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)] // FirmwareReleaseNotFound, FirmwareBoardNotFound
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status409Conflict, MediaTypeNames.Application.ProblemJson)] // FirmwareReleaseNotEditable
    public async Task<IActionResult> UploadBoardArtifacts(
        [FromRoute] Guid releaseId,
        [FromRoute] string board,
        CancellationToken ct)
    {
        var release = await _db.FirmwareReleases.FirstOrDefaultAsync(r => r.Id == releaseId, ct);
        if (release is null)
        {
            return Problem(FirmwareError.FirmwareReleaseNotFound);
        }

        if (!IsOwnedByCaller(release))
        {
            return Problem(FirmwareError.FirmwareReleaseNotOwned);
        }

        if (release.Status != ReleaseStatus.Staging && release.Status != ReleaseStatus.Editing)
        {
            return Problem(FirmwareError.FirmwareReleaseNotEditable);
        }

        var resolved = await _db.ResolveBoardAsync(board, ct);
        if (resolved is not { } boardRef)
        {
            return Problem(FirmwareError.FirmwareBoardNotFound);
        }

        if (!release.DeclaredBoards.Contains(boardRef.Id))
        {
            return Problem(FirmwareError.FirmwareBoardNotDeclared);
        }

        var boardId = boardRef.Id;
        var boardEntity = await _db.FirmwareBoards.FirstAsync(b => b.Id == boardId, ct);

        var files = Request.Form.Files;
        if (files.Count == 0)
        {
            return Problem(FirmwareError.FirmwareManifestKeysMismatch("No artifact files uploaded"));
        }

        var sha256FormValue = Request.Form["sha256"].ToString();
        if (string.IsNullOrWhiteSpace(sha256FormValue))
        {
            return Problem(FirmwareError.FirmwareManifestKeysMismatch("Missing required 'sha256' form field"));
        }

        Dictionary<string, string>? expectedHashes;
        try
        {
            expectedHashes = JsonSerializer.Deserialize<Dictionary<string, string>>(
                sha256FormValue,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return Problem(FirmwareError.FirmwareManifestKeysMismatch("'sha256' form field is not valid JSON"));
        }

        if (expectedHashes is null)
        {
            return Problem(FirmwareError.FirmwareManifestKeysMismatch("'sha256' form field is null"));
        }

        // Validate file field names and build an uploaded-type map.
        var uploadedByType = new Dictionary<FirmwareArtifactType, IFormFile>();
        foreach (var file in files)
        {
            if (!ArtifactFieldNames.TryGetValue(file.Name, out var artifactType))
            {
                return Problem(FirmwareError.FirmwareInvalidArtifactType);
            }
            uploadedByType[artifactType] = file;
        }

        // Normalize expected manifest keys to enum values.
        var normalizedExpected = new Dictionary<FirmwareArtifactType, string>();
        foreach (var kv in expectedHashes)
        {
            if (!ArtifactFieldNames.TryGetValue(kv.Key, out var artifactType))
            {
                return Problem(FirmwareError.FirmwareInvalidArtifactType);
            }
            normalizedExpected[artifactType] = kv.Value;
        }

        // Keys must match exactly (spec §5.2).
        var onlyInFiles = uploadedByType.Keys.Except(normalizedExpected.Keys).ToList();
        var onlyInManifest = normalizedExpected.Keys.Except(uploadedByType.Keys).ToList();
        if (onlyInFiles.Count > 0 || onlyInManifest.Count > 0)
        {
            var detail = $"Uploaded-only: [{string.Join(", ", onlyInFiles)}]; manifest-only: [{string.Join(", ", onlyInManifest)}]";
            return Problem(FirmwareError.FirmwareManifestKeysMismatch(detail));
        }

        // Validate required artifact types per board config.
        if (boardEntity.RequiredArtifactTypes.Length > 0)
        {
            var missingRequired = boardEntity.RequiredArtifactTypes
                .Where(r => !uploadedByType.ContainsKey(r))
                .Select(r => r.ToString().ToLowerInvariant())
                .ToList();
            if (missingRequired.Count > 0)
            {
                return Problem(FirmwareError.FirmwareMissingRequiredArtifacts(boardRef.Name, missingRequired));
            }
        }

        // Read and verify every artifact BEFORE touching storage or the database.
        //
        // The previous order — delete prior staged rows, then hash-and-upload each file in turn,
        // then report mismatches — meant one good file alongside one bad one left the good blob on the
        // CDN with no row referencing it. Neither abort nor the TTL job could ever find it, because
        // both enumerate staged rows. It also destroyed the board's previously valid staged artifacts
        // on the way to failing.
        var verified = new List<(FirmwareArtifactType Type, byte[] Bytes, string Hash)>();
        var mismatches = new List<string>();

        foreach (var (artifactType, file) in uploadedByType)
        {
            await using var memory = new MemoryStream();
            await file.CopyToAsync(memory, ct);
            var bytes = memory.ToArray();
            var actual = Convert.ToHexString(SHA256.HashData(bytes));

            var expected = normalizedExpected[artifactType];
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add($"{artifactType.ToString().ToLowerInvariant()} (expected={expected}, actual={actual})");
                continue;
            }

            verified.Add((artifactType, bytes, actual));
        }

        if (mismatches.Count > 0)
        {
            return Problem(FirmwareError.FirmwareSha256Mismatch(string.Join("; ", mismatches)));
        }

        var cdnBase = _apiConfig.Firmware.CdnBaseUrl.TrimEnd('/');
        var uploadedArtifacts = new List<FirmwareArtifactDto>();

        await using var uploadTransaction = await _db.Database.BeginTransactionAsync(ct);

        // Replacing this board's staged rows. Uploads overwrite at the same deterministic staging
        // keys, so a re-upload of the same board never leaves a stale blob behind.
        await _db.FirmwareStagedArtifacts
            .Where(a => a.ReleaseId == releaseId && a.BoardId == boardId)
            .ExecuteDeleteAsync(ct);

        foreach (var (artifactType, bytes, hash) in verified)
        {
            // Staged, not published: the public key is written only by PublishRelease.
            var stagingPath = FirmwareArtifactFileNames.BuildStagingPath(releaseId, boardId, artifactType);

            await using var uploadStream = new MemoryStream(bytes);
            await _storage.UploadFileAsync(stagingPath, uploadStream, ct);

            _db.FirmwareStagedArtifacts.Add(new FirmwareStagedArtifact
            {
                ReleaseId = releaseId,
                BoardId = boardId,
                ArtifactType = artifactType,
                HashSha256 = Convert.FromHexString(hash),
                FileSize = bytes.Length,
            });

            uploadedArtifacts.Add(new FirmwareArtifactDto
            {
                Type = artifactType.ToString().ToLowerInvariant(),
                Url = FirmwareArtifactFileNames.BuildUrl(cdnBase, release.Version, boardId, artifactType),
                Sha256Hash = hash,
                FileSize = bytes.Length,
            });
        }

        await _db.SaveChangesAsync(ct);
        await uploadTransaction.CommitAsync(ct);
        return Ok(uploadedArtifacts);
    }

    // ---- Publish Release ----

    /// <summary>
    /// Promotes a complete staging release to a published version.
    /// </summary>
    /// <param name="releaseId">Release to publish.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">The version was published.</response>
    /// <response code="400">A declared board is missing artifacts the board requires.</response>
    /// <response code="403">The release belongs to a different repository.</response>
    /// <response code="404">The release does not exist.</response>
    /// <response code="409">The release is not in staging, or its notes are not finalized.</response>
    [HttpPost("{releaseId:guid}/publish")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)] // FirmwareReleaseIncomplete, FirmwareMissingRequiredArtifacts
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status403Forbidden, MediaTypeNames.Application.ProblemJson)] // FirmwareReleaseNotOwned
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)] // FirmwareReleaseNotFound
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status409Conflict, MediaTypeNames.Application.ProblemJson)] // FirmwareReleaseNotesNotFinalized, FirmwareReleaseNotStaging
    public async Task<IActionResult> PublishRelease([FromRoute] Guid releaseId, CancellationToken ct)
    {
        var release = await _db.FirmwareReleases
            .Include(r => r.StagedArtifacts)
            .Include(r => r.StagedReleaseNotes.OrderBy(n => n.Index))
            .FirstOrDefaultAsync(r => r.Id == releaseId, ct);

        if (release is null)
        {
            return Problem(FirmwareError.FirmwareReleaseNotFound);
        }

        if (!IsOwnedByCaller(release))
        {
            return Problem(FirmwareError.FirmwareReleaseNotOwned);
        }

        if (release.Status == ReleaseStatus.Editing)
        {
            return Problem(FirmwareError.FirmwareReleaseNotesNotFinalized);
        }

        if (release.Status != ReleaseStatus.Staging)
        {
            return Problem(FirmwareError.FirmwareReleaseNotStaging);
        }

        var boards = await _db.FirmwareBoards
            .Where(b => release.DeclaredBoards.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, ct);

        var uploadedBoardIds = release.StagedArtifacts.Select(a => a.BoardId).Distinct().ToHashSet();
        var missingBoards = release.DeclaredBoards
            .Where(b => !uploadedBoardIds.Contains(b))
            .Select(b => boards.TryGetValue(b, out var board) ? board.Name : b.ToString())
            .ToList();
        if (missingBoards.Count > 0)
        {
            return Problem(FirmwareError.FirmwareReleaseIncomplete(missingBoards));
        }

        foreach (var boardId in release.DeclaredBoards)
        {
            if (!boards.TryGetValue(boardId, out var board) || board.RequiredArtifactTypes.Length == 0)
                continue;

            var stagedTypes = release.StagedArtifacts
                .Where(a => a.BoardId == boardId)
                .Select(a => a.ArtifactType)
                .ToHashSet();
            var missingTypes = board.RequiredArtifactTypes
                .Where(r => !stagedTypes.Contains(r))
                .Select(r => r.ToString().ToLowerInvariant())
                .ToList();
            if (missingTypes.Count > 0)
            {
                return Problem(FirmwareError.FirmwareMissingRequiredArtifacts(board.Name, missingTypes));
            }
        }

        // Promote staged objects to their published keys before touching the database.
        //
        // Storage is not transactional, so one of the two orderings has to carry the risk. Copying
        // first means a later database failure leaves unreferenced objects at published keys, which
        // are invisible (no version row points at them) and are overwritten by a subsequent publish
        // of the same version. Committing first would instead publish a version whose artifacts are
        // not all present yet — hubs would download 404s. Unreferenced bytes beat a broken release.
        var promoted = new List<string>();
        try
        {
            foreach (var staged in release.StagedArtifacts)
            {
                var stagingPath = FirmwareArtifactFileNames.BuildStagingPath(
                    release.Id, staged.BoardId, staged.ArtifactType);
                var publishedPath = FirmwareArtifactFileNames.BuildStoragePath(
                    release.Version, staged.BoardId, staged.ArtifactType);

                await _storage.CopyFileAsync(stagingPath, publishedPath, ct);
                promoted.Add(publishedPath);
            }
        }
        catch (Exception ex)
        {
            // Roll back the partial promotion so a half-populated version directory is not left
            // behind. Best effort: if this fails too, the objects stay unreferenced and harmless.
            _logger.LogError(ex, "Failed to promote staged artifacts for release {ReleaseId}", release.Id);
            await TryDeleteAllAsync(promoted, ct);
            throw;
        }

        // The copy above succeeded, which only says the storage backend accepted it. Whether those
        // bytes are reachable at the URL this server is about to advertise is a separate fact, and
        // one nothing here has ever established: uploads go through IStorageService, URLs come from
        // Firmware:CdnBaseUrl, and an instance where those two do not refer to the same place
        // publishes a version whose every artifact 404s. That release looks entirely healthy through
        // the API - correct hashes, correct sizes - and the client that discovers otherwise is a hub
        // partway through an OTA update.
        //
        // Checked before the transaction commits, so a release that cannot be downloaded does not
        // become a release. Rolls the promotion back on failure, exactly as a copy error does.
        var unreachable = await _artifactVerifier.FindUnreachableAsync(
            release.Version,
            release.StagedArtifacts.Select(a => (a.BoardId, a.ArtifactType)),
            ct);

        if (unreachable.Count > 0)
        {
            await TryDeleteAllAsync(promoted, ct);

            var detail = string.Join("; ", unreachable.Take(5).Select(u => $"{u.Url} -> {u.Detail}"));
            if (unreachable.Count > 5)
            {
                detail += $" (and {unreachable.Count - 5} more)";
            }

            _logger.LogError(
                "Refusing to publish {Version}: {Count} artifact(s) not retrievable at the configured CDN base URL",
                release.Version, unreachable.Count);

            return Problem(FirmwareError.FirmwareArtifactsNotRetrievable(detail));
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var versionEntity = new FirmwareVersion
        {
            Version = release.Version,
            Channel = release.Channel,
            ReleaseDate = release.ReleaseDate,
            RepositoryId = release.RepositoryId,
            CommitHash = release.CommitHash,
            Ref = release.Ref,
            RunId = release.RunId,
        };
        await _db.FirmwareVersions.Upsert(versionEntity).On(v => v.Version).RunAsync(ct);

        await _db.FirmwareArtifacts.Where(a => a.Version == release.Version).ExecuteDeleteAsync(ct);
        foreach (var staged in release.StagedArtifacts)
        {
            _db.FirmwareArtifacts.Add(new FirmwareArtifact
            {
                Version = release.Version,
                BoardId = staged.BoardId,
                ArtifactType = staged.ArtifactType,
                HashSha256 = staged.HashSha256,
                FileSize = staged.FileSize,
            });
        }

        await _db.FirmwareReleaseNotes.Where(n => n.Version == release.Version).ExecuteDeleteAsync(ct);
        foreach (var staged in release.StagedReleaseNotes)
        {
            _db.FirmwareReleaseNotes.Add(new FirmwareReleaseNote
            {
                Version = release.Version,
                Index = staged.Index,
                SectionType = staged.SectionType,
                Title = staged.Title,
                Content = staged.Content,
            });
        }

        release.Status = ReleaseStatus.Published;

        try
        {
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await TryDeleteAllAsync(promoted, ct);
            throw;
        }

        // The release is live; its staging copies are now dead weight. Deleting them is best effort —
        // failing here would abort a publish that has already succeeded, and the TTL job does not
        // revisit published releases, so the worst case is some orphaned staging objects.
        try
        {
            await _storage.DeleteDirectoryAsync(FirmwareArtifactFileNames.BuildStagingPrefix(release.Id), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Published release {ReleaseId} but failed to clear its staging prefix", release.Id);
        }

        await _discord.NotifyFirmwareReleasePublishedAsync(
            release.Version,
            release.Channel.ToString().ToLowerInvariant(),
            release.CommitHash,
            ct);

        return CreatedAtAction(
            nameof(VersionsController.GetVersion),
            "Versions",
            new { firmwareVersion = release.Version },
            null);
    }

    // ---- Abort Release ----

    /// <summary>
    /// Abandons an open release and deletes everything it staged.
    /// </summary>
    /// <param name="releaseId">Release to abort.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">The release was aborted.</response>
    /// <response code="403">The release belongs to a different repository.</response>
    /// <response code="404">The release does not exist.</response>
    /// <response code="409">The release is no longer open.</response>
    [HttpDelete("{releaseId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status403Forbidden, MediaTypeNames.Application.ProblemJson)] // FirmwareReleaseNotOwned
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)] // FirmwareReleaseNotFound
    [ProducesResponseType<OpenShockProblem>(StatusCodes.Status409Conflict, MediaTypeNames.Application.ProblemJson)] // FirmwareReleaseNotEditable
    public async Task<IActionResult> AbortRelease([FromRoute] Guid releaseId, CancellationToken ct)
    {
        var release = await _db.FirmwareReleases
            .Include(r => r.StagedArtifacts)
            .FirstOrDefaultAsync(r => r.Id == releaseId, ct);

        if (release is null)
        {
            return Problem(FirmwareError.FirmwareReleaseNotFound);
        }

        if (!IsOwnedByCaller(release))
        {
            return Problem(FirmwareError.FirmwareReleaseNotOwned);
        }

        if (release.Status != ReleaseStatus.Staging && release.Status != ReleaseStatus.Editing)
        {
            return Problem(FirmwareError.FirmwareReleaseNotEditable);
        }

        // Everything this release wrote lives under its own staging prefix, so it can be dropped
        // wholesale with no risk of deleting an object a published version is serving.
        await _storage.DeleteDirectoryAsync(FirmwareArtifactFileNames.BuildStagingPrefix(release.Id), ct);

        release.Status = ReleaseStatus.Aborted;
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    // ---- Helpers ----

    private readonly record struct SourceClaims(Guid RepositoryId, string CommitHash, string? Ref, string? RunId);

    /// <summary>
    /// Best-effort deletion used to unwind a partial promotion. Never throws: it runs on paths that
    /// are already failing, and turning a cleanup error into the reported fault would hide the cause.
    /// </summary>
    private async Task TryDeleteAllAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        foreach (var path in paths)
        {
            try
            {
                await _storage.DeleteFileAsync(path, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove promoted artifact {Path} while unwinding", path);
            }
        }
    }

    /// <summary>
    /// True when the failure is the partial unique index guarding one open release per version.
    /// </summary>
    private static bool IsOpenReleaseConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && pg.ConstraintName == "ix_firmware_releases_open_version";

    /// <summary>
    /// Confirms the authenticated repository is the one that created this release.
    /// </summary>
    /// <remarks>
    /// Every registered repository presents an equally valid CI/CD principal, so authentication alone
    /// says nothing about <em>which</em> release the caller may touch. Without this check any
    /// authorized repository could inject binaries into another's in-flight release, publish it under
    /// that repository's identity and commit hash, or abort it.
    /// </remarks>
    private bool IsOwnedByCaller(FirmwareRelease release)
    {
        var rawRepoId = User.FindFirstValue(AuthSchemas.CiCdClaims.RepositoryId);
        return Guid.TryParse(rawRepoId, out var callerRepositoryId)
               && release.RepositoryId == callerRepositoryId;
    }

    private bool TryReadSourceClaims(out SourceClaims claims, out string missing)
    {
        var rawRepoId = User.FindFirstValue(AuthSchemas.CiCdClaims.RepositoryId);
        var commitHash = User.FindFirstValue(AuthSchemas.CiCdClaims.CommitHash);
        var refValue = User.FindFirstValue(AuthSchemas.CiCdClaims.Ref);
        var runId = User.FindFirstValue(AuthSchemas.CiCdClaims.RunId);

        if (string.IsNullOrWhiteSpace(rawRepoId) || !Guid.TryParse(rawRepoId, out var repoId))
        {
            claims = default;
            missing = AuthSchemas.CiCdClaims.RepositoryId;
            return false;
        }
        if (string.IsNullOrWhiteSpace(commitHash))
        {
            claims = default;
            missing = AuthSchemas.CiCdClaims.CommitHash;
            return false;
        }

        claims = new SourceClaims(repoId, commitHash, refValue, runId);
        missing = string.Empty;
        return true;
    }

    private static string DescribeParseError(ChangelogParseError error) => error switch
    {
        ChangelogParseError.Empty => "Changelog is empty or whitespace-only",
        ChangelogParseError.NoHeadings => "Changelog contains no '### Heading' sections",
        ChangelogParseError.AllSectionsEmpty => "All changelog sections are empty",
        _ => error.ToString()
    };
}
