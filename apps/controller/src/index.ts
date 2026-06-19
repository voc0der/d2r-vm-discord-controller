import { AgentRegistry } from "./agents.js";
import { loadConfig } from "./config.js";
import { AppDb } from "./db.js";
import { startDiscordBot } from "./discord/bot.js";
import { logger } from "./logger.js";
import { startControllerServer } from "./server.js";

const config = loadConfig();
const db = new AppDb();
const registry = new AgentRegistry(config, db);
const port = Number.parseInt(process.env.HTTP_PORT ?? "8080", 10);

await startControllerServer({ config, registry, port });

if (process.env.DISABLE_DISCORD === "true") {
  logger.warn("DISABLE_DISCORD=true, Discord bot is not starting");
} else {
  await startDiscordBot(config, registry, db);
}
