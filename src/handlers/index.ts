import type { Job, JobHandler } from "../types.js";
import { sqlHandler } from "./sql.js";
import { restApiHandler } from "./rest-api.js";

const handlers: Record<string, JobHandler> = {
  sql: sqlHandler,
  "rest-api": restApiHandler,
};

/**
 * Returns the appropriate handler for the given job type.
 * Throws if the job type is unknown.
 */
export function getHandler(job: Job): JobHandler {
  const handler = handlers[job.type];
  if (!handler) {
    throw new Error(`Unknown job type: "${job.type}"`);
  }
  return handler;
}
