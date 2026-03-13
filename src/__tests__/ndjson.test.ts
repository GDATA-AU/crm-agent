import { describe, it, expect } from "vitest";
import { createGunzip } from "node:zlib";
import { Readable } from "node:stream";
import { createNdjsonGzipWriter, createNdjsonTransform } from "../ndjson.js";

/** Read all data from a readable stream into a Buffer. */
function streamToBuffer(stream: Readable): Promise<Buffer> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    stream.on("data", (chunk: Buffer) => chunks.push(chunk));
    stream.on("end", () => resolve(Buffer.concat(chunks)));
    stream.on("error", reject);
  });
}

describe("createNdjsonGzipWriter", () => {
  it("writes valid gzip-compressed NDJSON", async () => {
    const writer = createNdjsonGzipWriter();
    writer.write({ id: 1, name: "Alice" });
    writer.write({ id: 2, name: "Bob" });
    await writer.end();

    const compressed = await streamToBuffer(writer.output);
    const decompressed = await streamToBuffer(
      Readable.from(compressed).pipe(createGunzip())
    );

    const lines = decompressed.toString("utf8").trim().split("\n");
    expect(lines).toHaveLength(2);
    expect(JSON.parse(lines[0])).toEqual({ id: 1, name: "Alice" });
    expect(JSON.parse(lines[1])).toEqual({ id: 2, name: "Bob" });
  });

  it("produces a non-empty gzip file even for zero rows", async () => {
    const writer = createNdjsonGzipWriter();
    await writer.end();

    const compressed = await streamToBuffer(writer.output);
    // A valid gzip file has at least the 10-byte header + 8-byte trailer.
    expect(compressed.length).toBeGreaterThanOrEqual(18);

    const decompressed = await streamToBuffer(
      Readable.from(compressed).pipe(createGunzip())
    );
    expect(decompressed.toString("utf8")).toBe("");
  });

  it("serialises rows with _rowHash field included", async () => {
    const writer = createNdjsonGzipWriter();
    writer.write({ id: 1, name: "Alice", _rowHash: "abc123" });
    await writer.end();

    const compressed = await streamToBuffer(writer.output);
    const decompressed = await streamToBuffer(
      Readable.from(compressed).pipe(createGunzip())
    );
    const parsed = JSON.parse(decompressed.toString("utf8").trim());
    expect(parsed._rowHash).toBe("abc123");
  });

  it("each line is a complete JSON object followed by a newline", async () => {
    const writer = createNdjsonGzipWriter();
    for (let i = 0; i < 5; i++) {
      writer.write({ i });
    }
    await writer.end();

    const compressed = await streamToBuffer(writer.output);
    const decompressed = await streamToBuffer(
      Readable.from(compressed).pipe(createGunzip())
    );
    const text = decompressed.toString("utf8");
    // Every non-empty line must parse as JSON
    const lines = text.split("\n").filter((l) => l.length > 0);
    expect(lines).toHaveLength(5);
    for (const line of lines) {
      expect(() => JSON.parse(line)).not.toThrow();
    }
  });
});

describe("createNdjsonTransform", () => {
  it("transforms objects to newline-delimited JSON strings", async () => {
    const transform = createNdjsonTransform();
    const rows = [{ a: 1 }, { b: 2 }, { c: 3 }];
    for (const row of rows) {
      transform.write(row);
    }
    transform.end();

    const buf = await streamToBuffer(transform as unknown as Readable);
    const text = buf.toString("utf8");
    const lines = text.trim().split("\n");
    expect(lines).toHaveLength(3);
    expect(JSON.parse(lines[0])).toEqual({ a: 1 });
    expect(JSON.parse(lines[1])).toEqual({ b: 2 });
    expect(JSON.parse(lines[2])).toEqual({ c: 3 });
  });
});
