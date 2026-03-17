using System.Net.Http.Json;
using System.Text.Json;
using CrmAgent.Models;

namespace CrmAgent.Services;

/// <summary>
/// HTTP client for the LGA Customer Portal API.
/// Registered as a typed HttpClient via DI.
/// </summary>
public sealed class PortalClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger<PortalClient> _logger;

    public PortalClient(HttpClient http, ILogger<PortalClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Poll the portal for a pending job.
    /// Returns the job if one is available, or <c>null</c> if the portal
    /// responded with 204 (no work). Throws on network or non-2xx errors.
    /// </summary>
    public async Task<Job?> PollForJobAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("api/agent/jobs", ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Poll failed: {(int)response.StatusCode} {response.ReasonPhrase} — {body}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("Poll response: {Body}", json);

        var envelope = JsonSerializer.Deserialize<PollResponse>(json, JsonOptions);
        return envelope?.Job;
    }

    /// <summary>
    /// Report a status update for an active job back to the portal.
    /// Does NOT throw on errors — logs and returns silently so the agent
    /// can continue running even when the portal is temporarily unreachable.
    /// </summary>
    public async Task ReportJobStatusAsync(string jobId, JobStatusUpdate update, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/agent/jobs/{Uri.EscapeDataString(jobId)}";
            var response = await _http.PatchAsJsonAsync(url, update, JsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Portal responded with {StatusCode} to status update for job {JobId}: {Body}",
                    (int)response.StatusCode, jobId, body);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to report job status for {JobId}", jobId);
        }
    }
}
