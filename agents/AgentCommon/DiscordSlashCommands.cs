using Discord;

namespace AgentCommon;

public static class DiscordSlashCommands
{
    public static ApplicationCommandProperties[] Build()
    {
        return
        [
            // Discord hard-caps a command at 25 options, and each Sub(...) below counts as one.
            // Past 25, the registration call in DiscordBot.OnReadyAsync fails closed: Discord
            // keeps the previously-registered set and the new/changed subcommands just never
            // appear. Stay under 25, or split overflow into a subcommand group / second
            // top-level command. (Not what caused the template/join-auto outage below - see
            // the comment on Sub() for that one - but a real second way to hit the same
            // "command silently never registers" symptom, so keeping the guard.)
            new SlashCommandBuilder()
                .WithName("d2r")
                .WithDescription("Operate Diablo II: Resurrected clients")
                .AddOptions(
                    Sub("status", "Show controller health plus one account or all account client statuses", OptionalAccount()),
                    Sub("start", "Launch one account, or ready all accounts when all is true", OptionalAccount(), AllFlag()),
                    Sub("stop", "Kill the D2R process for an account", Account()),
                    Sub("quit", "Focus D2R and close it with Alt+F4", OptionalAccount(), AllFlag()),
                    Sub("restart-client", "Restart the D2R process for an account", Account()),
                    Sub("screenshot", "Capture the VM's current primary-screen screenshot", Account()),
                    Sub("remote", "Show the configured remote-control URL for an account VM", Account()),
                    Sub("ready", "Launch D2R and skip intros for one account, or all online accounts when omitted", OptionalAccount()),
                    Sub("lobby", "Select character and open Lobby", Account(), CharacterSlot()),
                    Sub("play", "Select character and click Play", Account(), CharacterSlot()),
                    Sub("join", "Join a game, or auto-join template games when auto is true", OptionalAccount(), AllFlag(), JoinAutoFlag(), GameName(), Password(), Difficulty(), CharacterSlot(), Delay(), IdleMinutes(), JoinAutoWatch()),
                    Sub("create-game", "Create one game, or create and join across all accounts", OptionalAccount(), AllFlag(), GameName(), Password(), Difficulty(), CharacterSlot(), Watch()),
                    Sub("follow", "Follow the bound friend, bind a friend, or join by visible row", OptionalAccount(), AllFlag(), CharacterSlot(), FriendRow(), FollowBind(), FollowBindInGame(), FollowAutoFlag(), Delay(), IdleMinutes(), Watch()),
                    Sub("save-exit", "Open the in-game menu and click Save and Exit", OptionalAccount(), AllFlag()),
                    Sub("template", "Set the create/join auto-naming template", RequiredGameName(), Password()),
                    Sub("restart", "Respawn D2RHost so startup self-update can apply"),
                    Group("game", "Track the current D2R game details",
                        Sub("set", "Store the current game name/password for clients to join",
                            RequiredGameName(),
                            Password(),
                            Difficulty(),
                            Notes()),
                        Sub("show", "Show the stored game details"),
                        Sub("clear", "Clear the stored game details")),
                    Group("system", "Power actions on the D2RHost Windows machine",
                        Sub("sleep", "Sleep the D2RHost Windows machine"),
                        Sub("shutdown", "Shut down the D2RHost Windows machine"),
                        Sub("restart", "Restart the D2RHost Windows machine")),
                    Group("config", "Configure the D2R controller",
                        Sub("show", "Show runtime controller config"),
                        Sub("stagger", "Persist all-client stagger seconds and restart the host",
                            Seconds("seconds", "Delay between all-client actions, in seconds")),
                        Sub("notifications", "Persist Discord notification settings and restart the host",
                            BoolOption("enabled", "Whether to post game session updates"),
                            StringOption("channel-id", "Discord text channel ID for notifications", required: false),
                            BoolOption("updates-enabled", "Whether to post host availability/update notifications", required: false))),
                    Group("vm", "Operate mapped Hyper-V virtual machines",
                        Sub("status", "Get Hyper-V status for an account VM", Account()),
                        Sub("start", "Start an account VM", Account()),
                        Sub("stop", "Stop an account VM", Account()),
                        Sub("reboot", "Restart an account VM", Account()),
                        Sub("snapshot", "Create a Hyper-V checkpoint for an account VM", Account(), SnapshotName())))
                .Build()
        ];
    }

