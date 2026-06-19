import { readFileSync } from "node:fs";
import { z } from "zod";

const AgentSchema = z.object({
  kind: z.enum(["vm", "host"]),
  displayName: z.string().optional(),
  sharedSecret: z.string().min(12),
  remoteUrl: z.string().optional()
});

const AccountSchema = z.object({
  agentId: z.string().min(1),
  displayName: z.string().optional(),
  vmName: z.string().optional(),
  hostAgentId: z.string().optional()
});

const ControllerConfigSchema = z.object({
  allowedDiscordUserIds: z.array(z.string()).default([]),
  startAllDelaySeconds: z.number().int().min(0).default(20),
  agents: z.record(AgentSchema),
  accounts: z.record(AccountSchema)
});

export type ControllerConfig = z.infer<typeof ControllerConfigSchema>;
export type ControllerAgentConfig = ControllerConfig["agents"][string];
export type ControllerAccountConfig = ControllerConfig["accounts"][string];

export function loadConfig(path = process.env.CONFIG_PATH ?? "./config/controller.config.json"): ControllerConfig {
  const raw = readFileSync(path, "utf8");
  const parsed = ControllerConfigSchema.parse(JSON.parse(raw));

  for (const [accountKey, account] of Object.entries(parsed.accounts)) {
    if (!parsed.agents[account.agentId]) {
      throw new Error(`Account "${accountKey}" references missing VM agent "${account.agentId}".`);
    }

    if (account.hostAgentId && !parsed.agents[account.hostAgentId]) {
      throw new Error(`Account "${accountKey}" references missing host agent "${account.hostAgentId}".`);
    }
  }

  return parsed;
}

export function requireEnv(name: string): string {
  const value = process.env[name];
  if (!value) {
    throw new Error(`${name} is required.`);
  }

  return value;
}
