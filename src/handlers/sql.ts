import { createGzip } from "node:zlib";
import { PassThrough } from "node:stream";
import { resolveConnectionString } from "../config.js";
import { computeRowHash } from "../hash.js";
import { buildBlobName, uploadStreamToBlob } from "../blob.js";
import logger from "../logger.js";
import type {
  Job,
  SqlJobConfig,
  JobHandler,
  HandlerResult,
  ProgressCallback,
} from "../types.js";

async function executeMssql(
  connectionString: string,
  query: string,
  hashFields: string[],
  blobName: string,
  onProgress: ProgressCallback
): Promise<number> {
  const mssql = await import("mssql");

  const pool = await mssql.default.connect(connectionString);
  try {
    const request = pool.request();
    request.stream = true;

    const passThrough = new PassThrough();
    const gzip = createGzip();
    passThrough.pipe(gzip);

    const uploadPromise = uploadStreamToBlob(blobName, gzip);

    let processedRows = 0;

    await new Promise<void>((resolve, reject) => {
      request.on("row", (row: Record<string, unknown>) => {
        const rowWithHash = {
          ...row,
          _rowHash: computeRowHash(row, hashFields),
        };
        const line = JSON.stringify(rowWithHash) + "\n";
        passThrough.write(line, "utf8");

        processedRows++;
        if (processedRows % 1000 === 0) {
          onProgress({ processedRows, message: `Processing row ${processedRows}...` });
        }
      });

      request.on("error", reject);
      request.on("done", () => {
        passThrough.end();
        resolve();
      });

      request.query(query);
    });

    await uploadPromise;
    return processedRows;
  } finally {
    await pool.close();
  }
}

async function executePostgres(
  connectionString: string,
  query: string,
  hashFields: string[],
  blobName: string,
  onProgress: ProgressCallback
): Promise<number> {
  const { default: pg } = await import("pg");
  const { default: Cursor } = await import("pg-cursor");

  const client = new pg.Client(connectionString);
  await client.connect();

  try {
    const passThrough = new PassThrough();
    const gzip = createGzip();
    passThrough.pipe(gzip);

    const uploadPromise = uploadStreamToBlob(blobName, gzip);

    let processedRows = 0;
    const cursor = client.query(new Cursor(query));

    const BATCH = 1000;

    async function readBatch(): Promise<void> {
      const rows = await cursor.read(BATCH) as Record<string, unknown>[];
      if (rows.length === 0) {
        passThrough.end();
        return;
      }
      for (const row of rows) {
        const rowWithHash = {
          ...row,
          _rowHash: computeRowHash(row, hashFields),
        };
        passThrough.write(JSON.stringify(rowWithHash) + "\n", "utf8");
        processedRows++;
      }
      onProgress({ processedRows, message: `Processing row ${processedRows}...` });
      return readBatch();
    }

    await readBatch();
    await cursor.close();
    await uploadPromise;
    return processedRows;
  } finally {
    await client.end();
  }
}

async function executeMysql(
  connectionString: string,
  query: string,
  hashFields: string[],
  blobName: string,
  onProgress: ProgressCallback
): Promise<number> {
  const mysql = await import("mysql2");

  // Parse connection string — mysql2 accepts both DSN and config objects.
  // We use a simple connection wrapping the raw string.
  const connection = mysql.default.createConnection(connectionString);

  const passThrough = new PassThrough();
  const gzip = createGzip();
  passThrough.pipe(gzip);

  const uploadPromise = uploadStreamToBlob(blobName, gzip);

  let processedRows = 0;

  await new Promise<void>((resolve, reject) => {
    connection.connect((err) => {
      if (err) return reject(err);

      const q = connection.query(query);

      (q as unknown as NodeJS.EventEmitter).on(
        "result",
        (row: Record<string, unknown>) => {
          const rowWithHash = {
            ...row,
            _rowHash: computeRowHash(row, hashFields),
          };
          passThrough.write(JSON.stringify(rowWithHash) + "\n", "utf8");
          processedRows++;
          if (processedRows % 1000 === 0) {
            onProgress({ processedRows, message: `Processing row ${processedRows}...` });
          }
        }
      );

      (q as unknown as NodeJS.EventEmitter).on("error", (err: Error) => {
        passThrough.destroy(err);
        reject(err);
      });

      (q as unknown as NodeJS.EventEmitter).on("end", () => {
        passThrough.end();
        resolve();
      });
    });
  });

  connection.destroy();
  await uploadPromise;
  return processedRows;
}

export const sqlHandler: JobHandler = {
  async execute(job: Job, onProgress: ProgressCallback): Promise<HandlerResult> {
    const config = job.config as SqlJobConfig;
    const connectionString = resolveConnectionString(
      config.connectionRef,
      config.connectionString
    );

    const timestamp = new Date();
    const blobName = buildBlobName(job.blobPath, timestamp);

    logger.info({ jobId: job.id, driver: config.driver, blobName }, "Starting SQL extraction");

    let processedRows: number;

    switch (config.driver) {
      case "mssql":
        processedRows = await executeMssql(
          connectionString,
          config.query,
          job.hashFields,
          blobName,
          onProgress
        );
        break;

      case "postgres":
        processedRows = await executePostgres(
          connectionString,
          config.query,
          job.hashFields,
          blobName,
          onProgress
        );
        break;

      case "mysql":
        processedRows = await executeMysql(
          connectionString,
          config.query,
          job.hashFields,
          blobName,
          onProgress
        );
        break;

      default: {
        const exhaustive: never = config.driver;
        throw new Error(`Unsupported database driver: ${String(exhaustive)}`);
      }
    }

    logger.info({ jobId: job.id, processedRows, blobName }, "SQL extraction complete");
    return { blobName, processedRows };
  },
};
