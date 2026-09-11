using Discord;

namespace AgentCommon;

public static class DiscordSlashCommands
{
    public static ApplicationCommandProperties[] Build()
    {
        return
        [
            // Discord caps each level at 25 options and the entire command tree at 8000
            // characters across names, descriptions and choice values. Repeated option
            // descriptions count every time. Keep them terse: exceeding either limit rejects
            // registration and leaves the old commands in place. Tests enforce both limits.
            new SlashCommandBuilder()
                .WithName("d2r")
                .WithDescription("Control D2R clients")
                .AddOptions(
                    Sub("status", "Show host health and one or all client statuses", OptionalAccount()),
                    Sub("start", "Launch one client; all:true readies all", OptionalAccount(), AllFlag()),
                    Sub("stop", "Kill D2R for an account", Account()),
                    Sub("quit", "Close D2R with Alt+F4", OptionalAccount(), AllFlag()),
                    Sub("restart-client", "Restart D2R for an account", Account()),
                    Sub("screenshot", "Capture the VM screen", Account()),
                    Sub("remote", "Show the VM remote-control URL", Account()),
                    Sub("ready", "Launch D2R and skip intros; omit account for all online", OptionalAccount()),
                    Sub("lobby", "Select character and open Lobby", Account(), CharacterSlot()),
                    Sub("play", "Select character and click Play", Account(), CharacterSlot()),
                    Sub("join", "Join a game; auto:true follows the naming template", OptionalAccount(), AllFlag(), JoinAutoFlag(), GameName(), Password(), Difficulty(), CharacterSlot(), Delay(), IdleMinutes(), JoinAutoWatch()),
                    Sub("create-game", "Create a game; all:true joins all accounts", OptionalAccount(), AllFlag(), GameName(), Password(), Difficulty(), CharacterSlot(), Watch()),
                    Sub("follow", "Follow or bind a friend, or join by row", OptionalAccount(), AllFlag(), CharacterSlot(), FriendRow(), FollowBind(), FollowBindInGame(), FollowAutoFlag(), FollowBots(), Delay(), IdleMinutes(), Watch()),
                    Sub("dclone", "Park bots in separate games for a Diablo Clone hunt", DcloneBots(), DcloneDifficulty(), StopFlag(), Watch()),
                    Sub("save-exit", "Save and exit the game", OptionalAccount(), AllFlag()),
                    Sub("template", "Set the create/join naming template", RequiredGameName(), Password()),
                    Sub("restart", "Restart D2RHost and apply updates"),
                    Group("game", "Manage stored game details",
                        Sub("set", "Store game details for clients to join",
                            RequiredGameName(),
                            Password(),
                            Difficulty(),
                            Notes()),
                        Sub("show", "Show stored game details"),
                        Sub("clear", "Clear stored game details")),
                    Group("system", "Control host machine power",
                        Sub("sleep", "Sleep all online hosts; node/all:false targets one", NodeTarget(), AllNodesFlag()),
                        Sub("shutdown", "Shut down one or all online hosts", NodeTarget(), AllNodesFlag()),
                        Sub("restart", "Restart one or all online hosts", NodeTarget(), AllNodesFlag())),
                    Group("config", "Configure the controller",
                        Sub("show", "Show active config"),
                        Sub("stagger", "Save action spacing and restart host",
                            Seconds("seconds", "Seconds between client actions")),
                        Sub("api", "Enable/disable the HTTP API and issue its key",
                            BoolOption("enabled", "Allow HTTP control of this host"),
                            BoolOption("overwrite", "Replace an existing key, invalidating it", required: false)),
                        Sub("notifications", "Save notification settings and restart host",
                            BoolOption("enabled", "Post game session updates"),
                            StringOption("channel-id", "Notification text channel ID", required: false),
                            BoolOption("updates-enabled", "Post host availability and update notices", required: false))),
                    Group("vm", "Control Hyper-V VMs",
                        Sub("status", "Show an account VM's Hyper-V status", Account()),
                        Sub("start", "Start an account VM", Account()),
                        Sub("stop", "Stop an account VM", Account()),
                        Sub("turnoff", "Force off an account VM; unsaved work is lost", Account()),
                        Sub("reboot", "Restart an account VM", Account()),
                        Sub("snapshot", "Checkpoint an account VM", Account(), SnapshotName())))
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
        return StringOption("account", "Account key (e.g. hc1)", required: true);
    }

    private static SlashCommandOptionBuilder OptionalAccount()
    {
        return StringOption("account", "Account key (e.g. hc1)", required: false);
    }

    private static SlashCommandOptionBuilder NodeTarget()
    {
        return StringOption("node", "Target host node ID", required: false);
    }

    private static SlashCommandOptionBuilder AllNodesFlag()
    {
        return BoolOption(
            "all",
            "All online hosts; default true for sleep, otherwise false",
            required: false);
    }

    private static SlashCommandOptionBuilder GameName()
    {
        return StringOption("name", "Game name; default: /d2r game show", required: false);
    }

    private static SlashCommandOptionBuilder RequiredGameName()
    {
        return StringOption("name", "Game name", required: true);
    }

    private static SlashCommandOptionBuilder Password()
    {
        return StringOption("password", "Game password; default: /d2r game show", required: false);
    }

    private static SlashCommandOptionBuilder Notes()
    {
        return StringOption("notes", "Optional game note", required: false);
    }

    private static SlashCommandOptionBuilder SnapshotName()
    {
        return StringOption("name", "Optional checkpoint name", required: false);
    }

    private static SlashCommandOptionBuilder Difficulty()
    {
        return StringOption("difficulty", "Difficulty; default: /d2r game show or VM UI", required: false)
            .AddChoice("Normal", "normal")
            .AddChoice("Nightmare", "nightmare")
            .AddChoice("Hell", "hell");
    }

    private static SlashCommandOptionBuilder CharacterSlot()
    {
        return new SlashCommandOptionBuilder()
            .WithName("character-slot")
            .WithDescription("Character slot 1-8; default: VM config")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(false)
            .WithMinValue(1)
            .WithMaxValue(8);
    }

    private static SlashCommandOptionBuilder FriendRow()
    {
        return new SlashCommandOptionBuilder()
            .WithName("friend-row")
            .WithDescription("Friend row to follow/bind; default: VM config")
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
            .WithDescription("Seconds before each join/retry; default 0")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(false)
            .WithMinValue(0)
            .WithMaxValue(600);
    }

    private static SlashCommandOptionBuilder StopFlag()
    {
        return new SlashCommandOptionBuilder()
            .WithName("stop")
            .WithDescription("Stop the current loop")
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(false);
    }

    // Bots, not players, and unrelated to FollowBots' 7: a dclone park gives every bot its own
    // game, so nothing about D2R's 8-player cap bounds this. It caps the roster, which otherwise
    // keeps growing as VMs connect - a worker node waking mid-park has its VMs parked too.
    private static SlashCommandOptionBuilder DcloneBots()
    {
        return new SlashCommandOptionBuilder()
            .WithName("bots")
            .WithDescription("Max bots to park, one game each; default: every VM, even late ones")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(false)
            .WithMinValue(1)
            .WithMaxValue(64);
    }

    // Its own option rather than Difficulty(): a dclone park always means Hell, and inheriting
    // whatever /d2r game set last stored would silently open 8 Normal games instead.
    private static SlashCommandOptionBuilder DcloneDifficulty()
    {
        return StringOption("difficulty", "Parked game difficulty; default Hell", required: false)
            .AddChoice("Normal", "normal")
            .AddChoice("Nightmare", "nightmare")
            .AddChoice("Hell", "hell");
    }

    private static SlashCommandOptionBuilder AllFlag()
    {
        return new SlashCommandOptionBuilder()
            .WithName("all")
            .WithDescription("All online accounts; default true")
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(false);
    }

    private static SlashCommandOptionBuilder IdleMinutes()
    {
        return new SlashCommandOptionBuilder()
            .WithName("idle-minutes")
            .WithDescription("Minutes to retry the next game before stopping; default 60")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(false)
            .WithMinValue(1)
            .WithMaxValue(600);
    }

    private static SlashCommandOptionBuilder FollowBind()
    {
        return new SlashCommandOptionBuilder()
            .WithName("bind")
            .WithDescription("Bind selected friend-row (true) or clear binding (false)")
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(false);
    }

    private static SlashCommandOptionBuilder FollowBindInGame()
    {
        return new SlashCommandOptionBuilder()
            .WithName("bind-in-game")
            .WithDescription("Bind party-bar leader 1-7; repeat per alt, 0 clears all")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(false)
            .WithMinValue(0)
            .WithMaxValue(7);
    }

    private static SlashCommandOptionBuilder FollowAutoFlag()
    {
        return new SlashCommandOptionBuilder()
            .WithName("auto")
            .WithDescription("Auto-follow bound friend on all accounts: true=start, false=stop")
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(false);
    }

    // Bots, not players: D2R caps a game at 8 and the leader being followed holds one of those
    // slots, so 7 fills the game. The -1 / +1 buttons on the live monitor change this mid-run.
    private static SlashCommandOptionBuilder FollowBots()
    {
        return new SlashCommandOptionBuilder()
            .WithName("bots")
            .WithDescription("Bots in leader's game, 1-7; default 7 (full party)")
            .WithType(ApplicationCommandOptionType.Integer)
            .WithRequired(false)
            .WithMinValue(1)
            .WithMaxValue(7);
    }

    private static SlashCommandOptionBuilder JoinAutoFlag()
    {
        return new SlashCommandOptionBuilder()
            .WithName("auto")
            .WithDescription("Template auto-join: true=start, false=stop")
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(false);
    }

    private static SlashCommandOptionBuilder JoinAutoWatch()
    {
        return new SlashCommandOptionBuilder()
            .WithName("watch")
            .WithDescription("Also report failed join/leave attempts")
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(false);
    }

    private static SlashCommandOptionBuilder Watch()
    {
        return new SlashCommandOptionBuilder()
            .WithName("watch")
            .WithDescription("Show live frame/click diagnostics")
            .WithType(ApplicationCommandOptionType.Boolean)
            .WithRequired(false);
    }

    private static SlashCommandOptionBuilder MetricFlag()
    {
        return new SlashCommandOptionBuilder()
            .WithName("metric")
            .WithDescription("Host/VM RAM/CPU; defaults to false")
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
