using System.Data.Common;
using CrmAgent.Models;
using CrmAgent.Services;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace CrmAgent.Handlers;

/// <summary>
/// Executes SQL extraction jobs using streaming database cursors.
/// Supports MSSQL, PostgreSQL, and MySQL.
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
        var config = job.Config.ToSqlConfig();
        var connectionString = AgentConfig.ResolveConnectionString(config.ConnectionRef, config.ConnectionString);

        // Guard: reject queries that aren't SELECT statements.
        var firstToken = config.Query.TrimStart().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
        if (!AllowedFirstTokens.Contains(firstToken))
        {
            throw new InvalidOperationException(
                $"SQL query must be a SELECT statement (got \"{firstToken}...\"). " +
                "The agent does not execute DML/DDL queries.");
        }

        var timestamp = DateTime.UtcNow;
        var blobName = BlobStorageService.BuildBlobName(job.BlobPath, timestamp);

        _logger.LogInformation("Starting SQL extraction for job {JobId} driver={Driver} blob={BlobName}",
            job.Id, config.Driver, blobName);

        int processedRows;
        using var memoryStream = new MemoryStream();

        await using (var writer = new NdjsonGzipWriter(memoryStream))
        {
            processedRows = config.Driver switch
            {
                SqlDriver.Mssql => await ExecuteMssqlAsync(connectionString, config.Query, job.HashFields, writer, onProgress, ct),
                SqlDriver.Postgres => await ExecutePostgresAsync(connectionString, config.Query, job.HashFields, writer, onProgress, ct),
                SqlDriver.Mysql => await ExecuteMysqlAsync(connectionString, config.Query, job.HashFields, writer, onProgress, ct),
                _ => throw new InvalidOperationException($"Unsupported database driver: {config.Driver}"),
            };
        }

        // Upload the compressed NDJSON to blob storage.
        memoryStream.Position = 0;
        await _blob.UploadStreamAsync(blobName, memoryStream, ct);

        _logger.LogInformation("SQL extraction complete for job {JobId}: {Rows} rows → {BlobName}",
            job.Id, processedRows, blobName);

        return new HandlerResult { BlobName = blobName, ProcessedRows = processedRows };
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

    private static async Task<int> ExecutePostgresAsync(
        string connectionString, string query, string[] hashFields,
        NdjsonGzipWriter writer, Action<JobProgress> onProgress, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await StreamReaderAsync(reader, hashFields, writer, onProgress, ct);
    }

    private static async Task<int> ExecuteMysqlAsync(
        string connectionString, string query, string[] hashFields,
        NdjsonGzipWriter writer, Action<JobProgress> onProgress, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new MySqlCommand(query, connection);
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
