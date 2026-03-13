import { createHash } from "node:crypto";

/**
 * Compute a SHA-256 row hash from selected fields of a row.
 *
 * The hash is computed as:
 *   SHA-256( field1|field2|field3|... )
 *
 * where each field value is trimmed (if a string) before concatenation, and
 * null/undefined values are represented as an empty string.
 *
 * This matches the portal's `computeRowHash` fallback logic exactly.
 */
export function computeRowHash(
  row: Record<string, unknown>,
  hashFields: string[]
): string {
  const parts = hashFields.map((field) => {
    const val = row[field];
    if (val == null) return "";
    return String(val).trim();
  });
  return createHash("sha256").update(parts.join("|")).digest("hex");
}
