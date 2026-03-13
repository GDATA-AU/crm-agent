import pino from "pino";
import { getConfig } from "./config.js";

let _logger: pino.Logger | undefined;

function createLogger(): pino.Logger {
  const level = (() => {
    try {
      return getConfig().logLevel;
    } catch {
      return "info";
    }
  })();

  return pino({ level, base: { name: "crm-agent" } });
}

const logger = new Proxy({} as pino.Logger, {
  get(_target, prop) {
    if (!_logger) _logger = createLogger();
    return (_logger as unknown as Record<string | symbol, unknown>)[prop];
  },
});

export default logger;
