import { runLoop, stopLoop } from "./loop.js";
import logger from "./logger.js";
import { getConfig } from "./config.js";

// Validate config at startup — fail fast if required env vars are missing.
try {
  getConfig();
} catch (err) {
  const message = err instanceof Error ? err.message : String(err);
  process.stderr.write(`Configuration error: ${message}\n`);
  process.exit(1);
}

logger.info("crm-agent starting");

let shuttingDown = false;

function shutdown(signal: string): void {
  if (shuttingDown) return;
  shuttingDown = true;
  logger.info({ signal }, "Shutdown requested — stopping after current job");
  stopLoop();
}

process.on("SIGTERM", () => shutdown("SIGTERM"));
process.on("SIGINT", () => shutdown("SIGINT"));

runLoop().catch((err) => {
  logger.error({ err }, "Unexpected error in main loop");
  process.exit(1);
});
