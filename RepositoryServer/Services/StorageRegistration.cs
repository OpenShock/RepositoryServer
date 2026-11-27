using OpenShock.RepositoryServer.Config;

namespace OpenShock.RepositoryServer.Services;

/// <summary>
/// Service keys for the <see cref="IStorageService"/> instances. Firmware and desktop modules each
/// have their own storage, configured under <c>Firmware:Storage</c> and <c>Modules:Storage</c>, so
/// they can live in different buckets or zones behind different CDN hosts.
/// </summary>
public static class StorageKeys
{
    public const string Firmware = "firmware";
    public const string Modules = "modules";
}

public static class StorageRegistration
{
    /// <summary>
    /// Registers an <see cref="IStorageService"/> for <paramref name="config"/> under <paramref name="key"/>.
    /// Resolve it with <c>[FromKeyedServices(key)]</c>.
    /// </summary>
    public static IServiceCollection AddKeyedStorage(this IServiceCollection services, string key, StorageConfig config)
    {
        switch (config.Type)
        {
            case StorageType.BunnyCdn:
                var bunnyCdnConfig = config.BunnyCdn
                                     ?? throw new InvalidOperationException($"BunnyCdn storage config is required for {key} storage when Type is BunnyCdn.");
                // Named per key: the service puts its zone's access key on the client's default headers.
                var httpClientName = $"{nameof(BunnyCdnStorageService)}-{key}";
                services.AddHttpClient(httpClientName);
                services.AddKeyedSingleton<IStorageService>(key, (sp, _) => new BunnyCdnStorageService(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(httpClientName), bunnyCdnConfig));
                break;

            case StorageType.Local:
                var localConfig = config.Local
                                  ?? throw new InvalidOperationException($"Local storage config is required for {key} storage when Type is Local.");
                services.AddKeyedSingleton<IStorageService>(key, (_, _) => new LocalStorageService(localConfig));
                break;

            case StorageType.S3:
                var s3Config = config.S3
                               ?? throw new InvalidOperationException($"S3 storage config is required for {key} storage when Type is S3.");
                services.AddKeyedSingleton<IStorageService>(key, (_, _) => new S3StorageService(s3Config));
                break;

            default:
                throw new InvalidOperationException($"Unknown storage type for {key} storage: {config.Type}");
        }

        return services;
    }
}
