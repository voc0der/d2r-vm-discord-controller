using Discord;

namespace D2RHost;

public static class DiscordSlashCommands
{
    public static ApplicationCommandProperties[] Build()
    {
        return
        [
            new SlashCommandBuilder()
                .WithName("d2r")
                .WithDescription("Operate Diablo II: Resurrected clients")
                .AddOptions(
                    Sub("status", "Show one account or all account client statuses", OptionalAccount()),
                    Sub("start", "Launch Battle.net/D2R for an account", Account()),
                    Sub("stop", "Kill the D2R process for an account", Account()),
                    Sub("quit", "Focus D2R and close it with Alt+F4", Account()),
                    Sub("close", "Alias for quit", Account()),
                    Sub("restart-client", "Restart the D2R process for an account", Account()),
                    Sub("screenshot", "Capture the VM's current primary-screen screenshot", Account()),
                    Sub("remote", "Show the configured remote-control URL for an account VM", Account()),
                    Sub("ready", "Launch D2R, click Battle.net Play if needed, and skip intro screens", Account()),
                    Sub("lobby", "Select character and open Lobby", Account(), CharacterSlot()),
                    Sub("play", "Select character and click Play", Account(), CharacterSlot()),
                    Sub("join-game", "Open Lobby and join a named game", Account(), GameName(), Password(), Difficulty(), CharacterSlot()),
                    Sub("create-game", "Open Lobby and create a game", Account(), GameName(), Password(), Difficulty(), CharacterSlot()),
                    Sub("follow", "Join off a friend from the Lobby friends drawer", Account(), CharacterSlot(), FriendRow()),
                    Sub("save-exit", "Open the in-game menu and click Save and Exit", Account()),
                    Sub("leave", "Alias for Save and Exit", Account()),
                    Sub("create-game-all", "First account creates a game, then the rest join it", GameName(), Password(), Difficulty(), CharacterSlot(), Watch()),
                    Sub("join-all", "Stagger all accounts into a named game", GameName(), Password(), Difficulty(), CharacterSlot(), Watch()),
                    Sub("template", "Set the auto-naming template create-game-all/join-all use when no name is given (netrunner1, netrunner2, ...)", RequiredGameName(), Password()),
                    Sub("join-auto", "Auto-join the template's current numbered game, wait for someone to leave, leave, advance, repeat - until stopped", CharacterSlot(), Delay(), StopFlag()),
                    Sub("follow-all", "Stagger all accounts into a friend's game", CharacterSlot(), FriendRow()),
                    Sub("save-exit-all", "Stagger Save and Exit across all accounts"),
                    Sub("leave-all", "Alias for Save and Exit across all accounts"),
                    Sub("quit-all", "Stagger Alt+F4 quit across all online accounts"),
                    Sub("close-all", "Alias for quit-all"),
                    Sub("start-all", "Queue staggered ready flows for all configured accounts"),
                    Sub("health", "Show controller and agent connection health"))
                .Build(),
            new SlashCommandBuilder()
                .WithName("vm")
                .WithDescription("Operate mapped Hyper-V virtual machines")
                .AddOptions(
                    Sub("status", "Get Hyper-V status for an account VM", Account()),
                    Sub("start", "Start an account VM", Account()),
                    Sub("stop", "Stop an account VM", Account()),
                    Sub("reboot", "Restart an account VM", Account()),
                    Sub("snapshot", "Create a Hyper-V checkpoint for an account VM", Account(), SnapshotName()))
                .Build(),
            new SlashCommandBuilder()
                .WithName("game")
                .WithDescription("Track the current D2R game details")
                .AddOptions(
                    Sub("set", "Store the current game name/password for clients to join",
                        RequiredGameName(),
                        Password(),
                        Difficulty(),
                        Notes()),
                    Sub("show", "Show the stored game details"),
                    Sub("clear", "Clear the stored game details"))
                .Build(),
            new SlashCommandBuilder()
                .WithName("config")
                .WithDescription("Configure the D2R controller")
                .AddOptions(
                    Sub("show", "Show runtime controller config"),
                    Sub("stagger", "Persist all-client stagger seconds and restart the host",
                        Seconds("seconds", "Delay between all-client actions, in seconds")),
                    Sub("notifications", "Persist game session notification settings and restart the host",
                        BoolOption("enabled", "Whether to post game session updates"),
                        StringOption("channel-id", "Discord text channel ID for session updates", required: false)))
                .Build(),
            new SlashCommandBuilder()
                .WithName("restart")
                .WithDescription("Respawn D2RHost so startup self-update can apply")
                .Build()
        ];
    }

