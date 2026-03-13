import { describe, it, expect } from "vitest";
import { computeRowHash } from "../hash.js";

describe("computeRowHash", () => {
  it("produces a 64-character hex SHA-256 digest", () => {
    const row = { Name: "Smith", DOB: "1985-01-01", Email: "john@example.com" };
    const hash = computeRowHash(row, ["Name", "DOB", "Email"]);
    expect(hash).toMatch(/^[0-9a-f]{64}$/);
  });

  it("is deterministic for the same input", () => {
    const row = { A: "foo", B: "bar", C: "baz" };
    const fields = ["A", "B", "C"];
    expect(computeRowHash(row, fields)).toBe(computeRowHash(row, fields));
  });

  it("changes when a field value changes", () => {
    const row1 = { Name: "Smith", DOB: "1985-01-01" };
    const row2 = { Name: "Jones", DOB: "1985-01-01" };
    const fields = ["Name", "DOB"];
    expect(computeRowHash(row1, fields)).not.toBe(computeRowHash(row2, fields));
  });

  it("trims string values before hashing", () => {
    const row1 = { Name: "Smith  ", DOB: "1985-01-01" };
    const row2 = { Name: "Smith", DOB: "1985-01-01" };
    const fields = ["Name", "DOB"];
    expect(computeRowHash(row1, fields)).toBe(computeRowHash(row2, fields));
  });

  it("treats null and undefined as empty string", () => {
    const row1: Record<string, unknown> = { Name: "Smith", DOB: null };
    const row2: Record<string, unknown> = { Name: "Smith", DOB: undefined };
    const row3: Record<string, unknown> = { Name: "Smith", DOB: "" };
    const fields = ["Name", "DOB"];
    expect(computeRowHash(row1, fields)).toBe(computeRowHash(row2, fields));
    expect(computeRowHash(row1, fields)).toBe(computeRowHash(row3, fields));
  });

  it("produces expected hash for Pathway names-individuals fields", async () => {
    // Matches the portal's expected hash computation for this integration.
    const row = {
      Given_Name: "John  ",
      Surname: "Smith  ",
      DOB: "1985-03-15",
      Email: "john@example.com",
      Phone_H: "0398765432",
      Phone_M: "0412345678",
      Title: "Mr",
      Gender: "M",
      Category_Code: "IND",
      IS_Company: "F",
      IS_Private: "F",
      IS_Deceased: "F",
    };
    const fields = [
      "Given_Name",
      "Surname",
      "DOB",
      "Email",
      "Phone_H",
      "Phone_M",
      "Title",
      "Gender",
      "Category_Code",
      "IS_Company",
      "IS_Private",
      "IS_Deceased",
    ];

    const hash = computeRowHash(row, fields);
    // Verify the hash is deterministic and matches the same logic run inline.
    const { createHash } = await import("node:crypto");
    const expected = createHash("sha256")
      .update("John|Smith|1985-03-15|john@example.com|0398765432|0412345678|Mr|M|IND|F|F|F")
      .digest("hex");
    expect(hash).toBe(expected);
  });

  it("handles missing fields gracefully (returns empty string for missing key)", async () => {
    const row: Record<string, unknown> = { Name: "Smith" };
    const fields = ["Name", "MissingField"];
    const hash = computeRowHash(row, fields);
    // Should not throw, and should match treating the missing field as ""
    const { createHash } = await import("node:crypto");
    const expected = createHash("sha256").update("Smith|").digest("hex");
    expect(hash).toBe(expected);
  });

  it("handles numeric values", async () => {
    const row = { id: 12345, score: 98.6 };
    const fields = ["id", "score"];
    const hash = computeRowHash(row, fields);
    const { createHash } = await import("node:crypto");
    const expected = createHash("sha256").update("12345|98.6").digest("hex");
    expect(hash).toBe(expected);
  });
});
