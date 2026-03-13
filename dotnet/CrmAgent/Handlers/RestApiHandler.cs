using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrmAgent.Models;
using CrmAgent.Services;
using Microsoft.Extensions.Http;

namespace CrmAgent.Handlers;

/// <summary>
/// Executes REST API extraction jobs with support for Bearer/OAuth2 auth
/// and offset/cursor/link-header pagination.
/// </summary>
public sealed partial class RestApiHandler : IJobHandler
{
    private readonly BlobStorageService _blob;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<RestApiHandler> _logger;

    public RestApiHandler(BlobStorageService blob, IHttpClientFactory httpFactory, ILogger<RestApiHandler> logger)
    {
        _blob = blob;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<HandlerResult> ExecuteAsync(Job job, Action<JobProgress> onProgress, CancellationToken ct)
    {
        var config = job.Config.ToRestApiConfig();
        var token = await ResolveTokenAsync(config.Auth, ct);

        var timestamp = DateTime.UtcNow;
        var blobName = BlobStorageService.BuildBlobName(job.BlobPath, timestamp);

        _logger.LogInformation("Starting REST API extraction for job {JobId} url={BaseUrl} blob={BlobName}",
            job.Id, config.BaseUrl, blobName);

        int processedRows;
        using var memoryStream = new MemoryStream();

        await using (var writer = new NdjsonGzipWriter(memoryStream, leaveOpen: true))
        {
            processedRows = config.Pagination switch
            {
                null => await FetchSinglePageAsync(config, token, job.HashFields, writer, ct),
                { Type: PaginationType.LinkHeader } => await FetchWithLinkHeaderAsync(config, token, job.HashFields, writer, onProgress, ct),
                { Type: PaginationType.Offset } => await FetchWithOffsetAsync(config, token, job.HashFields, writer, onProgress, ct),
                { Type: PaginationType.Cursor } => await FetchWithCursorAsync(config, token, job.HashFields, writer, onProgress, ct),
                _ => throw new InvalidOperationException($"Unsupported pagination type: {config.Pagination.Type}"),
            };
        }

        memoryStream.Position = 0;
        await _blob.UploadStreamAsync(blobName, memoryStream, ct);

        _logger.LogInformation("REST API extraction complete for job {JobId}: {Rows} rows → {BlobName}",
            job.Id, processedRows, blobName);

        return new HandlerResult { BlobName = blobName, ProcessedRows = processedRows };
    }

    // -----------------------------------------------------------------------
    // Auth
    // -----------------------------------------------------------------------

    private async Task<string?> ResolveTokenAsync(RestApiAuth? auth, CancellationToken ct)
    {
        if (auth is null) return null;

        return auth.Type switch
        {
            AuthType.Bearer => auth.Token,
            AuthType.OAuth2ClientCredentials => await FetchOAuth2TokenAsync(auth, ct),
            _ => null,
        };
    }

    private async Task<string> FetchOAuth2TokenAsync(RestApiAuth auth, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(auth.TokenUrl) || string.IsNullOrEmpty(auth.ClientId) || string.IsNullOrEmpty(auth.ClientSecret))
            throw new InvalidOperationException("OAuth2 auth requires tokenUrl, clientId, and clientSecret");

        using var http = _httpFactory.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = auth.ClientId,
            ["client_secret"] = auth.ClientSecret,
        };
        if (!string.IsNullOrEmpty(auth.Scope))
            form["scope"] = auth.Scope;

