# crm-agent (.NET)

A lightweight extraction agent that runs as a Windows service, polls the LGA Customer Portal for jobs, executes them locally (SQL queries or REST API calls), and writes gzip-compressed NDJSON results to Azure Blob Storage.

## Overview

```
crm-agent (extracts from on-prem DB or external API) → Azure Blob Storage → Portal (diffs, upserts to Postgres)
```

The agent is **intentionally dumb** — it has no knowledge of the citizen schema, deduplication logic, or business rules. It:

1. Polls the portal's REST API for a pending job
2. Executes the job (SQL query or REST API extraction) using streaming
3. Computes a SHA-256 `_rowHash` for each row (for change detection)
4. Writes gzip-compressed NDJSON to Azure Blob Storage
5. Reports completion (or failure) back to the portal

## Tech stack

- **.NET 10** Worker Service
- **Microsoft.Data.SqlClient** — SQL Server
- **Npgsql** — PostgreSQL
- **MySqlConnector** — MySQL / MariaDB
- **Azure.Storage.Blobs** — blob upload
- **Serilog** — structured JSON logging
- **Microsoft.Extensions.Hosting.WindowsServices** — native Windows service support

## Project layout

```
CrmAgent/
  Program.cs                  # Entry point, DI configuration
  AgentConfig.cs              # Configuration from env vars / appsettings
  AgentWorker.cs              # BackgroundService — the main poll loop
  appsettings.json            # Configuration file
  install-service.bat         # Windows service install/uninstall
  Models/
    Job.cs                    # All shared types (Job, JobConfig, enums, etc.)
  Services/
    PortalClient.cs           # HTTP client for the portal API
    BlobStorageService.cs     # Azure Blob upload helpers
    HashService.cs            # SHA-256 row hashing
    NdjsonGzipWriter.cs       # NDJSON + gzip streaming
  Handlers/
    IJobHandler.cs            # Handler interface
    HandlerFactory.cs         # Resolves job type → handler
    SqlHandler.cs             # SQL handler (MSSQL, Postgres, MySQL)
    RestApiHandler.cs         # REST API handler (offset/cursor/link-header pagination)
CrmAgent.Tests/
    HashServiceTests.cs
    NdjsonGzipWriterTests.cs
    BlobStorageServiceTests.cs
```

## Prerequisites

- **Windows Server 2016+** (or Windows 10+ for development)
- Network access **outbound** to:
  - The portal URL (HTTPS, port 443)
  - Azure Blob Storage (HTTPS, port 443)
- Network access to local databases
- An Azure Blob Storage account with an `erp-imports` container

No inbound firewall rules are required. All communication is initiated by the agent.

## Quick start (development)

```bash
cd dotnet/CrmAgent
dotnet run
```

## Build & publish

```bash
# Self-contained single-file exe (no .NET runtime needed on target)
dotnet publish -c Release -r win-x64 --self-contained -o publish

# Or framework-dependent (smaller, requires .NET runtime on target)
dotnet publish -c Release -o publish
```

## Install as a Windows service

### Install (run as Administrator)

```powershell
# 1. Publish first
dotnet publish -c Release -r win-x64 --self-contained -o publish

# 2. Edit appsettings.json in the publish folder with your config

# 3. Install the service
install-service.bat
```

### Uninstall

```powershell
install-service.bat --uninstall
```

### Manual service management

```powershell
sc query crm-agent       # Check status
sc stop crm-agent        # Stop
sc start crm-agent       # Start
```

## Configuration

Configuration can be set via `appsettings.json` or environment variables. Environment variables take precedence for backwards compatibility.

| Setting | Env var | Required | Default | Description |
|---|---|---|---|---|
| `Agent:PortalUrl` | `PORTAL_URL` | Yes | — | Base URL of the portal |
| `Agent:AgentApiKey` | `AGENT_API_KEY` | Yes | — | API key for authentication |
| `Agent:AzureStorageConnectionString` | `AZURE_STORAGE_CONNECTION_STRING` | Yes | — | Azure Blob Storage connection string |
| `Agent:PollIntervalMs` | `POLL_INTERVAL_MS` | | `30000` | Poll interval in ms |
| `Agent:HeartbeatIntervalMs` | `HEARTBEAT_INTERVAL_MS` | | `30000` | Heartbeat interval in ms |

### Local connection strings

Connection strings are stored locally and referenced by name:

```json
// Environment variables
CONN_PATHWAY_DB=Server=192.168.1.50;Database=Pathway;User Id=readonly;Password=xxx;Encrypt=false;
CONN_MERIT_DB=Server=192.168.1.51;Database=Merit;User Id=readonly;Password=xxx;Encrypt=false;
```

The portal sends job configs with `connectionRef: "pathway-db"` instead of the actual connection string.

## Tests

```bash
cd dotnet/CrmAgent.Tests
dotnet test
```

## Commands

| Task | Command |
|------|---------|
| Build | `dotnet build` |
| Run (dev) | `dotnet run` |
| Test | `dotnet test` |
| Publish (self-contained) | `dotnet publish -c Release -r win-x64 --self-contained -o publish` |
