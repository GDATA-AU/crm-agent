# crm-agent

A lightweight extraction agent that runs as a Windows service inside council networks, polls the LGA Customer Portal for jobs, executes them locally, and writes results to Azure Blob Storage.

## Overview

`crm-agent` is the extraction component in the council ERP integration pipeline.

**Pipeline:**
```
crm-agent (extracts from on-prem DB or external API) → Azure Blob Storage → Portal (diffs, upserts to Postgres)
```

The agent is **intentionally dumb** — it has no knowledge of the citizen schema, deduplication logic, or business rules. It:

1. Polls the portal's REST API for a pending job
2. Executes the job (SQL query or REST API extraction) using streaming
3. Computes a SHA-256 `_rowHash` for each row (for change detection)
4. Writes gzip-compressed NDJSON to Azure Blob Storage
5. Reports completion (or failure) back to the portal

All business logic remains in the portal. The agent only interprets structured configuration objects for known job types — it does **not** execute arbitrary code from job configs.

## Tech stack

- **.NET 10** Worker Service
- **Microsoft.Data.SqlClient** — SQL Server (Windows Integrated Security)
- **Azure.Storage.Blobs** — blob upload
- **Serilog** — structured JSON logging
- **Microsoft.Extensions.Hosting.WindowsServices** — native Windows service support

## Prerequisites

- **Windows Server 2016+** (or Windows 10+ for development)
- Network access **outbound** to:
  - The portal URL (HTTPS, typically port 443)
  - Azure Blob Storage endpoint (HTTPS, port 443)
- Network access to local databases (configured by the council IT team)
- An Azure Blob Storage account with an `erp-imports` container

No inbound firewall rules are required. All communication is initiated by the agent.

## Installation

### 1. Download or build the agent

Option A — build from source:

```bash
git clone https://github.com/GDATA-AU/crm-agent.git
cd crm-agent/dotnet/CrmAgent
dotnet publish -c Release -r win-x64 --self-contained -o publish
```

Option B — download the pre-built release from GitHub Releases.

The published output is a single `CrmAgent.exe` with no external dependencies.

### 2. Configure

Edit `appsettings.json` in the publish folder:

```json
{
  "Agent": {
    "PortalUrl": "https://council.lga-portal.com.au",
    "AgentApiKey": "your-api-key",
    "AzureStorageConnectionString": "DefaultEndpointsProtocol=https;AccountName=..."
  }
}
```

Alternatively, set environment variables (`PORTAL_URL`, `AGENT_API_KEY`, `AZURE_STORAGE_CONNECTION_STRING`). Environment variables take precedence over `appsettings.json`.

