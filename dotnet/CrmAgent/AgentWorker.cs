using CrmAgent.Handlers;
using CrmAgent.Models;
using CrmAgent.Services;

namespace CrmAgent;

/// <summary>
/// The main agent poll loop, implemented as a <see cref="BackgroundService"/>.
/// Polls the portal for jobs, executes them, and reports results.
/// Runs until the host requests a graceful shutdown via the cancellation token.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly AgentConfig _config;
    private readonly PortalClient _portal;
    private readonly HandlerFactory _handlers;
    private readonly ILogger<AgentWorker> _logger;

    public AgentWorker(
        AgentConfig config,
        PortalClient portal,
        HandlerFactory handlers,
        ILogger<AgentWorker> logger)
    {
        _config = config;
        _portal = portal;
        _handlers = handlers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agent poll loop started (interval={PollIntervalMs}ms)", _config.PollIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            // ---------------------------------------------------------------
            // Poll for a job
            // ---------------------------------------------------------------
            Job? job;
            try
            {
                job = await _portal.PollForJobAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to poll for job — will retry");
                await WaitAsync(stoppingToken);
                continue;
            }

            if (job is null)
            {
                _logger.LogDebug("No job available — sleeping");
                await WaitAsync(stoppingToken);
                continue;
            }

            _logger.LogInformation("Job received: {JobId} type={JobType}", job.Id, job.Type);
            _logger.LogInformation(
                "Job config: {JobId} baseUrl={BaseUrl} method={Method} authType={AuthType} tokenUrl={TokenUrl} " +
                "params={Params} paginationType={PaginationType} pageSize={PageSize} dataField={DataField} " +
                "pageParam={PageParam} pageSizeParam={PageSizeParam} hashFields={HashFields} blobPath={BlobPath}",
                job.Id,
                job.Config.BaseUrl,
                job.Config.Method,
                job.Config.Auth?.Type,
                job.Config.Auth?.TokenUrl,
                job.Config.Params is not null ? string.Join(", ", job.Config.Params.Select(kv => $"{kv.Key}={kv.Value}")) : null,
                job.Config.Pagination?.Type,
                job.Config.Pagination?.PageSize,
                job.Config.Pagination?.DataField,
                job.Config.Pagination?.PageParam,
                job.Config.Pagination?.PageSizeParam,
                job.Config.HashFields,
                job.Config.BlobPath);

            // Ping jobs are heartbeat checks from the portal. They are marked
            // completed immediately with no handler execution or blob output.
            if (job.Type == JobType.Ping)
            {
                _logger.LogInformation("Ping job received: {JobId} - completing immediately", job.Id);

                await _portal.ReportJobStatusAsync(job.Id, new JobStatusUpdate
                {
                    Status = JobStatus.Completed,
                }, stoppingToken);

                _logger.LogInformation("Ping job completed: {JobId}", job.Id);
                continue;
            }

            // ---------------------------------------------------------------
            // Report "running"
            // ---------------------------------------------------------------
            await _portal.ReportJobStatusAsync(job.Id, new JobStatusUpdate { Status = JobStatus.Running }, stoppingToken);

            // ---------------------------------------------------------------
            // Start heartbeat timer
            // ---------------------------------------------------------------
            var lastProgress = new JobProgress { ProcessedRows = 0 };
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var heartbeatTask = RunHeartbeatAsync(job.Id, () => lastProgress, heartbeatCts.Token);

            // ---------------------------------------------------------------
            // Execute the handler
            // ---------------------------------------------------------------
            try
            {
                var handler = _handlers.GetHandler(job);

                var result = await handler.ExecuteAsync(job, progress =>
                {
                    lastProgress = progress;
                }, stoppingToken);

                // Stop heartbeat
                await heartbeatCts.CancelAsync();
                await AwaitHeartbeat(heartbeatTask);

                // Report completion
                await _portal.ReportJobStatusAsync(job.Id, new JobStatusUpdate
                {
                    Status = JobStatus.Completed,
                    Progress = new JobProgress { ProcessedRows = result.ProcessedRows },
                    BlobName = result.BlobName,
                }, stoppingToken);

                _logger.LogInformation("Job completed: {JobId} rows={Rows} blob={BlobName}",
                    job.Id, result.ProcessedRows, result.BlobName);
            }
            catch (OperationCanceledException)
            {
                await heartbeatCts.CancelAsync();
                await AwaitHeartbeat(heartbeatTask);
                _logger.LogInformation("Job {JobId} cancelled due to shutdown", job.Id);
                break;
            }
            catch (Exception ex)
            {
                await heartbeatCts.CancelAsync();
                await AwaitHeartbeat(heartbeatTask);

                _logger.LogError(ex, "Job failed: {JobId}", job.Id);

                await _portal.ReportJobStatusAsync(job.Id, new JobStatusUpdate
                {
                    Status = JobStatus.Failed,
                    Error = ex.Message,
                }, CancellationToken.None);
            }

            // Sleep before next poll
            await WaitAsync(stoppingToken);
        }

        _logger.LogInformation("Agent poll loop stopped");
    }

    private async Task WaitAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_config.PollIntervalMs, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown — return immediately.
        }
    }

    private async Task RunHeartbeatAsync(string jobId, Func<JobProgress> getProgress, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_config.HeartbeatIntervalMs, ct);
                await _portal.ReportJobStatusAsync(jobId, new JobStatusUpdate
                {
                    Status = JobStatus.Running,
                    Progress = getProgress(),
                }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the heartbeat is stopped.
        }
    }

    private static async Task AwaitHeartbeat(Task heartbeatTask)
    {
        try { await heartbeatTask; }
        catch (OperationCanceledException) { }
    }
}
