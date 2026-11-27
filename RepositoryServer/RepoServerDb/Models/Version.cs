namespace OpenShock.RepositoryServer.RepoServerDb.Models;

public sealed class Version
{
    public required string VersionName { get; set; }

    public required string Module { get; set; }

    /// <summary>
    /// Where the zip is hosted, when that is not this server's module storage. Null for a version
    /// uploaded through this server: its URL is built from <c>Modules:CdnBaseUrl</c> when served.
    /// Use <see cref="Utils.ModuleZip.Url"/> rather than reading this directly.
    /// </summary>
    public Uri? ZipUrl { get; set; }

    public required byte[] HashSha256 { get; set; }

    public Uri? ChangelogUrl { get; set; }

    public Uri? ReleaseUrl { get; set; }

    public Module ModuleNavigation { get; set; } = null!;
}
