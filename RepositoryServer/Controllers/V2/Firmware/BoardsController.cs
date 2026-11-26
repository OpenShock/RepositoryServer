using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenShock.Internal.Common;
using OpenShock.RepositoryServer.ApiVersioning;
using OpenShock.RepositoryServer.Models.Firmware;
using OpenShock.RepositoryServer.RepoServerDb.Models;
using OpenShock.RepositoryServer.RepoServerDb;
using OpenShock.RepositoryServer.Utils;
using System.Net.Mime;

namespace OpenShock.RepositoryServer.Controllers.V2.Firmware;

/// <summary>
/// Boards firmware can be flashed onto.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("/{version:apiVersion}/firmware/boards")]
[Consumes(MediaTypeNames.Application.Json)]
public sealed class BoardsController : OpenShockControllerBase
{
    private readonly RepoServerContext _db;

    public BoardsController(RepoServerContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Lists the known boards.
    /// </summary>
    /// <param name="chip">Optional chip name filter, matched case-insensitively.</param>
    /// <param name="includeDiscontinued">Whether to include boards that are no longer produced.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The matching boards, ordered by name.</response>
    [HttpGet]
    [CacheControl(300)]
    [ProducesResponseType<List<FirmwareBoardDto>>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
    public async Task<IActionResult> ListBoards(
        [FromQuery] string? chip,
        [FromQuery] bool includeDiscontinued = true,
        CancellationToken ct = default)
    {
        IQueryable<FirmwareBoard> query = _db.FirmwareBoards
            .Include(b => b.ChipNavigation)
            .Include(b => b.UsbDevices);

        // Filter by chip name — what a connected-device detection yields, and what the manifest's
        // chips array is keyed by. Matched case-insensitively against the unique lower(name) index.
        if (!string.IsNullOrWhiteSpace(chip))
        {
            var loweredChip = chip.ToLowerInvariant();
            query = query.Where(b => b.ChipNavigation.Name.ToLower() == loweredChip);
        }

        if (!includeDiscontinued)
        {
            query = query.Where(b => !b.Discontinued);
        }

        var rows = await query.OrderBy(b => b.Name).ToListAsync(ct);

        var boards = rows
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

        return Ok(boards);
    }
}
