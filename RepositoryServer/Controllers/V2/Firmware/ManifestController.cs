using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenShock.Internal.Common;
using OpenShock.RepositoryServer.ApiVersioning;
using OpenShock.RepositoryServer.Enums;
using OpenShock.RepositoryServer.Models.Firmware;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.Utils;
using System.Net.Mime;

namespace OpenShock.RepositoryServer.Controllers.V2.Firmware;

/// <summary>
/// The whole firmware catalog in one response, for flashing tools that want everything up front.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("/{version:apiVersion}/firmware/manifest")]
[Consumes(MediaTypeNames.Application.Json)]
public sealed class ManifestController : OpenShockControllerBase
{
    private static readonly ReleaseChannel[] AllChannels =
    [
        ReleaseChannel.Stable,
        ReleaseChannel.Beta,
        ReleaseChannel.Develop
    ];

    private readonly RepoServerContext _db;

    public ManifestController(RepoServerContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Gets the firmware catalog: latest version per channel, boards, chips, USB identifiers and advisories.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The manifest.</response>
    [HttpGet]
    [CacheControl(300)]
    [ProducesResponseType<FirmwareManifestResponse>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
    public async Task<IActionResult> GetManifest(CancellationToken ct)
    {
        // DbContext is not thread-safe; queries run sequentially. The manifest is cached
        // at the HTTP layer (max-age=300) so serialization here is a non-issue.
        var latest = new Dictionary<string, string>();
        foreach (var ch in AllChannels)
        {
            var visibleChannels = ReleaseChannels.VisibleTo(ch);
            var version = await _db.FirmwareVersions
                .Where(v => visibleChannels.Contains(v.Channel))
                .OrderByNewest()
                .Select(v => v.Version)
                .FirstOrDefaultAsync(ct);

            if (version is not null)
            {
                latest[ch.ToString().ToLowerInvariant()] = version;
            }
        }

        var boardRows = await _db.FirmwareBoards
            .Include(b => b.ChipNavigation)
            .Include(b => b.UsbDevices)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);

        var chipRows = await _db.FirmwareChips
            .Include(c => c.UsbDevices)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        var filterRows = await _db.UsbSerialFilters
            .OrderBy(f => f.Vid).ThenBy(f => f.Pid)
            .Select(f => new FirmwareUsbSerialFilterDto { Vid = f.Vid, Pid = f.Pid })
            .ToListAsync(ct);

        var deviceRows = await _db.UsbDevices
            .OrderBy(d => d.Vid).ThenBy(d => d.Pid)
            .Select(d => new FirmwareUsbDeviceDto
            {
                Id = d.Id,
                Vid = d.Vid,
                Pid = d.Pid,
                Name = d.Name
            })
            .ToListAsync(ct);

        var boards = boardRows
            .Select(b => new FirmwareBoardDto
            {
                Name = b.Name,
                ChipName = b.ChipNavigation.Name,
                Discontinued = b.Discontinued,
                UsbDevices = b.UsbDevices
                    .Select(d => new FirmwareUsbDeviceDto { Id = d.Id, Vid = d.Vid, Pid = d.Pid, Name = d.Name })
                    .ToList()
            })
            .ToList();

        var chips = chipRows
            .Select(c => new FirmwareChipDto
            {
                Name = c.Name,
                Architecture = EnumNaming.FormatArchitecture(c.Architecture),
                UsbDevices = c.UsbDevices
                    .Select(d => new FirmwareUsbDeviceDto { Id = d.Id, Vid = d.Vid, Pid = d.Pid, Name = d.Name })
                    .ToList()
            })
            .ToList();

        // Postgres sorts enum columns by label creation order (alphabetical with MapEnum),
        // not severity rank — order on the CLR enum value in memory instead.
        var advisoryRows = await _db.FirmwareAdvisories.ToListAsync(ct);
        var advisories = advisoryRows
            .OrderBy(a => a.Severity)
            .ThenBy(a => a.Title)
            .Select(a => new FirmwareAdvisoryDto
            {
                Severity = a.Severity.ToString().ToLowerInvariant(),
                Title = a.Title,
                Content = a.Content,
                AffectedVersions = a.AffectedVersions,
                Url = a.Url
            })
            .ToList();

        var response = new FirmwareManifestResponse
        {
            Channels = AllChannels.Select(c => c.ToString().ToLowerInvariant()).ToList(),
            Latest = latest,
            Boards = boards,
            Chips = chips,
            UsbSerialFilters = filterRows,
            UsbDevices = deviceRows,
            Advisories = advisories
        };

        return Ok(response);
    }
}