    // description is capped at 100 chars by Discord (Discord.Net throws ArgumentException
    // synchronously from WithDescription, before any network call). Build() runs inside
    // DiscordBot.OnReadyAsync, a Discord.NET gateway event handler - an exception there is
    // swallowed into "A Ready handler has thrown an unhandled exception" with no other visible
    // error, so the whole command set silently never registers. Bit us once already
    // (issue #20 follow-up, template/join-auto both ran past 100 chars).
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

        builder.AddOption(MetricFlag());
        return builder;
    }

    private static SlashCommandOptionBuilder Group(
        string name,
        string description,
        params SlashCommandOptionBuilder[] subcommands)
    {
        var builder = new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.SubCommandGroup);

        foreach (var subcommand in subcommands)
        {
            builder.AddOption(subcommand);
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
        return StringOption("name", "Game name; defaults to /d2r game show value", required: false);
    }

    private static SlashCommandOptionBuilder RequiredGameName()
    {
        return StringOption("name", "Game name", required: true);
    }

    private static SlashCommandOptionBuilder Password()
    {
        return StringOption("password", "Game password; defaults to /d2r game show value", required: false);
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
        return StringOption("difficulty", "Game difficulty; defaults to /d2r game show value or VM UI default", required: false)
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
            .WithDescription("Visible friends drawer row for follow/bind; defaults to VM config")
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

    private static SlashCommandOptionBuilder AllFlag()
    {
        return new SlashCommandOptionBuilder()
            .WithName("all")
            .WithDescription("Run across all online accounts; defaults to true")
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(false);
    }

    private static SlashCommandOptionBuilder IdleMinutes()
    {
        return new SlashCommandOptionBuilder()
            .WithName("idle-minutes")
            .WithDescription("Minutes to retry joining the next game before giving up and disabling; default 60")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(false)
            .WithMinValue(1)
            .WithMaxValue(600);
    }

    private static SlashCommandOptionBuilder FollowBind()
    {
        return new SlashCommandOptionBuilder()
            .WithName("bind")
            .WithDescription("Capture (true) or clear (false) the friend to auto-follow from selected friend-row")
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(false);
    }

    private static SlashCommandOptionBuilder FollowBindInGame()
    {
        return new SlashCommandOptionBuilder()
            .WithName("bind-in-game")
            .WithDescription("Fingerprint the party-bar name at this position (1-8) as the leader; 0 clears it")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(false)
            .WithMinValue(0)
            .WithMaxValue(8);
    }

    private static SlashCommandOptionBuilder FollowAutoFlag()
    {
        return new SlashCommandOptionBuilder()
            .WithName("auto")
            .WithDescription("Start (true) or stop (false) auto-following the bound friend across all accounts")
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(false);
    }

    private static SlashCommandOptionBuilder JoinAutoFlag()
    {
        return new SlashCommandOptionBuilder()
            .WithName("auto")
            .WithDescription("Start (true) or stop (false) template auto-join")
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(false);
    }

    private static SlashCommandOptionBuilder JoinAutoWatch()
    {
        return new SlashCommandOptionBuilder()
            .WithName("watch")
            .WithDescription("Also post each failed join/leave attempt, not just successes and the final outcome")
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

    private static SlashCommandOptionBuilder MetricFlag()
    {
        return new SlashCommandOptionBuilder()
            .WithName("metric")
            .WithDescription("Append host and VM RAM/CPU telemetry; defaults to false")
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(false);
    }

    private static SlashCommandOptionBuilder BoolOption(string name, string description, bool required = true)
    {
        return new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(required);
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
