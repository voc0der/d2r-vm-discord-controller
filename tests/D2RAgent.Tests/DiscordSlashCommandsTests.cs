using AgentCommon;
using Discord;
using Xunit;

namespace D2RAgent.Tests;

public sealed class DiscordSlashCommandsTests
{
    [Fact]
    public void NoDescriptionExceedsDiscordsHundredCharacterLimit()
    {
        foreach (var command in DiscordSlashCommands.Build())
        {
            var slash = Assert.IsType<SlashCommandProperties>(command);
            AssertDescription(slash.Name.Value, slash.Description.Value);

            if (slash.Options.IsSpecified)
            {
                foreach (var option in slash.Options.Value)
                {
                    AssertOptionDescriptions(slash.Name.Value, option);
                }
            }
        }
    }

    [Fact]
    public void NoCommandExceedsDiscordsTwentyFiveOptionLimit()
    {
        foreach (var command in DiscordSlashCommands.Build())
        {
            var slash = Assert.IsType<SlashCommandProperties>(command);
            var count = slash.Options.IsSpecified ? slash.Options.Value.Count : 0;
            Assert.True(count <= 25, $"/{slash.Name.Value} has {count} options, over Discord's 25-option cap.");
        }
    }

    [Fact]
    public void FollowCommandIncludesBooleanWatchOption()
    {
        var d2r = DiscordSlashCommands.Build()
            .Select(Assert.IsType<SlashCommandProperties>)
            .Single(command => command.Name.Value == "d2r");
        var follow = Assert.Single(d2r.Options.Value, option => option.Name == "follow");
        var watch = Assert.Single(follow.Options!, option => option.Name == "watch");

        Assert.Equal(ApplicationCommandOptionType.Boolean, watch.Type);
    }

    [Fact]
    public void FoldedCommandsExposeAllFlagOnD2R()
    {
        var d2r = GetD2RCommand();
        foreach (var name in new[] { "start", "quit", "join", "create-game", "follow", "save-exit" })
        {
            var command = Assert.Single(d2r.Options.Value, option => option.Name == name);
            var all = Assert.Single(command.Options!, option => option.Name == "all");

            Assert.Equal(ApplicationCommandOptionType.Boolean, all.Type);
        }
    }

    [Fact]
    public void D2RCommandIncludesNestedCommandGroups()
    {
        var d2r = GetD2RCommand();
        var config = Assert.Single(d2r.Options.Value, option => option.Name == "config");
        var game = Assert.Single(d2r.Options.Value, option => option.Name == "game");
        var system = Assert.Single(d2r.Options.Value, option => option.Name == "system");
        var vm = Assert.Single(d2r.Options.Value, option => option.Name == "vm");
        var restart = Assert.Single(d2r.Options.Value, option => option.Name == "restart");

        Assert.Equal(ApplicationCommandOptionType.SubCommand, restart.Type);
        Assert.Equal(ApplicationCommandOptionType.SubCommandGroup, config.Type);
        Assert.Contains(config.Options!, option => option.Name == "stagger");
        var notifications = Assert.Single(config.Options!, option => option.Name == "notifications");
        var updateNotifications = Assert.Single(notifications.Options!, option => option.Name == "updates-enabled");
        Assert.Equal(ApplicationCommandOptionType.Boolean, updateNotifications.Type);
        Assert.Equal(ApplicationCommandOptionType.SubCommandGroup, game.Type);
        Assert.Contains(game.Options!, option => option.Name == "set");
        Assert.Contains(game.Options!, option => option.Name == "show");
        Assert.Contains(game.Options!, option => option.Name == "clear");
        Assert.Equal(ApplicationCommandOptionType.SubCommandGroup, system.Type);
        Assert.Contains(system.Options!, option => option.Name == "sleep");
        Assert.Contains(system.Options!, option => option.Name == "shutdown");
        Assert.Contains(system.Options!, option => option.Name == "restart");
        foreach (var subcommand in system.Options!)
        {
            var metric = Assert.Single(subcommand.Options!, option => option.Name == "metric");
            Assert.Equal(ApplicationCommandOptionType.Boolean, metric.Type);
        }

        Assert.Equal(ApplicationCommandOptionType.SubCommandGroup, vm.Type);
        Assert.Contains(vm.Options!, option => option.Name == "reboot");
    }

    [Fact]
    public void SubcommandsIncludeMetricFlag()
    {
        var d2r = GetD2RCommand();

        foreach (var option in d2r.Options.Value)
        {
            if (option.Type == ApplicationCommandOptionType.SubCommandGroup)
            {
                foreach (var subcommand in option.Options!)
                {
                    AssertMetricFlag($"/d2r {option.Name} {subcommand.Name}", subcommand);
                }
            }
            else if (option.Type == ApplicationCommandOptionType.SubCommand)
            {
                AssertMetricFlag($"/d2r {option.Name}", option);
            }
        }
    }

    [Fact]
    public void LegacyTopLevelAndAllSubcommandsAreNoLongerRegistered()
    {
        var commands = DiscordSlashCommands.Build()
            .Select(Assert.IsType<SlashCommandProperties>)
            .ToArray();
        var commandNames = commands.Select(command => command.Name.Value).ToArray();

        Assert.DoesNotContain("config", commandNames);
        Assert.DoesNotContain("game", commandNames);
        Assert.DoesNotContain("restart", commandNames);
        Assert.DoesNotContain("vm", commandNames);

        var d2r = commands.Single(command => command.Name.Value == "d2r");
        var d2rSubcommands = d2r.Options.Value.Select(option => option.Name).ToArray();
        foreach (var name in new[] { "create-game-all", "join-all", "join-auto", "join-game", "follow-all", "save-exit-all", "quit-all", "start-all", "health" })
        {
            Assert.DoesNotContain(name, d2rSubcommands);
        }
    }

    [Fact]
    public void JoinCommandIncludesAutoFlag()
    {
        var d2r = GetD2RCommand();
        var join = Assert.Single(d2r.Options.Value, option => option.Name == "join");
        var auto = Assert.Single(join.Options!, option => option.Name == "auto");

        Assert.Equal(ApplicationCommandOptionType.Boolean, auto.Type);
    }

    private static void AssertOptionDescriptions(string commandName, ApplicationCommandOptionProperties option)
    {
        AssertDescription($"{commandName} {option.Name}", option.Description);

        if (option.Options is { } nested)
        {
            Assert.True(nested.Count <= 25, $"/{commandName} {option.Name} has {nested.Count} options, over Discord's 25-option cap.");

            foreach (var child in nested)
            {
                AssertOptionDescriptions($"{commandName} {option.Name}", child);
            }
        }
    }

    private static void AssertDescription(string path, string description)
    {
        Assert.True(
            description.Length <= 100,
            $"{path} description is {description.Length} chars, over Discord's 100-char cap: \"{description}\"");
    }

    private static void AssertMetricFlag(string path, ApplicationCommandOptionProperties subcommand)
    {
        var metric = Assert.Single(subcommand.Options!, option => option.Name == "metric");
        Assert.Equal(ApplicationCommandOptionType.Boolean, metric.Type);
        Assert.False(metric.IsRequired, $"{path} metric should be optional.");
    }

    private static SlashCommandProperties GetD2RCommand()
    {
        return DiscordSlashCommands.Build()
            .Select(Assert.IsType<SlashCommandProperties>)
            .Single(command => command.Name.Value == "d2r");
    }
}