    private static SlashCommandOptionBuilder Sub(
        string name,
        string description,
        params SlashCommandOptionBuilder[] options)
    {
        var builder = new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.SubCommand);

        foreach (var option in options)
        {
            builder.AddOption(option);
        }

        return builder;
    }

    private static SlashCommandOptionBuilder Account()
    {
        return StringOption("account", "Configured account key, for example hc1", required: true);
    }

    private static SlashCommandOptionBuilder OptionalAccount()
    {
        return StringOption("account", "Configured account key, for example hc1", required: false);
    }

    private static SlashCommandOptionBuilder GameName()
    {
        return StringOption("name", "Game name; defaults to /game show value", required: false);
    }

    private static SlashCommandOptionBuilder RequiredGameName()
    {
        return StringOption("name", "Game name", required: true);
    }

    private static SlashCommandOptionBuilder Password()
    {
        return StringOption("password", "Game password; defaults to /game show value", required: false);
    }

    private static SlashCommandOptionBuilder Notes()
    {
        return StringOption("notes", "Optional short note, for example join off friend instead", required: false);
    }

    private static SlashCommandOptionBuilder SnapshotName()
    {
        return StringOption("name", "Optional snapshot/checkpoint name", required: false);
    }

    private static SlashCommandOptionBuilder Difficulty()
    {
        return StringOption("difficulty", "Game difficulty; defaults to /game show value or VM UI default", required: false)
            .AddChoice("Normal", "normal")
            .AddChoice("Nightmare", "nightmare")
            .AddChoice("Hell", "hell");
    }

    private static SlashCommandOptionBuilder CharacterSlot()
    {
        return new SlashCommandOptionBuilder()
            .WithName("character-slot")
            .WithDescription("Character slot to select, 1-8; defaults to VM config")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(false)
            .WithMinValue(1)
            .WithMaxValue(8);
    }

    private static SlashCommandOptionBuilder FriendRow()
    {
        return new SlashCommandOptionBuilder()
            .WithName("friend-row")
            .WithDescription("Visible friends drawer row to right-click; defaults to VM config")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(false)
            .WithMinValue(1)
            .WithMaxValue(20);
    }

    private static SlashCommandOptionBuilder Seconds(string name, string description)
    {
        return new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(true)
            .WithMinValue(0)
            .WithMaxValue(300);
    }

    private static SlashCommandOptionBuilder Delay()
    {
        return new SlashCommandOptionBuilder()
            .WithName("delay")
            .WithDescription("Seconds to wait before each join attempt; default 0. Also used as the wait between retry attempts.")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(false)
            .WithMinValue(0)
            .WithMaxValue(600);
    }

    private static SlashCommandOptionBuilder StopFlag()
    {
        return new SlashCommandOptionBuilder()
            .WithName("stop")
            .WithDescription("Stop a running join-auto loop instead of starting one")
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(false);
    }

    private static SlashCommandOptionBuilder Watch()
    {
        return new SlashCommandOptionBuilder()
            .WithName("watch")
            .WithDescription("Post a live-updating diagnostics message (frame/click attempts) while this runs")
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(false);
    }

    private static SlashCommandOptionBuilder BoolOption(string name, string description)
    {
        return new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(true);
    }

    private static SlashCommandOptionBuilder StringOption(
        string name,
        string description,
        bool required)
    {
        return new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(required);
    }
}
