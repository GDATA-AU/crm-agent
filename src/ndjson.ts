import { createGzip } from "node:zlib";
import { PassThrough, Readable, Transform } from "node:stream";

/**
 * Creates a streaming pipeline that accepts plain JavaScript objects and
 * outputs a gzip-compressed NDJSON stream suitable for uploading to blob
 * storage.
 *
 * Usage:
 *   const writer = createNdjsonGzipStream();
 *   writer.output  // pipe this to the blob upload
 *   writer.write({ ... });
 *   await writer.end();
 */
export interface NdjsonGzipWriter {
  /** The compressed output stream — pipe this to blob storage. */
  readonly output: Readable;
  /** Write a single row object. */
  write(row: Record<string, unknown>): void;
  /** Flush and close the stream.  Returns a promise that resolves when done. */
  end(): Promise<void>;
}

export function createNdjsonGzipWriter(): NdjsonGzipWriter {
  const passThrough = new PassThrough();
  const gzip = createGzip();
  passThrough.pipe(gzip);

  let ended = false;

  function write(row: Record<string, unknown>): void {
    if (ended) throw new Error("Cannot write to a closed NdjsonGzipWriter");
    const line = JSON.stringify(row) + "\n";
    passThrough.write(line, "utf8");
  }

  function end(): Promise<void> {
    if (ended) return Promise.resolve();
    ended = true;
    return new Promise((resolve, reject) => {
      gzip.once("finish", resolve);
      gzip.once("error", reject);
      passThrough.end();
    });
  }

  return { output: gzip, write, end };
}

/**
 * Creates a Transform stream that accepts objects (in object mode) and emits
 * NDJSON lines as Buffers.  Useful for piping from a database cursor.
 */
export function createNdjsonTransform(): Transform {
  return new Transform({
    writableObjectMode: true,
    transform(row: Record<string, unknown>, _encoding, callback) {
      try {
        this.push(Buffer.from(JSON.stringify(row) + "\n", "utf8"));
        callback();
      } catch (err) {
        callback(err as Error);
      }
    },
  });
}
