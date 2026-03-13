using CrmAgent.Services;

namespace CrmAgent.Tests;

public class BlobStorageServiceTests
{
    [Fact]
    public void BuildBlobNameFormatsCorrectly()
    {
        var timestamp = new DateTime(2026, 3, 13, 2, 30, 0, DateTimeKind.Utc);
        var result = BlobStorageService.BuildBlobName("pathway/names-individuals/snapshots/", timestamp);
        Assert.Equal("pathway/names-individuals/snapshots/2026-03-13T02-30-00Z.ndjson.gz", result);
    }

    [Fact]
    public void BuildBlobNameAddsTrailingSlash()
    {
        var timestamp = new DateTime(2026, 3, 13, 2, 30, 0, DateTimeKind.Utc);
        var result = BlobStorageService.BuildBlobName("pathway/snapshots", timestamp);
        Assert.Equal("pathway/snapshots/2026-03-13T02-30-00Z.ndjson.gz", result);
    }
}
