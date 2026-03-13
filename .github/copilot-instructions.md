# Copilot Instructions — crm-agent

## What this project is

A lightweight extraction agent that polls the LGA Customer Portal for jobs, executes SQL queries or REST API calls on-prem, and streams gzipped NDJSON results to Azure Blob Storage.

## Tech stack

- **Runtime:** Node.js 20+, ESM (`"type": "module"`)
- **Language:** TypeScript 5.9, strict mode, target ES2022, module Node16
- **Build:** tsup → `dist/`
- **Test:** vitest
- **Logging:** pino (structured JSON)
- **DB drivers:** mssql, pg (with pg-cursor), mysql2
- **Cloud:** @azure/storage-blob

## Project layout

```
src/
  index.ts          # Entry point, signal handling, starts loop
  loop.ts           # Poll → execute → report lifecycle
  portal.ts         # HTTP client for the LGA Customer Portal API
  blob.ts           # Azure Blob upload helpers
  ndjson.ts         # NDJSON Transform stream utilities
  hash.ts           # SHA-256 row hashing for change detection
  config.ts         # dotenv config, resolves connection refs
  logger.ts         # Proxy-based pino singleton
  types.ts          # All shared interfaces (JobHandler, Job, JobConfig, etc.)
  handlers/
    index.ts        # Handler factory (resolves job type → handler)
    sql.ts          # SQL handler (MSSQL, Postgres, MySQL via streaming cursors)
    rest-api.ts     # REST API handler (offset/cursor/link-header pagination, Bearer/OAuth2)
  __tests__/
    hash.test.ts
    ndjson.test.ts
```

## Conventions

- **Files:** kebab-case (`rest-api.ts`).
- **Symbols:** camelCase for variables/functions, PascalCase for types/interfaces.
- **Streaming:** Use Node.js streams (Transform, PassThrough, gzip) to keep memory low. Never buffer full datasets.
- **Error handling:** Fail fast on config errors at startup. The polling loop is resilient — it retries on transient failures. Handlers should throw on unrecoverable errors.
- **Graceful shutdown:** The agent handles SIGTERM/SIGINT by finishing the current job before exiting. Respect this pattern.
- **Connection strings:** SQL jobs resolve connection strings via `connectionRef` → env var `CONN_<NAME>` (uppercased, hyphens → underscores). Direct `connectionString` is a fallback.
- **Handler pattern:** Handlers implement the `JobHandler` interface from `types.ts`. Register new handlers in `handlers/index.ts`.

## Commands

| Task | Command |
|------|---------|
| Build | `npm run build` |
| Dev | `npm run dev` |
| Test | `npm run test` |
| Type-check | `npm run typecheck` |

## Environment variables

Required: `PORTAL_URL`, `AGENT_API_KEY`, `AZURE_STORAGE_CONNECTION_STRING`.
SQL connection refs: `CONN_<NAME>` (e.g. `CONN_MY_DB`).
