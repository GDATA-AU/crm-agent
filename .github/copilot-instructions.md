# Copilot Instructions — crm-agent

## What this project is

A lightweight extraction agent that polls the LGA Customer Portal for jobs, executes SQL queries or REST API calls on-prem, and writes gzipped NDJSON results to Azure Blob Storage. Deployed as a native Windows service.

## Tech stack

- **Runtime:** .NET 10 Worker Service
- **Language:** C# 13, nullable enabled, implicit usings
- **DB drivers:** Microsoft.Data.SqlClient (MSSQL), Npgsql (Postgres), MySqlConnector (MySQL)
- **Cloud:** Azure.Storage.Blobs
- **Logging:** Serilog (structured JSON)
- **Service hosting:** Microsoft.Extensions.Hosting.WindowsServices
- **Test:** xUnit

## Project layout

```
dotnet/
  CrmAgent/
    Program.cs                  # Entry point, DI wiring, Windows Service support
    AgentConfig.cs              # Config from appsettings.json + env vars
    AgentWorker.cs              # BackgroundService — the main poll loop
    appsettings.json            # Configuration
    install-service.bat         # Windows service install/uninstall
    Models/
      Job.cs                    # All shared types (Job, JobConfig, enums, status, etc.)
    Services/
      PortalClient.cs           # Typed HttpClient for the portal API
      BlobStorageService.cs     # Azure Blob upload helpers
      HashService.cs            # SHA-256 row hashing for change detection
      NdjsonGzipWriter.cs       # NDJSON + GZip streaming
    Handlers/
      IJobHandler.cs            # Handler interface
      HandlerFactory.cs         # Resolves job type → handler via DI
      SqlHandler.cs             # SQL handler (MSSQL, Postgres, MySQL via DbDataReader)
      RestApiHandler.cs         # REST API handler (offset/cursor/link-header pagination, Bearer/OAuth2)
  CrmAgent.Tests/
    HashServiceTests.cs
    NdjsonGzipWriterTests.cs
    BlobStorageServiceTests.cs
```

## Legacy Node.js code

The `src/` directory at the repo root contains the original Node.js/TypeScript prototype. It is **not the active implementation** — the production agent is in `dotnet/CrmAgent/`. The Node code is retained for reference only.

## Conventions

- **Files:** PascalCase matching class name (`RestApiHandler.cs`).
- **Symbols:** PascalCase for public members, `_camelCase` for private fields, `camelCase` for locals/parameters.
- **Async:** All I/O methods are async and accept `CancellationToken`. Respect cancellation throughout.
- **DI:** Services are registered in `Program.cs`. Use constructor injection. `PortalClient` is a typed `HttpClient`.
- **Streaming:** Use `DbDataReader.ReadAsync()` for database streaming, `GZipStream` for compression. Never buffer full datasets in memory.
- **Error handling:** Fail fast on config errors at startup. The polling loop is resilient — it catches and logs transient failures. Handlers should throw on unrecoverable errors.
- **Graceful shutdown:** The `BackgroundService` base class propagates `CancellationToken` from the host. The agent finishes the current job before exiting. Respect this pattern.
- **Connection strings:** SQL jobs resolve connection strings via `connectionRef` → env var `CONN_<NAME>` (uppercased, hyphens → underscores). Direct `connectionString` is a fallback.
- **Handler pattern:** Handlers implement `IJobHandler` and are resolved by `HandlerFactory` via DI. Register new handlers in `HandlerFactory.cs` and `Program.cs`.
- **SQL safety:** The SQL handler rejects queries that don't start with `SELECT` or `WITH`.

## Commands

| Task | Command |
|------|---------|
| Build | `dotnet build` |
| Run (dev) | `dotnet run --project dotnet/CrmAgent` |
| Test | `dotnet test dotnet/CrmAgent.Tests` |
| Publish | `dotnet publish dotnet/CrmAgent -c Release -r win-x64 --self-contained -o publish` |

## Environment variables

Config via `appsettings.json` (section `Agent`) or environment variables (env vars take precedence).

Required: `PORTAL_URL`, `AGENT_API_KEY`, `AZURE_STORAGE_CONNECTION_STRING`.
SQL connection refs: `CONN_<NAME>` (e.g. `CONN_MY_DB`).
