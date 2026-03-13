import { createGzip } from "node:zlib";
import { PassThrough } from "node:stream";
import { computeRowHash } from "../hash.js";
import { buildBlobName, uploadStreamToBlob } from "../blob.js";
import logger from "../logger.js";
import type {
  Job,
  RestApiJobConfig,
  RestApiAuth,
  RestApiPagination,
  JobHandler,
  HandlerResult,
  ProgressCallback,
} from "../types.js";

/**
 * Resolve an OAuth2 client-credentials token.
 */
async function fetchOAuth2Token(auth: RestApiAuth): Promise<string> {
  if (!auth.tokenUrl || !auth.clientId || !auth.clientSecret) {
    throw new Error("OAuth2 auth requires tokenUrl, clientId, and clientSecret");
  }

  const body = new URLSearchParams({
    grant_type: "client_credentials",
    client_id: auth.clientId,
    client_secret: auth.clientSecret,
    ...(auth.scope ? { scope: auth.scope } : {}),
  });

  const res = await fetch(auth.tokenUrl, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: body.toString(),
  });

  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new Error(`OAuth2 token request failed: ${res.status} — ${text}`);
  }

  const data = (await res.json()) as { access_token: string };
  return data.access_token;
}

/**
 * Resolve the bearer token from the auth config.
 */
async function resolveToken(auth?: RestApiAuth): Promise<string | undefined> {
  if (!auth) return undefined;
  if (auth.type === "bearer") return auth.token;
  if (auth.type === "oauth2-client-credentials") return fetchOAuth2Token(auth);
  return undefined;
}

/**
 * Resolve a dot-notation path into a JSON value.
 * e.g. "data.results" on `{ data: { results: [...] } }` returns the array.
 */
function getNestedValue(obj: unknown, path: string): unknown {
  return path.split(".").reduce<unknown>((acc, key) => {
    if (acc != null && typeof acc === "object") {
      return (acc as Record<string, unknown>)[key];
    }
    return undefined;
  }, obj);
}

interface PageResult {
  body: unknown;
  headers: Headers;
}

/**
 * Fetch a single page from the API.
 */
async function fetchPage(
  url: string,
  method: "GET" | "POST",
  extraHeaders: Record<string, string>
): Promise<PageResult> {
  const res = await fetch(url, { method, headers: extraHeaders });
  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new Error(`REST API request failed: ${res.status} ${res.statusText} — ${text}`);
  }
  return { body: await res.json(), headers: res.headers };
}

/**
 * Extract the Link: <url>; rel="next" header value, if present.
 */
function parseLinkHeaderNext(headers: Headers): string | null {
  const link = headers.get("link");
  if (!link) return null;
  const match = link.match(/<([^>]+)>;\s*rel="next"/);
  return match ? match[1] : null;
}

export const restApiHandler: JobHandler = {
  async execute(job: Job, onProgress: ProgressCallback): Promise<HandlerResult> {
    const config = job.config as RestApiJobConfig;

    const token = await resolveToken(config.auth);

    const authHeaders: Record<string, string> = token
      ? { Authorization: `Bearer ${token}` }
      : {};

    const baseHeaders: Record<string, string> = {
      ...(config.headers ?? {}),
      ...authHeaders,
    };

    const timestamp = new Date();
    const blobName = buildBlobName(job.blobPath, timestamp);

    logger.info({ jobId: job.id, baseUrl: config.baseUrl, blobName }, "Starting REST API extraction");

    const passThrough = new PassThrough();
    const gzip = createGzip();
    passThrough.pipe(gzip);

    const uploadPromise = uploadStreamToBlob(blobName, gzip);

    let processedRows = 0;
    const pagination = config.pagination;

    if (!pagination) {
      // Single-page response
      const { body: data } = await fetchPage(config.baseUrl, config.method, baseHeaders);
      const records = Array.isArray(data) ? data : [data];
      for (const record of records as Record<string, unknown>[]) {
        const rowWithHash = {
          ...record,
          _rowHash: computeRowHash(record, job.hashFields),
        };
        passThrough.write(JSON.stringify(rowWithHash) + "\n", "utf8");
        processedRows++;
      }
      passThrough.end();
    } else if (pagination.type === "link-header") {
      let nextUrl: string | null = config.baseUrl;
      while (nextUrl) {
        const { body: data, headers } = await fetchPage(nextUrl, config.method, baseHeaders);
        const records = pagination.dataField
          ? (getNestedValue(data, pagination.dataField) as Record<string, unknown>[])
          : (data as Record<string, unknown>[]);

        for (const record of records) {
          const rowWithHash = {
            ...record,
            _rowHash: computeRowHash(record, job.hashFields),
          };
          passThrough.write(JSON.stringify(rowWithHash) + "\n", "utf8");
          processedRows++;
        }

        onProgress({ processedRows, message: `Processed ${processedRows} records...` });
        nextUrl = parseLinkHeaderNext(headers);
      }
      passThrough.end();
    } else if (pagination.type === "offset") {
      const pageSize = pagination.pageSize ?? 100;
      const pageParam = pagination.pageParam ?? "page";
      const pageSizeParam = pagination.pageSizeParam ?? "pageSize";
      let page = 0;

      while (true) {
        const separator = config.baseUrl.includes("?") ? "&" : "?";
        const url = `${config.baseUrl}${separator}${pageParam}=${page}&${pageSizeParam}=${pageSize}`;
        const { body: data } = await fetchPage(url, config.method, baseHeaders);

        const records = pagination.dataField
          ? (getNestedValue(data, pagination.dataField) as Record<string, unknown>[])
          : (data as Record<string, unknown>[]);

        if (!records || records.length === 0) break;

        for (const record of records) {
          const rowWithHash = {
            ...record,
            _rowHash: computeRowHash(record, job.hashFields),
          };
          passThrough.write(JSON.stringify(rowWithHash) + "\n", "utf8");
          processedRows++;
        }

        onProgress({ processedRows, message: `Processed ${processedRows} records...` });

        if (records.length < pageSize) break;
        page++;
      }
      passThrough.end();
    } else if (pagination.type === "cursor") {
      const pageSizeParam = pagination.pageSizeParam ?? "pageSize";
      const pageSize = pagination.pageSize ?? 100;
      const cursorField = pagination.cursorField ?? "nextCursor";
      const pageParam = pagination.pageParam ?? "cursor";

      let cursor: string | null = null;

      while (true) {
        let url = config.baseUrl;
        const params = new URLSearchParams({ [pageSizeParam]: String(pageSize) });
        if (cursor) params.set(pageParam, cursor);
        const separator = url.includes("?") ? "&" : "?";
        url = `${url}${separator}${params.toString()}`;

        const { body: data } = await fetchPage(url, config.method, baseHeaders);

        const records = pagination.dataField
          ? (getNestedValue(data, pagination.dataField) as Record<string, unknown>[])
          : (data as Record<string, unknown>[]);

        if (!records || records.length === 0) break;

        for (const record of records) {
          const rowWithHash = {
            ...record,
            _rowHash: computeRowHash(record, job.hashFields),
          };
          passThrough.write(JSON.stringify(rowWithHash) + "\n", "utf8");
          processedRows++;
        }

        onProgress({ processedRows, message: `Processed ${processedRows} records...` });

        const nextCursor = getNestedValue(data, cursorField);
        if (!nextCursor) break;
        cursor = String(nextCursor);
      }
      passThrough.end();
    } else {
      passThrough.end();
    }

    await uploadPromise;

    logger.info({ jobId: job.id, processedRows, blobName }, "REST API extraction complete");
    return { blobName, processedRows };
  },
};
