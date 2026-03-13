# Architecture — crm-agent

## System overview

```
┌──────────────────────┐         HTTPS          ┌──────────────────────┐
│   LGA Customer       │◄──────────────────────►│   crm-agent          │
│   Portal (cloud)     │   poll / status / hb    │   (on-prem Windows   │
│                      │                         │    service)          │
└──────────┬───────────┘                         └──┬──────────┬───────┘
           │                                        │          │
           │  reads blobs                           │          │ reads data
           ▼                                        ▼          ▼
┌──────────────────────┐               ┌────────────────────────────────┐
│   Azure Blob Storage │◄──────────────│   On-prem databases / APIs    │
│   (erp-imports)      │  uploads gz   │   (MSSQL, REST endpoints)     │
│                      │  NDJSON       │                               │
└──────────────────────┘               └────────────────────────────────┘
```

**Direction of data flow:** The agent **pulls** job instructions from the portal, **reads** data from local sources, and **pushes** results to Azure Blob Storage. All communication is outbound from the agent — no inbound ports are required.

## Components

### Portal (external)

The LGA Customer Portal is a cloud-hosted application that:

- Defines extraction jobs (SQL queries, REST API configs)
- Queues jobs for agents to pick up
- Receives status updates and heartbeats from agents
- Reads uploaded blobs, diffs them, and upserts changes to its Postgres database

The agent has no knowledge of the portal's internal schema or business logic.

### crm-agent (this repo)

A .NET 10 Worker Service deployed as a Windows service. Its responsibilities are narrow:

1. **Poll** the portal for a pending job
2. **Execute** the job (stream SQL results or paginate a REST API)
3. **Hash** each row with SHA-256 for change detection
4. **Compress** results as gzip NDJSON
5. **Upload** to Azure Blob Storage
6. **Report** completion or failure back to the portal

### CrmAgent.Tray (system tray app)

A Windows Forms app that provides a UI for:

- Connecting the agent to the portal (entering portal URL and API key)
- Viewing agent status
- Managing the Windows service (start/stop)

Configuration is written to a shared `appsettings.json` that the agent reads.

## Job lifecycle

```
Portal                          Agent                           Blob Storage
  │                               │                                │
  │  POST /poll ◄─────────────────│                                │
  │  ─────────────────────────►   │                                │
  │  { job: { id, type, config }} │                                │
  │                               │                                │
  │                               │── open DB connection ──►       │
  │                               │   or HTTP request              │
  │                               │                                │
  │  PATCH /jobs/:id/status ◄─────│  (heartbeat every 30s)         │
  │  { status: running,           │                                │
  │    progress: { rows: 5000 }}  │                                │
  │                               │                                │
  │                               │── stream rows ──►              │
  │                               │   hash + compress + write      │
  │                               │                                │
  │                               │────── upload .ndjson.gz ──────►│
  │                               │                                │
  │  PATCH /jobs/:id/status ◄─────│                                │
  │  { status: completed,         │                                │
  │    blobName: "..." }          │                                │
  │                               │                                │
```

## SQL connection model

```
Portal sends:           { server: "SQLPROD01", database: "Pathway", query: "SELECT ..." }
                              │                   │
Agent builds:                 ▼                   ▼
  SqlConnectionStringBuilder { DataSource = "SQLPROD01", InitialCatalog = "Pathway", IntegratedSecurity = true }
```

- The portal provides only the **server name** and **database name** — no credentials.
- The agent always uses **Windows Integrated Security**, authenticating as the service account.
- SQL usernames and passwords are **never accepted** — the code rejects them.
- The service account must have `db_datareader` access on the target databases.

## REST API connection model

```
Portal sends:           { baseUrl: "https://api.vendor.com/v1/records",
                          method: "GET",
                          auth: { type: "bearer", token: "..." },
                          pagination: { type: "offset", pageSize: 100 } }
```

The portal provides the full API configuration including auth tokens. The agent makes HTTP requests, follows pagination, and streams results.

## Security model

| Concern | Approach |
|---|---|
| **SQL credentials** | Never used. Windows Integrated Security only (service account). |
| **Azure Storage credentials** | Stored locally on the agent (`appsettings.json` or env var). |
| **Agent ↔ Portal auth** | API key in `Authorization` header over HTTPS. |
| **SQL injection prevention** | Agent rejects queries that don't start with `SELECT` or `WITH`. |
| **Data in transit** | All external communication is HTTPS (TLS). |
| **No arbitrary code execution** | Agent only interprets structured job configs for known job types. |
| **Network posture** | All connections are outbound. No inbound ports required. |

## Data flow: blob output

```
erp-imports/{integrationCode}/{tableName}/snapshots/{timestamp}.ndjson.gz
```

Each blob contains gzip-compressed NDJSON (one JSON object per line). Every row includes:

- All source columns as-is
- `_rowHash` — a SHA-256 hex digest of selected field values (pipe-delimited, trimmed) used by the portal for change detection

## Project structure

```
dotnet/
  CrmAgent/                       # The agent (Windows service)
    Program.cs                    #   Entry point, DI wiring
    AgentConfig.cs                #   Configuration
    AgentWorker.cs                #   BackgroundService — the main poll loop
    Models/
      Job.cs                      #   All shared types (Job, JobConfig, enums, etc.)
    Services/
      PortalClient.cs             #   Typed HttpClient for the portal API
      BlobStorageService.cs       #   Azure Blob upload helpers
      HashService.cs              #   SHA-256 row hashing
      NdjsonGzipWriter.cs         #   NDJSON + GZip streaming
    Handlers/
      IJobHandler.cs              #   Handler interface
      HandlerFactory.cs           #   Resolves job type → handler via DI
      SqlHandler.cs               #   SQL handler (MSSQL, Windows Integrated Security)
      RestApiHandler.cs           #   REST API handler (pagination, auth)
  CrmAgent.Tests/                 # Unit tests (xUnit)
  CrmAgent.Tray/                  # System tray UI (Windows Forms)
```

## Key design decisions

1. **Agent is intentionally dumb.** It has no knowledge of the citizen schema, deduplication logic, or business rules. All intelligence lives in the portal.

2. **Streaming, not buffering.** SQL results are streamed via `DbDataReader`, compressed on-the-fly with `GZipStream`, and uploaded to blob storage. The agent never holds a full dataset in memory.

3. **MSSQL uses Windows auth exclusively.** The portal sends server + database names (non-secret routing info). The agent builds the connection using Integrated Security. This eliminates credential management entirely for SQL jobs.

4. **Heartbeat during long jobs.** The agent sends periodic progress updates to the portal so it can detect stalled agents and show progress in the UI.

5. **Graceful shutdown.** The Windows service host propagates cancellation. The agent finishes the current job before exiting — it doesn't abandon work mid-stream.
