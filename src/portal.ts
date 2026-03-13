import { getConfig } from "./config.js";
import type { Job, JobStatusUpdate } from "./types.js";
import logger from "./logger.js";

/**
 * Poll the portal for a pending job.
 *
 * Returns the job if one is available, or `null` if the portal responded
 * with 204 (no work).  Throws on network or non-2xx errors.
 */
export async function pollForJob(): Promise<Job | null> {
  const { portalUrl, agentApiKey } = getConfig();
  const url = `${portalUrl}/api/agent/jobs`;

  const res = await fetch(url, {
    headers: { Authorization: `Bearer ${agentApiKey}` },
  });

  if (res.status === 204) return null;

  if (!res.ok) {
    const body = await res.text().catch(() => "");
    throw new Error(`Poll failed: ${res.status} ${res.statusText} — ${body}`);
  }

  const data = (await res.json()) as { job: Job };
  return data.job;
}

/**
 * Report a status update for an active job back to the portal.
 *
 * Does NOT throw on network errors — it logs them and returns silently so
 * that the agent can continue running even when the portal is temporarily
 * unreachable.
 */
export async function reportJobStatus(
  jobId: string,
  update: JobStatusUpdate
): Promise<void> {
  const { portalUrl, agentApiKey } = getConfig();
  const url = `${portalUrl}/api/agent/jobs/${encodeURIComponent(jobId)}`;

  try {
    const res = await fetch(url, {
      method: "PATCH",
      headers: {
        Authorization: `Bearer ${agentApiKey}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify(update),
    });

    if (!res.ok) {
      const body = await res.text().catch(() => "");
      logger.warn(
        { jobId, status: res.status, body },
        "Portal responded with non-2xx to status update"
      );
    }
  } catch (err) {
    logger.warn({ jobId, err }, "Failed to report job status to portal");
  }
}
