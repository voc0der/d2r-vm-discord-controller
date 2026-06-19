import {
  AttachmentBuilder,
  ChatInputCommandInteraction,
  Client,
  Events,
  GatewayIntentBits,
  MessageFlags
} from "discord.js";

import type { AgentRegistry, AgentSnapshot } from "../agents.js";
import type { ControllerAccountConfig, ControllerConfig } from "../config.js";
import { requireEnv } from "../config.js";
import type { ActiveGame, AppDb } from "../db.js";
import { logger } from "../logger.js";
import { isScreenshotResultData, type ControllerCommand } from "../protocol.js";

export async function startDiscordBot(config: ControllerConfig, registry: AgentRegistry, db: AppDb): Promise<Client> {
  const token = requireEnv("DISCORD_TOKEN");
  const client = new Client({ intents: [GatewayIntentBits.Guilds] });

  client.once(Events.ClientReady, (readyClient) => {
    logger.info({ user: readyClient.user.tag }, "discord bot logged in");
  });

  client.on(Events.InteractionCreate, async (interaction) => {
    if (!interaction.isChatInputCommand()) {
      return;
    }

    if (!isAllowed(config, interaction.user.id)) {
      await interaction.reply({
        content: "Not authorized for this controller.",
        flags: MessageFlags.Ephemeral
      });
      return;
    }

    try {
      if (interaction.commandName === "d2r") {
        await handleD2RCommand(config, registry, db, interaction);
        return;
      }

      if (interaction.commandName === "vm") {
        await handleVmCommand(config, registry, interaction);
        return;
      }

      if (interaction.commandName === "game") {
        await handleGameCommand(db, interaction);
        return;
      }
    } catch (error) {
      logger.error({ error }, "discord command failed");
      const message = error instanceof Error ? error.message : "Unknown command failure.";
      const content = `Command failed: ${message}`;

      if (interaction.deferred || interaction.replied) {
        await interaction.editReply({ content });
      } else {
        await interaction.reply({ content, flags: MessageFlags.Ephemeral });
      }
    }
  });

  await client.login(token);
  return client;
}