See [Configuration](#configuration) for all available settings.

### 3. Install as a Windows service (run as Administrator)

```powershell
install-service.bat
```

To uninstall:
```powershell
install-service.bat --uninstall
```

### 4. Verify the agent is running

```powershell
sc query crm-agent
```

Check **Event Viewer → Windows Logs → Application** for structured JSON log output. The agent logs every poll cycle and job execution.

## Configuration

Configuration is via `appsettings.json` or environment variables. Environment variables take precedence.

| Setting | Env var | Required | Default | Description |
|---|---|---|---|---|
| `Agent:PortalUrl` | `PORTAL_URL` | ✅ | — | Base URL of the LGA Customer Portal |
| `Agent:AgentApiKey` | `AGENT_API_KEY` | ✅ | — | Long-lived API key provisioned when the agent is set up |
| `Agent:AzureStorageConnectionString` | `AZURE_STORAGE_CONNECTION_STRING` | ✅ | — | Azure Blob Storage connection string |
| `Agent:PollIntervalMs` | `POLL_INTERVAL_MS` | | `30000` | How often to poll the portal for jobs (ms) |
| `Agent:HeartbeatIntervalMs` | `HEARTBEAT_INTERVAL_MS` | | `30000` | How often to send a heartbeat during a job (ms) |

Serilog log levels can be configured in the `Serilog` section of `appsettings.json`.

### SQL connection strings

No local connection string configuration is needed. The portal sends `server` and `database` with each SQL job. The agent builds the connection string locally using **Windows Integrated Security** — it always authenticates as the Windows service account. SQL User Id/Password credentials are never accepted.

The service account must have **read-only** (`db_datareader`) access to the target databases. See [Setting up read-only database access](#setting-up-read-only-database-access) below.

## Setting up read-only database access

The agent's database access **must be read-only**. It only needs `SELECT` permission — it never writes to source databases.

The agent authenticates as the Windows service account. Grant read-only access to that account on each target database:

```sql
-- Switch to the target database
USE Pathway;

-- Create a user mapped to the service account (domain\account)
CREATE USER [DOMAIN\crm-agent-svc] FOR LOGIN [DOMAIN\crm-agent-svc];

-- Grant read-only access
EXEC sp_addrolemember 'db_datareader', 'DOMAIN\crm-agent-svc';
```

## Development

The source is in `dotnet/CrmAgent/`. To work on the agent locally:

```bash
cd dotnet/CrmAgent
dotnet run
```

| Task | Command |
|------|---------|
| Build | `dotnet build` |
| Run (dev) | `dotnet run` |
| Test | `cd ../CrmAgent.Tests && dotnet test` |
| Publish (self-contained) | `dotnet publish -c Release -r win-x64 --self-contained -o publish` |

## Verifying the agent is working

1. **Check logs** — the agent logs every poll cycle. Look for `"No job available"` (normal when idle) or `"Job received"` / `"Job completed"` messages.

2. **Check the portal UI** — the portal shows the last heartbeat time and job status for each registered agent.

3. **Check Azure Blob Storage** — after a successful job, a `.ndjson.gz` file will appear in the `erp-imports` container at the configured blob path.

## Troubleshooting

### Agent can't reach the portal

- Check that `PORTAL_URL` is correct and reachable from the agent host
- Verify that port 443 is open outbound on the host firewall and any network firewalls
- Confirm the agent host can resolve the portal's DNS name: `nslookup your-council.lga-portal.com.au`

### Agent can't connect to the database

- Verify the service account running the agent has `db_datareader` access to the target database
- Check that TCP/IP is enabled in SQL Server Configuration Manager
- Confirm the server name in the portal matches the actual SQL Server hostname/instance
- Check that the database server allows connections from the agent host's IP

### Blob upload fails

- Verify `AZURE_STORAGE_CONNECTION_STRING` is correct
- Check that the `erp-imports` container exists in the storage account
- Confirm the storage account allows connections from the agent host (no IP firewall restrictions)

### Agent keeps reporting jobs as failed

- Check the agent logs for the specific error message
- For database errors: test the connection string manually using a SQL client tool
- For REST API errors: verify the API endpoint and credentials in the job config

## Updating the agent

```bash
cd /path/to/crm-agent
git pull origin main

# Build and test
dotnet build
dotnet test dotnet/CrmAgent.Tests

# Publish and restart service
dotnet publish dotnet/CrmAgent -c Release -r win-x64 --self-contained -o publish

# Linux:
sudo systemctl restart crm-agent

# Windows (as Administrator):
publish\install-service.bat --uninstall
publish\install-service.bat
```

## Architecture

```
dotnet/
  CrmAgent/
    Program.cs                  Entry point, DI wiring, Windows Service support
    AgentConfig.cs              Config from appsettings.json + env vars
    AgentWorker.cs              BackgroundService main poll loop
    Models/Job.cs               Shared job types and status models
    Services/                   Portal/blob/hash/NDJSON services
    Handlers/                   SQL and REST extraction handlers
  CrmAgent.Tests/               xUnit tests
  CrmAgent.Tray/                Windows tray app for setup and status
```

## Blob output format

Blobs are written to:
```
erp-imports/{integrationCode}/{tableName}/snapshots/{timestamp}.ndjson.gz
```

Each file is gzip-compressed NDJSON (one JSON object per line). Every row includes all source columns as-is plus a `_rowHash` field — a SHA-256 hex digest of selected field values (pipe-delimited, trimmed) used by the portal for change detection.

## Security

- All communication with the portal is over HTTPS with certificate validation enabled
- The agent API key is stored in the environment, not in code
- **SQL connections use Windows Integrated Security only** — the agent authenticates as the service account. SQL usernames and passwords are never accepted
- The portal sends only server name and database name for SQL jobs — no credentials transit the network
- The agent's database access must be read-only (see [Setting up read-only database access](#setting-up-read-only-database-access))
- The SQL handler rejects any query that doesn't start with `SELECT` or `WITH`
- The agent only interprets structured configuration objects for known job types — it does **not** eval or execute arbitrary code
