using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CrmAgent.Services;

/// <summary>
/// Helpers for uploading to the <c>erp-imports</c> Azure Blob Storage container.
/// </summary>
public sealed class BlobStorageService
{
    private const string ContainerName = "erp-imports";

    private readonly BlobContainerClient _container;

    public BlobStorageService(AgentConfig config)
    {
        var serviceClient = new BlobServiceClient(config.AzureStorageConnectionString);
        _container = serviceClient.GetBlobContainerClient(ContainerName);
    }

    /// <summary>
    /// Upload a stream to blob storage.
    /// </summary>
    public async Task UploadStreamAsync(string blobName, Stream stream, CancellationToken ct = default)
    {
        var blobClient = _container.GetBlobClient(blobName);
        await blobClient.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/gzip" },
        }, cancellationToken: ct);
    }

    /// <summary>
    /// Build the full blob name from a path prefix and a timestamp.
    /// </summary>
    public static string BuildBlobName(string blobPath, DateTime timestamp)
    {
        var ts = timestamp
            .ToUniversalTime()
            .ToString("yyyy-MM-ddTHH-mm-ssZ");
        var prefix = blobPath.EndsWith('/') ? blobPath : blobPath + "/";
        return $"{prefix}{ts}.ndjson.gz";
    }
}
