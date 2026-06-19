import { REST, Routes } from "discord.js";

import { requireEnv } from "./config.js";
import { slashCommands } from "./discord/commands.js";
import { logger } from "./logger.js";

const token = requireEnv("DISCORD_TOKEN");
const clientId = requireEnv("DISCORD_CLIENT_ID");
const guildId = process.env.DISCORD_GUILD_ID;
const rest = new REST({ version: "10" }).setToken(token);

if (guildId) {
  await rest.put(Routes.applicationGuildCommands(clientId, guildId), {
    body: slashCommands
  });
  logger.info({ guildId, count: slashCommands.length }, "registered guild slash commands");
} else {
  await rest.put(Routes.applicationCommands(clientId), {
    body: slashCommands
  });
  logger.info({ count: slashCommands.length }, "registered global slash commands");
}
