#!/usr/bin/env node
/**
 * install-windows.ts
 *
 * Installs crm-agent as a Windows service using node-windows.
 *
 * Usage (run as Administrator):
 *   npx tsx install-windows.ts
 *
 * To uninstall:
 *   npx tsx install-windows.ts --uninstall
 */
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const isUninstall = process.argv.includes("--uninstall");

// node-windows only works on Windows — guard early on other platforms.
if (process.platform !== "win32") {
  console.error("install-windows.ts must be run on Windows.");
  process.exit(1);
}

// Dynamic import so the module resolution doesn't fail on non-Windows at
// TypeScript compile time.
const { Service } = await import("node-windows");

const svc = new Service({
  name: "crm-agent",
  description: "LGA CRM Agent — polls the council portal for extraction jobs and writes results to Azure Blob Storage.",
  script: path.join(__dirname, "dist", "index.js"),
  nodeOptions: ["--enable-source-maps"],
  env: [
    { name: "NODE_ENV", value: "production" },
  ],
});

if (isUninstall) {
  svc.on("uninstall", () => {
    console.log("crm-agent service uninstalled.");
  });
  console.log("Uninstalling crm-agent service...");
  svc.uninstall();
} else {
  svc.on("install", () => {
    console.log("crm-agent service installed.  Starting...");
    svc.start();
  });

  svc.on("alreadyinstalled", () => {
    console.log("crm-agent service is already installed.");
  });

  svc.on("error", (err: Error) => {
    console.error("Service error:", err.message);
    process.exit(1);
  });

  console.log("Installing crm-agent as a Windows service...");
  svc.install();
}
