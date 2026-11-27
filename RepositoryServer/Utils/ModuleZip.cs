using OpenShock.RepositoryServer.Config;
using Version = OpenShock.RepositoryServer.RepoServerDb.Models.Version;

namespace OpenShock.RepositoryServer.Utils;

/// <summary>
/// Where a module version's zip lives. A version uploaded through this server is stored at
/// <see cref="StoragePath"/> in module storage and its URL is built from <c>Modules:CdnBaseUrl</c>
/// when it is served, so moving the CDN only needs a config change. A version registered with an
/// explicit URL, such as one hosted before this server stored modules itself, keeps that URL.
/// </summary>
public static class ModuleZip
{
    /// <summary>Key of a hosted version's zip, relative to the root of module storage.</summary>
    public static string StoragePath(string moduleId, string version) => $"{moduleId}/{version}/module.zip";

    /// <summary>Public URL of a hosted version's zip.</summary>
    public static Uri HostedUrl(ModulesConfig config, string moduleId, string version) =>
        new($"{config.CdnBaseUrl.TrimEnd('/')}/{StoragePath(moduleId, version)}");

    /// <summary>The URL clients download <paramref name="version"/> from.</summary>
    public static Uri Url(ModulesConfig config, Version version) =>
        version.ZipUrl ?? HostedUrl(config, version.Module, version.VersionName);
}