async function handleD2RCommand(
  config: ControllerConfig,
  registry: AgentRegistry,
  db: AppDb,
  interaction: ChatInputCommandInteraction
): Promise<void> {
  const subcommand = interaction.options.getSubcommand();

  if (subcommand === "health") {
    await interaction.reply({
      content: formatHealth(registry.snapshot()),
      flags: MessageFlags.Ephemeral
    });
    return;
  }

  if (subcommand === "status") {
    const accountKey = interaction.options.getString("account");
    await interaction.reply({
      content: accountKey
        ? formatAccountStatus(config, registry, accountKey)
        : formatAllAccountStatuses(config, registry),
      flags: MessageFlags.Ephemeral
    });
    return;
  }

  if (subcommand === "start-all") {
    await queueAllCommands(interaction, config, registry, "launch_d2r", (_accountKey, account) => buildAccountArgs(_accountKey, account));
    return;
  }

  if (subcommand === "join-all") {
    const game = resolveGameInput(db, interaction);
    await queueAllCommands(
      interaction,
      config,
      registry,
      "menu_join_game",
      (queuedAccountKey, queuedAccount) => buildMenuArgs(queuedAccountKey, queuedAccount, {
        ...game,
        characterSlot: interaction.options.getInteger("character-slot") ?? undefined
      }),
      150_000
    );
    return;
  }

  if (subcommand === "follow-all") {
    await queueAllCommands(
      interaction,
      config,
      registry,
      "menu_join_friend",
      (queuedAccountKey, queuedAccount) => buildMenuArgs(queuedAccountKey, queuedAccount, {
        characterSlot: interaction.options.getInteger("character-slot") ?? undefined,
        friendRow: interaction.options.getInteger("friend-row") ?? undefined
      }),
      150_000
    );
    return;
  }

  if (subcommand === "save-exit-all" || subcommand === "leave-all") {
    await queueAllCommands(
      interaction,
      config,
      registry,
      "menu_save_exit",
      (queuedAccountKey, queuedAccount) => buildMenuArgs(queuedAccountKey, queuedAccount),
      90_000
    );
    return;
  }

  const { accountKey, account } = requireAccount(config, interaction.options.getString("account", true));

  if (subcommand === "start") {
    await runVmCommand(interaction, registry, account, "launch_d2r", buildAccountArgs(accountKey, account));
    return;
  }

  if (subcommand === "stop") {
    await runVmCommand(interaction, registry, account, "kill_d2r", buildAccountArgs(accountKey, account));
    return;
  }

  if (subcommand === "restart-client") {
    await runVmCommand(interaction, registry, account, "restart_d2r", buildAccountArgs(accountKey, account));
    return;
  }

  if (subcommand === "ready") {
    await runVmCommand(interaction, registry, account, "menu_ready", buildMenuArgs(accountKey, account), 150_000);
    return;
  }

  if (subcommand === "lobby") {
    await runVmCommand(interaction, registry, account, "menu_lobby", buildMenuArgs(accountKey, account, {
      characterSlot: interaction.options.getInteger("character-slot") ?? undefined
    }), 90_000);
    return;
  }

  if (subcommand === "play") {
    await runVmCommand(interaction, registry, account, "menu_play", buildMenuArgs(accountKey, account, {
      characterSlot: interaction.options.getInteger("character-slot") ?? undefined
    }), 90_000);
    return;
  }

  if (subcommand === "join-game") {
    const game = resolveGameInput(db, interaction);
    await runVmCommand(interaction, registry, account, "menu_join_game", buildMenuArgs(accountKey, account, {
      ...game,
      characterSlot: interaction.options.getInteger("character-slot") ?? undefined
    }), 150_000);
    return;
  }

  if (subcommand === "create-game") {
    const game = resolveGameInput(db, interaction);
    await runVmCommand(interaction, registry, account, "menu_create_game", buildMenuArgs(accountKey, account, {
      ...game,
      characterSlot: interaction.options.getInteger("character-slot") ?? undefined
    }), 150_000);
    return;
  }

  if (subcommand === "follow") {
    await runVmCommand(interaction, registry, account, "menu_join_friend", buildMenuArgs(accountKey, account, {
      characterSlot: interaction.options.getInteger("character-slot") ?? undefined,
      friendRow: interaction.options.getInteger("friend-row") ?? undefined
    }), 150_000);
    return;
  }

  if (subcommand === "save-exit" || subcommand === "leave") {
    await runVmCommand(interaction, registry, account, "menu_save_exit", buildMenuArgs(accountKey, account), 90_000);
    return;
  }

  if (subcommand === "screenshot") {
    await interaction.deferReply({ flags: MessageFlags.Ephemeral });
    const result = await registry.sendCommand(
      account.agentId,
      "screenshot",
      buildAccountArgs(accountKey, account),
      60_000
    );

    if (!result.ok || !isScreenshotResultData(result.data)) {
      await interaction.editReply(formatCommandResult(result.ok, result.message));
      return;
    }

    const extension = result.data.mimeType === "image/jpeg" ? "jpg" : "png";
    const attachment = new AttachmentBuilder(Buffer.from(result.data.base64, "base64"), {
      name: `${accountKey}-screenshot.${extension}`
    });
    await interaction.editReply({
      content: result.message,
      files: [attachment]
    });
    return;
  }

  if (subcommand === "remote") {
    const remoteUrl = config.agents[account.agentId]?.remoteUrl;
    await interaction.reply({
      content: remoteUrl
        ? `${accountKey} remote link: ${remoteUrl}`
        : `No remoteUrl is configured for ${accountKey} (${account.agentId}).`,
      flags: MessageFlags.Ephemeral
    });
  }
}

