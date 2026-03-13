/**
 * Shared TypeScript interfaces for crm-agent.
 */

// ---------------------------------------------------------------------------
// Job types returned by the portal poll endpoint
// ---------------------------------------------------------------------------

export type JobType = "sql" | "rest-api";
export type JobStatus = "running" | "completed" | "failed";

export interface SqlJobConfig {
  type: "sql";
  /** Database driver to use. */
  driver: "mssql" | "postgres" | "mysql";
  /**
   * Direct connection string (used in development / when `connectionRef` is
   * absent).  In production, prefer `connectionRef` so that the connection
   * string never transits the network.
   */
  connectionString?: string;
  /**
   * References a locally-stored connection string by name.  The agent looks
   * up the value from the environment variable `CONN_<NAME>` (upper-cased,
   * hyphens replaced with underscores).  Takes precedence over
   * `connectionString` when both are supplied.
   */
  connectionRef?: string;
  /** SELECT query to execute.  The agent DB user must be READ-ONLY. */
  query: string;
  /** Blob path prefix, e.g. "pathway/names-individuals/snapshots/" */
  blobPath: string;
  /** Column names whose values are concatenated to produce the row hash. */
  hashFields: string[];
}

export interface RestApiPagination {
  type: "offset" | "cursor" | "link-header";
  /** Query-parameter name for the page/offset value. */
  pageParam?: string;
  /** Query-parameter name for the page size. */
  pageSizeParam?: string;
  pageSize?: number;
  /** JSON-path (dot-notation) to the cursor value inside the response body. */
  cursorField?: string;
  /** JSON-path (dot-notation) to the array of records inside the response body. */
  dataField?: string;
  /** JSON-path (dot-notation) to the total count inside the response body (optional). */
  totalField?: string;
}

export interface RestApiAuth {
  type: "bearer" | "oauth2-client-credentials";
  /** Static bearer token (used when `type === "bearer"`). */
  token?: string;
  /** OAuth2 token endpoint (used when `type === "oauth2-client-credentials"`). */
  tokenUrl?: string;
  clientId?: string;
  clientSecret?: string;
  scope?: string;
}

export interface RestApiJobConfig {
  type: "rest-api";
  baseUrl: string;
  method: "GET" | "POST";
  headers?: Record<string, string>;
  auth?: RestApiAuth;
  pagination?: RestApiPagination;
  /** Blob path prefix, e.g. "pathway/names-individuals/snapshots/" */
  blobPath: string;
  /** Field names whose values are concatenated to produce the row hash. */
  hashFields: string[];
}

export type JobConfig = SqlJobConfig | RestApiJobConfig;

export interface Job {
  id: string;
  type: JobType;
  config: JobConfig;
  /** Blob path prefix supplied by the portal for convenience. */
  blobPath: string;
  /** Field names to include in the row hash. */
  hashFields: string[];
}

// ---------------------------------------------------------------------------
// Status reporting
// ---------------------------------------------------------------------------

export interface JobProgress {
  processedRows: number;
  totalRows?: number;
  message?: string;
}

export interface JobStatusUpdate {
  status: JobStatus;
  progress?: JobProgress;
  /** Set on `failed` status. */
  error?: string;
  /** Set on `completed` status — full blob name including container prefix. */
  blobName?: string;
}

// ---------------------------------------------------------------------------
// Handler interface
// ---------------------------------------------------------------------------

export interface HandlerResult {
  blobName: string;
  processedRows: number;
}

export type ProgressCallback = (progress: JobProgress) => void;

export interface JobHandler {
  execute(job: Job, onProgress: ProgressCallback): Promise<HandlerResult>;
}
