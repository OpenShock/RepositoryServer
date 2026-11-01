using Microsoft.EntityFrameworkCore;
using OneOf;
using OneOf.Types;
using OpenShock.RepositoryServer.Enums;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.RepoServerDb.Models;
using OpenShock.RepositoryServer.Utils;

namespace OpenShock.RepositoryServer.Services.Admin;

/// <summary>
/// Janitorial work on firmware releases: repairing changelogs that failed to parse, and removing
/// published versions.
/// </summary>
public sealed class ReleaseAdminService
{
    private readonly RepoServerContext _db;

    public ReleaseAdminService(RepoServerContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Releases that CI created with an unparseable changelog and cannot publish until a human fixes
    /// the notes.
    /// </summary>
    public Task<FirmwareRelease[]> ListEditableAsync(CancellationToken ct = default) =>
        _db.FirmwareReleases
            .Include(r => r.RepositoryNavigation)
            .Where(r => r.Status == ReleaseStatus.Editing || r.Status == ReleaseStatus.Staging)
            .OrderByDescending(r => r.CreatedAt)
            .ToArrayAsync(ct);

    public Task<FirmwareRelease?> FindAsync(Guid releaseId, CancellationToken ct = default) =>
        _db.FirmwareReleases.FirstOrDefaultAsync(r => r.Id == releaseId, ct);

    public Task<FirmwareStagedReleaseNote[]> ListStagedNotesAsync(
        Guid releaseId, CancellationToken ct = default) =>
        _db.FirmwareStagedReleaseNotes
            .Where(n => n.ReleaseId == releaseId)
            .OrderBy(n => n.Index)
            .ToArrayAsync(ct);

    /// <summary>
    /// Replaces a release's staged notes with a freshly parsed changelog and returns it to
    /// <c>staging</c>, which is the only status publish accepts.
    /// </summary>
    public async Task<OneOf<Success, NotFound, NotEditable, InvalidChangelog>> FixChangelogAsync(
        Guid releaseId, string changelog, CancellationToken ct = default)
    {
        var release = await _db.FirmwareReleases.FirstOrDefaultAsync(r => r.Id == releaseId, ct);
        if (release is null)
        {
            return new NotFound();
        }

        if (release.Status != ReleaseStatus.Staging && release.Status != ReleaseStatus.Editing)
        {
            return new NotEditable(release.Status);
        }

        var parseResult = ChangelogParser.Parse(changelog);
        if (!parseResult.TryPickT0(out var notes, out var error))
        {
            return new InvalidChangelog(error);
        }

        await _db.FirmwareStagedReleaseNotes
            .Where(n => n.ReleaseId == releaseId)
            .ExecuteDeleteAsync(ct);

        for (var i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            _db.FirmwareStagedReleaseNotes.Add(new FirmwareStagedReleaseNote
            {
                ReleaseId = releaseId,
                Index = i,
                SectionType = Enum.Parse<ReleaseNoteSectionType>(note.Type, true),
                Title = note.Title,
                Content = note.Content
            });
        }

        release.Status = ReleaseStatus.Staging;
        await _db.SaveChangesAsync(ct);

        return new Success();
    }

    public Task<FirmwareVersion[]> ListVersionsAsync(CancellationToken ct = default) =>
        _db.FirmwareVersions
            .Include(v => v.RepositoryNavigation)
            .OrderByDescending(v => v.ReleaseDate)
            .ThenByDescending(v => v.Version)
            .ToArrayAsync(ct);

    /// <summary>
    /// Removes a published version. Artifacts in storage are left in place: they are immutable, and a
    /// version is usually pulled because it is bad rather than because the bytes must disappear.
    /// </summary>
    public async Task<OneOf<Success, NotFound>> DeleteVersionAsync(string firmwareVersion, CancellationToken ct = default)
    {
        var deleted = await _db.FirmwareVersions
            .Where(v => v.Version == firmwareVersion)
            .ExecuteDeleteAsync(ct);

        if (deleted <= 0)
        {
            return new NotFound();
        }

        return new Success();
    }
}
