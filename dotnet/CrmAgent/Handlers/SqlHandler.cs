using System.Data.Common;
using CrmAgent.Models;
using CrmAgent.Services;
using Microsoft.Data.SqlClient;

namespace CrmAgent.Handlers;

/// <summary>
/// Executes SQL extraction jobs against MSSQL using streaming database cursors.
/// Always uses Windows Integrated Security (the service account).
/// </summary>
public sealed class SqlHandler : IJobHandler
{
    private static readonly HashSet<string> AllowedFirstTokens = ["SELECT", "WITH"];

    private readonly BlobStorageService _blob;
    private readonly ILogger<SqlHandler> _logger;

    public SqlHandler(BlobStorageService blob, ILogger<SqlHandler> logger)
    {
        _blob = blob;
        _logger = logger;
    }

    public async Task<HandlerResult> ExecuteAsync(Job job, Action<JobProgress> onProgress, CancellationToken ct)
    {
        var config = job.Config.ToSqlConfig(job);
        var connectionString = BuildMssqlConnectionString(config);

        // Guard: reject queries that aren't SELECT statements.
        var firstToken = config.Query.TrimStart().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
        if (!AllowedFirstTokens.Contains(firstToken))
        {
            throw new InvalidOperationException(
                $"SQL query must be a SELECT statement (got \"{firstToken}...\"). " +
                "The agent does not execute DML/DDL queries.");
        }

        var timestamp = DateTime.UtcNow;
        var blobName = BlobStorageService.BuildBlobName(config.BlobPath, timestamp);

        _logger.LogInformation("Starting SQL extraction for job {JobId} blob={BlobName}",
            job.Id, blobName);

        int processedRows;
        using var memoryStream = new MemoryStream();

        await using (var writer = new NdjsonGzipWriter(memoryStream, leaveOpen: true))
        {
            processedRows = await ExecuteMssqlAsync(connectionString, config.Query, config.HashFields, writer, onProgress, ct);
        }

        // Upload the compressed NDJSON to blob storage.
        memoryStream.Position = 0;
        await _blob.UploadStreamAsync(blobName, memoryStream, ct);

        _logger.LogInformation("SQL extraction complete for job {JobId}: {Rows} rows → {BlobName}",
            job.Id, processedRows, blobName);

        return new HandlerResult { BlobName = blobName, ProcessedRows = processedRows };
    }

    /// <summary>
    /// Builds an MSSQL connection string from the server and database name
    /// provided by the portal, using Windows Integrated Security (the service account).
    /// SQL authentication credentials are never accepted.
    /// </summary>
    private static string BuildMssqlConnectionString(SqlJobConfig config)
    {
        if (string.IsNullOrEmpty(config.Server))
            throw new InvalidOperationException("MSSQL job config missing 'server'");
        if (string.IsNullOrEmpty(config.Database))
            throw new InvalidOperationException("MSSQL job config missing 'database'");

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = config.Server,
            InitialCatalog = config.Database,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
        };
        return builder.ConnectionString;
    }

    private static async Task<int> ExecuteMssqlAsync(
        string connectionString, string query, string[] hashFields,
        NdjsonGzipWriter writer, Action<JobProgress> onProgress, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await StreamReaderAsync(reader, hashFields, writer, onProgress, ct);
    }

    /// <summary>
    /// Generic streaming reader that works with any ADO.NET DbDataReader.
    /// Reads rows one at a time and writes them as NDJSON with a row hash.
    /// </summary>
    private static async Task<int> StreamReaderAsync(
        DbDataReader reader, string[] hashFields,
        NdjsonGzipWriter writer, Action<JobProgress> onProgress, CancellationToken ct)
    {
        var processedRows = 0;
        var fieldNames = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToArray();

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(fieldNames.Length);
            for (var i = 0; i < fieldNames.Length; i++)
            {
                row[fieldNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            row["_rowHash"] = HashService.ComputeRowHash(row, hashFields);
            await writer.WriteRowAsync(row);

            processedRows++;
            if (processedRows % 1000 == 0)
            {
                onProgress(new JobProgress
                {
                    ProcessedRows = processedRows,
                    Message = $"Processing row {processedRows}...",
                });
            }
        }

        return processedRows;
    }
}