        var response = await http.PostAsync(auth.TokenUrl, new FormUrlEncodedContent(form), ct);

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"OAuth2 token request failed: {(int)response.StatusCode} — {text}");
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("OAuth2 response missing access_token");
    }

    // -----------------------------------------------------------------------
    // HTTP helpers
    // -----------------------------------------------------------------------

    private HttpClient CreateApiClient(RestApiJobConfig config, string? token)
    {
        var http = _httpFactory.CreateClient();

        if (config.Headers is not null)
        {
            foreach (var (key, value) in config.Headers)
                http.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }

        if (token is not null)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return http;
    }

    private static async Task<(JsonElement Body, HttpResponseHeaders Headers)> FetchPageAsync(
        HttpClient http, string url, string method, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        var response = await http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"REST API request failed: {(int)response.StatusCode} {response.ReasonPhrase} — {text}");
        }

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return (doc.RootElement.Clone(), response.Headers);
    }

    /// <summary>
    /// Resolve a dot-notation path into a JSON value.
    /// </summary>
    private static JsonElement? GetNestedValue(JsonElement root, string path)
    {
        var current = root;
        foreach (var key in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out var next))
                return null;
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Write an array of JSON records as NDJSON rows with hashes.
    /// Returns the number of records written.
    /// </summary>
    private static async Task<int> WriteRecordsAsync(
        JsonElement records, string[] hashFields, NdjsonGzipWriter writer)
    {
        var count = 0;
        foreach (var record in records.EnumerateArray())
        {
            // Materialise to dictionary so we can add _rowHash.
            var row = new Dictionary<string, object?>();
            foreach (var prop in record.EnumerateObject())
            {
                row[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetDecimal(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.GetRawText(),
                };
            }
            row["_rowHash"] = HashService.ComputeRowHash(record, hashFields);
            await writer.WriteRowAsync(row);
            count++;
        }
        return count;
    }

    private static JsonElement GetRecords(JsonElement body, string? dataField)
    {
        if (dataField is not null)
        {
            var nested = GetNestedValue(body, dataField);
            if (nested is null || nested.Value.ValueKind != JsonValueKind.Array)
                return default;
            return nested.Value;
        }

        // If the root is an array, return it directly. Otherwise wrap in an array-like approach.
        if (body.ValueKind == JsonValueKind.Array)
            return body;

        // Single object — treated as one-element array. Parse it that way.
        using var doc = JsonDocument.Parse($"[{body.GetRawText()}]");
        return doc.RootElement.Clone();
    }

    // -----------------------------------------------------------------------
    // Pagination strategies
    // -----------------------------------------------------------------------

    private async Task<int> FetchSinglePageAsync(
        RestApiJobConfig config, string? token, string[] hashFields,
        NdjsonGzipWriter writer, CancellationToken ct)
    {
        using var http = CreateApiClient(config, token);
        var (body, _) = await FetchPageAsync(http, config.BaseUrl, config.Method, ct);
        var records = GetRecords(body, null);
        return records.ValueKind == JsonValueKind.Array
            ? await WriteRecordsAsync(records, hashFields, writer)
            : 0;
    }

    private async Task<int> FetchWithLinkHeaderAsync(
        RestApiJobConfig config, string? token, string[] hashFields,
        NdjsonGzipWriter writer, Action<JobProgress> onProgress, CancellationToken ct)
    {
        using var http = CreateApiClient(config, token);
        var processedRows = 0;
        string? nextUrl = config.BaseUrl;

        while (nextUrl is not null)
        {
            var (body, headers) = await FetchPageAsync(http, nextUrl, config.Method, ct);
            var records = GetRecords(body, config.Pagination?.DataField);
            if (records.ValueKind == JsonValueKind.Array)
                processedRows += await WriteRecordsAsync(records, hashFields, writer);

            onProgress(new JobProgress { ProcessedRows = processedRows, Message = $"Processed {processedRows} records..." });
            nextUrl = ParseLinkHeaderNext(headers);
        }

        return processedRows;
    }

    private async Task<int> FetchWithOffsetAsync(
        RestApiJobConfig config, string? token, string[] hashFields,
        NdjsonGzipWriter writer, Action<JobProgress> onProgress, CancellationToken ct)
    {
        using var http = CreateApiClient(config, token);
        var pagination = config.Pagination!;
        var pageSize = pagination.PageSize ?? 100;
        var pageParam = pagination.PageParam ?? "page";
        var pageSizeParam = pagination.PageSizeParam ?? "pageSize";
        var processedRows = 0;
        var page = 0;

        while (true)
        {
            var separator = config.BaseUrl.Contains('?') ? "&" : "?";
            var url = $"{config.BaseUrl}{separator}{pageParam}={page}&{pageSizeParam}={pageSize}";
            var (body, _) = await FetchPageAsync(http, url, config.Method, ct);
            var records = GetRecords(body, pagination.DataField);

            if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() == 0)
                break;

            var count = await WriteRecordsAsync(records, hashFields, writer);
            processedRows += count;

            onProgress(new JobProgress { ProcessedRows = processedRows, Message = $"Processed {processedRows} records..." });

            if (count < pageSize) break;
            page++;
        }

        return processedRows;
    }

    private async Task<int> FetchWithCursorAsync(
        RestApiJobConfig config, string? token, string[] hashFields,
        NdjsonGzipWriter writer, Action<JobProgress> onProgress, CancellationToken ct)
    {
        using var http = CreateApiClient(config, token);
        var pagination = config.Pagination!;
        var pageSize = pagination.PageSize ?? 100;
        var pageSizeParam = pagination.PageSizeParam ?? "pageSize";
        var cursorField = pagination.CursorField ?? "nextCursor";
        var pageParam = pagination.PageParam ?? "cursor";
        var processedRows = 0;
        string? cursor = null;

        while (true)
        {
            var separator = config.BaseUrl.Contains('?') ? "&" : "?";
            var url = $"{config.BaseUrl}{separator}{pageSizeParam}={pageSize}";
            if (cursor is not null)
                url += $"&{pageParam}={Uri.EscapeDataString(cursor)}";

            var (body, _) = await FetchPageAsync(http, url, config.Method, ct);
            var records = GetRecords(body, pagination.DataField);

            if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() == 0)
                break;

            processedRows += await WriteRecordsAsync(records, hashFields, writer);

            onProgress(new JobProgress { ProcessedRows = processedRows, Message = $"Processed {processedRows} records..." });

            var nextCursor = GetNestedValue(body, cursorField);
            if (nextCursor is null ||
                nextCursor.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                break;

            cursor = nextCursor.Value.ToString();
        }

        return processedRows;
    }

    // -----------------------------------------------------------------------
    // Link header parsing
    // -----------------------------------------------------------------------

    private static string? ParseLinkHeaderNext(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values))
            return null;

        var link = string.Join(", ", values);
        var match = LinkHeaderNextRegex().Match(link);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"<([^>]+)>;\s*rel=""next""")]
    private static partial Regex LinkHeaderNextRegex();
}
