import { SlashCommandBuilder, SlashCommandIntegerOption, SlashCommandStringOption } from "discord.js";

function accountOption(option: SlashCommandStringOption): SlashCommandStringOption {
  return option
    .setName("account")
    .setDescription("Configured account key, for example hc1")
    .setRequired(true);
}

function optionalAccountOption(option: SlashCommandStringOption): SlashCommandStringOption {
  return option
    .setName("account")
    .setDescription("Configured account key, for example hc1")
    .setRequired(false);
}

function characterSlotOption(option: SlashCommandIntegerOption): SlashCommandIntegerOption {
  return option
    .setName("character-slot")
    .setDescription("Character slot to select, 1-8; defaults to VM config")
    .setMinValue(1)
    .setMaxValue(8)
    .setRequired(false);
}

function friendRowOption(option: SlashCommandIntegerOption): SlashCommandIntegerOption {
  return option
    .setName("friend-row")
    .setDescription("Visible friends drawer row to right-click; defaults to VM config")
    .setMinValue(1)
    .setMaxValue(20)
    .setRequired(false);
}

function gameNameOption(option: SlashCommandStringOption): SlashCommandStringOption {
  return option
    .setName("name")
    .setDescription("Game name; defaults to /game show value")
    .setRequired(false);
}

function passwordOption(option: SlashCommandStringOption): SlashCommandStringOption {
  return option
    .setName("password")
    .setDescription("Game password; defaults to /game show value")
    .setRequired(false);
}

function difficultyOption(option: SlashCommandStringOption): SlashCommandStringOption {
  return option
    .setName("difficulty")
    .setDescription("Game difficulty; defaults to /game show value or VM UI default")
    .setRequired(false)
    .addChoices(
      { name: "Normal", value: "normal" },
      { name: "Nightmare", value: "nightmare" },
      { name: "Hell", value: "hell" }
    );
}

export const slashCommands = [
  new SlashCommandBuilder()
    .setName("d2r")
    .setDescription("Operate Diablo II: Resurrected clients")
    .addSubcommand((subcommand) =>
      subcommand
        .setName("status")
        .setDescription("Show one account or all account client statuses")
        .addStringOption(optionalAccountOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("start")
        .setDescription("Launch Battle.net/D2R for an account")
        .addStringOption(accountOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("stop")
        .setDescription("Kill the D2R process for an account")
        .addStringOption(accountOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("restart-client")
        .setDescription("Restart the D2R process for an account")
        .addStringOption(accountOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("screenshot")
        .setDescription("Capture the VM's current primary-screen screenshot")
        .addStringOption(accountOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("remote")
        .setDescription("Show the configured remote-control URL for an account VM")
        .addStringOption(accountOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("ready")
        .setDescription("Launch D2R, click Battle.net Play if needed, and skip intro screens")
        .addStringOption(accountOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("lobby")
        .setDescription("Select character and open Lobby")
        .addStringOption(accountOption)
        .addIntegerOption(characterSlotOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("play")
        .setDescription("Select character and click Play")
        .addStringOption(accountOption)
        .addIntegerOption(characterSlotOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("join-game")
        .setDescription("Open Lobby and join a named game")
        .addStringOption(accountOption)
        .addStringOption(gameNameOption)
        .addStringOption(passwordOption)
        .addStringOption(difficultyOption)
        .addIntegerOption(characterSlotOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("create-game")
        .setDescription("Open Lobby and create a game")
        .addStringOption(accountOption)
        .addStringOption(gameNameOption)
        .addStringOption(passwordOption)
        .addStringOption(difficultyOption)
        .addIntegerOption(characterSlotOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("follow")
        .setDescription("Join off a friend from the Lobby friends drawer")
        .addStringOption(accountOption)
        .addIntegerOption(characterSlotOption)
        .addIntegerOption(friendRowOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("save-exit")
        .setDescription("Open the in-game menu and click Save and Exit")
        .addStringOption(accountOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("leave")
        .setDescription("Alias for Save and Exit")
        .addStringOption(accountOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("join-all")
        .setDescription("Stagger all accounts into a named game")
        .addStringOption(gameNameOption)
        .addStringOption(passwordOption)
        .addStringOption(difficultyOption)
        .addIntegerOption(characterSlotOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("follow-all")
        .setDescription("Stagger all accounts into a friend's game")
        .addIntegerOption(characterSlotOption)
        .addIntegerOption(friendRowOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("save-exit-all")
        .setDescription("Stagger Save and Exit across all accounts")
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("leave-all")
        .setDescription("Alias for Save and Exit across all accounts")
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("start-all")
        .setDescription("Queue staggered launches for all configured accounts")
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("health")
        .setDescription("Show controller and agent connection health")
    ),
  new SlashCommandBuilder()
    .setName("vm")
    .setDescription("Operate mapped Hyper-V virtual machines")
    .addSubcommand((subcommand) =>
      subcommand
        .setName("status")
        .setDescription("Get Hyper-V status for an account VM")
        .addStringOption(accountOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("start")
        .setDescription("Start an account VM")
        .addStringOption(accountOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("stop")
        .setDescription("Stop an account VM")
        .addStringOption(accountOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("reboot")
        .setDescription("Restart an account VM")
        .addStringOption(accountOption)
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("snapshot")
        .setDescription("Create a Hyper-V checkpoint for an account VM")
        .addStringOption(accountOption)
        .addStringOption((option) =>
          option
            .setName("name")
            .setDescription("Optional snapshot/checkpoint name")
            .setRequired(false)
        )
    ),
  new SlashCommandBuilder()
    .setName("game")
    .setDescription("Track the current D2R game details")
    .addSubcommand((subcommand) =>
      subcommand
        .setName("set")
        .setDescription("Store the current game name/password for clients to join")
        .addStringOption((option) =>
          option
            .setName("name")
            .setDescription("Game name")
            .setRequired(true)
        )
        .addStringOption((option) =>
          option
            .setName("password")
            .setDescription("Game password, if any")
            .setRequired(false)
        )
        .addStringOption((option) =>
          option
            .setName("difficulty")
            .setDescription("Game difficulty")
            .setRequired(false)
            .addChoices(
              { name: "Normal", value: "normal" },
              { name: "Nightmare", value: "nightmare" },
              { name: "Hell", value: "hell" }
            )
        )
        .addStringOption((option) =>
          option
            .setName("notes")
            .setDescription("Optional short note, for example join off friend instead")
            .setRequired(false)
        )
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("show")
        .setDescription("Show the stored game details")
    )
    .addSubcommand((subcommand) =>
      subcommand
        .setName("clear")
        .setDescription("Clear the stored game details")
    )
].map((command) => command.toJSON());
