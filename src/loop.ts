import { getConfig } from "./config.js";
import { pollForJob, reportJobStatus } from "./portal.js";
import { getHandler } from "./handlers/index.js";
import logger from "./logger.js";
import type { JobProgress } from "./types.js";

let running = true;
let currentJobId: string | undefined;

/**
 * Signal the loop to stop after the current job (if any) completes.
 */
export function stopLoop(): void {
  running = false;
}

/**
 * Sleep for `ms` milliseconds, but wake up immediately if `running` becomes
 * false.
 */
function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => {
    const id = setTimeout(resolve, ms);
    // If the agent is shutting down, resolve the sleep immediately by checking
    // `running` periodically.  In practice, the shutdown handler calls
    // `stopLoop()` which terminates the while-loop at the next check, so a
    // simple timeout is fine here.
    if (!running) {
      clearTimeout(id);
      resolve();
    }
  });
}

/**
 * The main agent poll loop.
 *
 * Polls the portal for jobs, executes them, and reports results.
 * Runs until `stopLoop()` is called.
 */
export async function runLoop(): Promise<void> {
  const { pollIntervalMs, heartbeatIntervalMs } = getConfig();

  logger.info({ pollIntervalMs }, "Agent poll loop started");

  while (running) {
    let job = null;
    try {
      job = await pollForJob();
    } catch (err) {
      logger.warn({ err }, "Failed to poll for job — will retry");
      await sleep(pollIntervalMs);
      continue;
    }

    if (!job) {
      logger.debug("No job available — sleeping");
      await sleep(pollIntervalMs);
      continue;
    }

    currentJobId = job.id;
    logger.info({ jobId: job.id, jobType: job.type }, "Job received");

    // -------------------------------------------------------------------------
    // Report "running"
    // -------------------------------------------------------------------------
    await reportJobStatus(job.id, { status: "running" });

    // -------------------------------------------------------------------------
    // Start heartbeat timer
    // -------------------------------------------------------------------------
    let lastProgress: JobProgress = { processedRows: 0 };
    const heartbeatTimer = setInterval(() => {
      void reportJobStatus(job.id, {
        status: "running",
        progress: lastProgress,
      });
    }, heartbeatIntervalMs);

    // -------------------------------------------------------------------------
    // Execute the handler
    // -------------------------------------------------------------------------
    try {
      const handler = getHandler(job);

      const result = await handler.execute(job, (progress) => {
        lastProgress = progress;
      });

      // Stop heartbeat
      clearInterval(heartbeatTimer);
      currentJobId = undefined;

      // Report completion
      await reportJobStatus(job.id, {
        status: "completed",
        progress: { processedRows: result.processedRows },
        blobName: result.blobName,
      });

      logger.info(
        { jobId: job.id, blobName: result.blobName, rows: result.processedRows },
        "Job completed"
      );
    } catch (err) {
      clearInterval(heartbeatTimer);
      currentJobId = undefined;

      const message = err instanceof Error ? err.message : String(err);
      logger.error({ jobId: job.id, err }, "Job failed");

      await reportJobStatus(job.id, {
        status: "failed",
        error: message,
      });
    }

    // Sleep before next poll
    if (running) {
      await sleep(pollIntervalMs);
    }
  }

  logger.info("Agent poll loop stopped");
}

export { currentJobId };