async function handleVmCommand(
  config: ControllerConfig,
  registry: AgentRegistry,
  interaction: ChatInputCommandInteraction
): Promise<void> {
  const subcommand = interaction.options.getSubcommand();
  const { accountKey, account } = requireAccount(config, interaction.options.getString("account", true));
  const hostAgentId = account.hostAgentId;
  const vmName = account.vmName ?? account.agentId;

  if (!hostAgentId) {
    throw new Error(`Account "${accountKey}" does not have hostAgentId configured.`);
  }

  await interaction.deferReply({ flags: MessageFlags.Ephemeral });

  const args = {
    accountKey,
    vmName,
    snapshotName: subcommand === "snapshot" ? interaction.options.getString("name") ?? undefined : undefined
  };
  const command =
    subcommand === "status"
      ? "vm_status"
      : subcommand === "start"
        ? "vm_start"
        : subcommand === "stop"
          ? "vm_stop"
          : subcommand === "reboot"
            ? "vm_reboot"
            : "vm_snapshot";

  const result = await registry.sendCommand(hostAgentId, command, args);
  await interaction.editReply(formatCommandResult(result.ok, result.message));
}

async function handleGameCommand(db: AppDb, interaction: ChatInputCommandInteraction): Promise<void> {
  const subcommand = interaction.options.getSubcommand();

  if (subcommand === "set") {
    const game = db.setActiveGame({
      name: interaction.options.getString("name", true),
      password: blankToUndefined(interaction.options.getString("password")),
      difficulty: interaction.options.getString("difficulty") ?? undefined,
      notes: blankToUndefined(interaction.options.getString("notes")),
      updatedBy: interaction.user.id
    });

    await interaction.reply({
      content: `Stored current game:\n${formatActiveGame(game)}`,
      flags: MessageFlags.Ephemeral
    });
    return;
  }

  if (subcommand === "show") {
    const game = db.getActiveGame();
    await interaction.reply({
      content: game ? formatActiveGame(game) : "No current game is stored.",
      flags: MessageFlags.Ephemeral
    });
    return;
  }

  if (subcommand === "clear") {
    const cleared = db.clearActiveGame();
    await interaction.reply({
      content: cleared ? "Cleared the stored game." : "No current game was stored.",
      flags: MessageFlags.Ephemeral
    });
  }
}

function isAllowed(config: ControllerConfig, userId: string): boolean {
  return config.allowedDiscordUserIds.length === 0 || config.allowedDiscordUserIds.includes(userId);
}

function requireAccount(
  config: ControllerConfig,
  accountKey: string
): { accountKey: string; account: ControllerAccountConfig } {
  const account = config.accounts[accountKey];
  if (!account) {
    throw new Error(`Unknown account "${accountKey}".`);
  }

  return { accountKey, account };
}

function buildAccountArgs(accountKey: string, account: ControllerAccountConfig): Record<string, unknown> {
  return {
    accountKey,
    displayName: account.displayName ?? accountKey,
    vmName: account.vmName ?? account.agentId
  };
}

function buildMenuArgs(
  accountKey: string,
  account: ControllerAccountConfig,
  extra: Record<string, unknown> = {}
): Record<string, unknown> {
  return {
    ...buildAccountArgs(accountKey, account),
    ...extra
  };
}

async function runVmCommand(
  interaction: ChatInputCommandInteraction,
  registry: AgentRegistry,
  account: ControllerAccountConfig,
  command: ControllerCommand,
  args: Record<string, unknown>,
  timeoutMs = 60_000
): Promise<void> {
  await interaction.deferReply({ flags: MessageFlags.Ephemeral });
  const result = await registry.sendCommand(account.agentId, command, args, timeoutMs);
  await interaction.editReply(formatCommandResult(result.ok, result.message));
}

async function queueAllCommands(
  interaction: ChatInputCommandInteraction,
  config: ControllerConfig,
  registry: AgentRegistry,
  command: ControllerCommand,
  argsFactory: (accountKey: string, account: ControllerAccountConfig) => Record<string, unknown>,
  timeoutMs = 60_000
): Promise<void> {
  const entries = Object.entries(config.accounts);
  const staggerSeconds = getClientStaggerSeconds(config);
  await interaction.reply({
    content: `Queued ${entries.length} ${command} command(s) with ${staggerSeconds}s stagger.`,
    flags: MessageFlags.Ephemeral
  });

  entries.forEach(([queuedAccountKey, queuedAccount], index) => {
    const delayMs = index * staggerSeconds * 1000;
    setTimeout(() => {
      registry
        .sendCommand(queuedAccount.agentId, command, argsFactory(queuedAccountKey, queuedAccount), timeoutMs)
        .catch((error) => logger.error({ error, accountKey: queuedAccountKey, command }, "queued all-account command failed"));
    }, delayMs);
  });
}

