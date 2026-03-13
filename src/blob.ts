import { BlobServiceClient, BlockBlobClient } from "@azure/storage-blob";
import { Readable } from "node:stream";
import { getConfig } from "./config.js";

const CONTAINER_NAME = "erp-imports";

let _client: BlobServiceClient | undefined;

function getBlobServiceClient(): BlobServiceClient {
  if (!_client) {
    _client = BlobServiceClient.fromConnectionString(
      getConfig().azureStorageConnectionString
    );
  }
  return _client;
}

/**
 * Returns a `BlockBlobClient` for the given blob name inside the
 * `erp-imports` container.
 *
 * @param blobName - path relative to the container root, e.g.
 *   "pathway/names-individuals/snapshots/2026-03-13T02-30-00Z.ndjson.gz"
 */
export function getBlobClient(blobName: string): BlockBlobClient {
  const serviceClient = getBlobServiceClient();
  const containerClient = serviceClient.getContainerClient(CONTAINER_NAME);
  return containerClient.getBlockBlobClient(blobName);
}

/**
 * Upload a readable stream to blob storage.
 *
 * @param blobName   - path inside the container
 * @param stream     - compressed NDJSON readable stream
 * @param bufferSize - internal buffer size in bytes (default 4 MB)
 * @param maxConcurrency - max parallel upload threads (default 4)
 */
export async function uploadStreamToBlob(
  blobName: string,
  stream: Readable,
  bufferSize = 4 * 1024 * 1024,
  maxConcurrency = 4
): Promise<void> {
  const blobClient = getBlobClient(blobName);
  await blobClient.uploadStream(stream, bufferSize, maxConcurrency, {
    blobHTTPHeaders: { blobContentType: "application/gzip" },
  });
}

/**
 * Build the full blob name from a path prefix and a timestamp.
 *
 * @param blobPath  - prefix from the job config, e.g.
 *   "pathway/names-individuals/snapshots/"
 * @param timestamp - ISO-8601 date-time string, colons replaced with hyphens
 *   to satisfy blob-name constraints.
 */
export function buildBlobName(blobPath: string, timestamp: Date): string {
  const ts = timestamp
    .toISOString()
    .replace(/\.\d{3}Z$/, "Z")   // strip milliseconds
    .replace(/:/g, "-");         // colons → hyphens (Windows-safe)
  const prefix = blobPath.endsWith("/") ? blobPath : `${blobPath}/`;
  return `${prefix}${ts}.ndjson.gz`;
}
