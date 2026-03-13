import "dotenv/config";

function required(name: string): string {
  const val = process.env[name];
  if (!val) {
    throw new Error(`Missing required environment variable: ${name}`);
  }
  return val;
}

function optional(name: string, defaultValue: string): string {
  return process.env[name] ?? defaultValue;
}

function optionalInt(name: string, defaultValue: number): number {
  const raw = process.env[name];
  if (raw === undefined || raw === "") return defaultValue;
  const parsed = parseInt(raw, 10);
  if (isNaN(parsed)) {
    throw new Error(`Environment variable ${name} must be an integer, got: ${raw}`);
  }
  return parsed;
}

export interface Config {
  portalUrl: string;
  agentApiKey: string;
  azureStorageConnectionString: string;
  pollIntervalMs: number;
  heartbeatIntervalMs: number;
  logLevel: string;
}

let _config: Config | undefined;

export function getConfig(): Config {
  if (_config) return _config;

  _config = {
    portalUrl: required("PORTAL_URL").replace(/\/$/, ""),
    agentApiKey: required("AGENT_API_KEY"),
    azureStorageConnectionString: required("AZURE_STORAGE_CONNECTION_STRING"),
    pollIntervalMs: optionalInt("POLL_INTERVAL_MS", 30_000),
    heartbeatIntervalMs: optionalInt("HEARTBEAT_INTERVAL_MS", 30_000),
    logLevel: optional("LOG_LEVEL", "info"),
  };

  return _config;
}

/**
 * Resolve a connection string from either a `connectionRef` (environment
 * variable lookup) or a direct `connectionString` value.
 *
 * Resolution order:
 * 1. If `connectionRef` is provided, look up `CONN_<REF>` in the environment
 *    (upper-cased, hyphens and spaces replaced with underscores).
 * 2. Fall back to `connectionString`.
 * 3. Throw if neither is available.
 */
export function resolveConnectionString(
  connectionRef?: string,
  connectionString?: string
): string {
  if (connectionRef) {
    const envKey = `CONN_${connectionRef.toUpperCase().replace(/[-\s]/g, "_")}`;
    const val = process.env[envKey];
    if (val) return val;
    throw new Error(
      `connectionRef "${connectionRef}" maps to env var "${envKey}" which is not set`
    );
  }
  if (connectionString) return connectionString;
  throw new Error("Job config must include either connectionRef or connectionString");
}