function getClientStaggerSeconds(config: ControllerConfig): number {
  const raw = process.env.CLIENT_STAGGER_SECONDS;
  if (!raw || !raw.trim()) {
    return config.startAllDelaySeconds;
  }

  const parsed = Number.parseInt(raw, 10);
  if (!Number.isFinite(parsed) || parsed < 0) {
    logger.warn({ CLIENT_STAGGER_SECONDS: raw }, "invalid CLIENT_STAGGER_SECONDS, using config startAllDelaySeconds");
    return config.startAllDelaySeconds;
  }

  return parsed;
}

function resolveGameInput(
  db: AppDb,
  interaction: ChatInputCommandInteraction
): { gameName: string; password?: string; difficulty?: string } {
  const stored = db.getActiveGame();
  const gameName = blankToUndefined(interaction.options.getString("name")) ?? stored?.name;
  if (!gameName) {
    throw new Error("Game name is required. Pass name or set it first with /game set.");
  }

  return {
    gameName,
    password: blankToUndefined(interaction.options.getString("password")) ?? stored?.password,
    difficulty: interaction.options.getString("difficulty") ?? stored?.difficulty
  };
}

function formatCommandResult(ok: boolean, message: string): string {
  return `${ok ? "OK" : "Failed"}: ${message}`;
}

function formatActiveGame(game: ActiveGame): string {
  return [
    `Game: ${game.name}`,
    `Password: ${game.password ?? "(none)"}`,
    `Difficulty: ${game.difficulty ?? "(not set)"}`,
    game.notes ? `Notes: ${game.notes}` : undefined,
    `Updated: ${new Date(game.updatedUtc).toLocaleString()}`
  ]
    .filter(Boolean)
    .join("\n");
}

function blankToUndefined(value: string | null): string | undefined {
  if (!value || !value.trim()) {
    return undefined;
  }

  return value.trim();
}

function formatHealth(agents: AgentSnapshot[]): string {
  const connected = agents.filter((agent) => agent.connected).length;
  const lines = agents.map((agent) => {
    const label = agent.displayName ? `${agent.id} (${agent.displayName})` : agent.id;
    return `${agent.connected ? "online " : "offline"} ${label}`;
  });

  return [`Agents: ${connected}/${agents.length} connected`, ...lines].join("\n").slice(0, 1900);
}

function formatAllAccountStatuses(config: ControllerConfig, registry: AgentRegistry): string {
  const lines = Object.entries(config.accounts).map(([accountKey, account]) => {
    const agent = registry.getAgent(account.agentId);
    return formatAccountStatusLine(accountKey, account, agent);
  });

  return lines.join("\n").slice(0, 1900);
}

function formatAccountStatus(config: ControllerConfig, registry: AgentRegistry, accountKey: string): string {
  const { account } = requireAccount(config, accountKey);
  return formatAccountStatusLine(accountKey, account, registry.getAgent(account.agentId));
}

function formatAccountStatusLine(
  accountKey: string,
  account: ControllerAccountConfig,
  agent: AgentSnapshot | undefined
): string {
  const name = account.displayName ? `${accountKey} (${account.displayName})` : accountKey;
  if (!agent?.connected) {
    return `${name}: offline`;
  }

  const status = agent.lastStatus ?? {};
  const battleNet = formatRunning(status.battleNetRunning);
  const d2r = formatRunning(status.d2rRunning);
  const lastSeen = agent.lastSeenAt ? new Date(agent.lastSeenAt).toLocaleString() : "unknown";
  return `${name}: online, Battle.net ${battleNet}, D2R ${d2r}, seen ${lastSeen}`;
}

function formatRunning(value: unknown): string {
  if (value === true) {
    return "running";
  }

  if (value === false) {
    return "stopped";
  }

  return "unknown";
}
