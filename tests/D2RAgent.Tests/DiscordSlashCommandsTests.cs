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
}
