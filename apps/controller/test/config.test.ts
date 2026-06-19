import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

import { loadConfig } from "../src/config.js";

describe("loadConfig", () => {
  it("loads a valid controller config", () => {
    const dir = mkdtempSync(join(tmpdir(), "d2r-config-"));
    const path = join(dir, "controller.config.json");
    writeFileSync(
      path,
      JSON.stringify({
        allowedDiscordUserIds: ["123"],
        agents: {
          "d2r-hc-01": {
            kind: "vm",
            sharedSecret: "0123456789abcdef"
          }
        },
        accounts: {
          hc1: {
            agentId: "d2r-hc-01"
          }
        }
      })
    );

    const config = loadConfig(path);
    expect(config.accounts.hc1.agentId).toBe("d2r-hc-01");
    expect(config.startAllDelaySeconds).toBe(20);
  });

  it("rejects accounts that reference missing agents", () => {
    const dir = mkdtempSync(join(tmpdir(), "d2r-config-"));
    const path = join(dir, "controller.config.json");
    writeFileSync(
      path,
      JSON.stringify({
        agents: {},
        accounts: {
          hc1: {
            agentId: "missing"
          }
        }
      })
    );

    expect(() => loadConfig(path)).toThrow(/missing VM agent/);
  });
});
