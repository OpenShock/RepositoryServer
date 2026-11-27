using System.ComponentModel.DataAnnotations;

namespace OpenShock.RepositoryServer.Config;

public sealed class ModulesConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
    public Uri? Homepage { get; init; } = null;

    /// <summary>Public base URL module zips are served from. Points at the root of <see cref="Storage"/>.</summary>
    public required string CdnBaseUrl { get; init; }

    /// <summary>Where module zips are written. Separate from the firmware storage.</summary>
    [Required] public required StorageConfig Storage { get; init; }
}