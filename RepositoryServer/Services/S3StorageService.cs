using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OpenShock.RepositoryServer.Config;

namespace OpenShock.RepositoryServer.Services;

public sealed class S3StorageService : IStorageService, IDisposable
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly string? _keyPrefix;
    private readonly bool _disablePayloadSigning;

    public S3StorageService(S3StorageConfig config)
    {
        _bucketName = config.BucketName;
        _keyPrefix = config.KeyPrefix?.TrimEnd('/');

        var s3Config = new AmazonS3Config
        {
            // Only send and verify checksums where the S3 API requires them. The SDK otherwise adds
            // trailing CRC checksums to every request, which S3-compatible services such as
            // Cloudflare R2 do not all accept.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        if (config.ServiceUrl != null)
        {
            s3Config.ServiceURL = config.ServiceUrl;
            // Unsigned payloads are only permitted over TLS, so plain-HTTP endpoints (local MinIO) keep signing.
            _disablePayloadSigning = config.ServiceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        if (config.Region != null)
        {
            s3Config.AuthenticationRegion = config.Region;
        }

        _s3Client = new AmazonS3Client(config.AccessKey, config.SecretKey, s3Config);
    }

    /// <inheritdoc />
    public async Task UploadFileAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = ResolveKey(path),
            InputStream = content,
            ContentType = "application/octet-stream",
            // S3-compatible endpoints (Cloudflare R2 among them) do not support streaming SigV4
            // uploads, which is what the SDK uses by default.
            DisablePayloadSigning = _disablePayloadSigning,
            DisableDefaultChecksumValidation = _disablePayloadSigning,
        };

        await _s3Client.PutObjectAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        // Server-side copy — the object never travels through this process.
        var request = new CopyObjectRequest
        {
            SourceBucket = _bucketName,
            SourceKey = ResolveKey(sourcePath),
            DestinationBucket = _bucketName,
            DestinationKey = ResolveKey(destinationPath),
        };

        await _s3Client.CopyObjectAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await _s3Client.DeleteObjectAsync(_bucketName, ResolveKey(path), cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteDirectoryAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var resolvedPrefix = ResolveKey(prefix.TrimEnd('/') + "/");

        var listRequest = new ListObjectsV2Request
        {
            BucketName = _bucketName,
            Prefix = resolvedPrefix,
        };

        ListObjectsV2Response response;
        do
        {
            response = await _s3Client.ListObjectsV2Async(listRequest, cancellationToken);

            if (response.S3Objects.Count > 0)
            {
                var deleteRequest = new DeleteObjectsRequest
                {
                    BucketName = _bucketName,
                    Objects = response.S3Objects.Select(o => new KeyVersion { Key = o.Key }).ToList(),
                };
                await _s3Client.DeleteObjectsAsync(deleteRequest, cancellationToken);
            }

            listRequest.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListFilesAsync(string prefix = "", [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var resolvedPrefix = ResolveKey(prefix);

        var listRequest = new ListObjectsV2Request
        {
            BucketName = _bucketName,
            Prefix = resolvedPrefix,
        };

        ListObjectsV2Response response;
        do
        {
            response = await _s3Client.ListObjectsV2Async(listRequest, cancellationToken);

            foreach (var obj in response.S3Objects)
            {
                // Strip key prefix to return paths relative to storage root
                var relativePath = _keyPrefix != null
                    ? obj.Key[(_keyPrefix.Length + 1)..]
                    : obj.Key;
                yield return relativePath;
            }

            listRequest.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);
    }

    private string ResolveKey(string path) => _keyPrefix != null ? $"{_keyPrefix}/{path}" : path;

    public void Dispose() => _s3Client.Dispose();
}
