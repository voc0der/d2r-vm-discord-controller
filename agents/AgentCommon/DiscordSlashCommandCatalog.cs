using Discord;

namespace AgentCommon;

/// <summary>
/// The registered <c>/d2r</c> surface, read back out of <see cref="DiscordSlashCommands.Build"/>.
/// </summary>
/// <remarks>
/// Build() is the only source of truth for what actually exists - a handler with no entry there is
/// a command Discord never offers, which has fooled us before. Deriving the HTTP API's command list
/// and its validation from the same call means the API cannot drift from Discord either: a
/// subcommand added to one is added to both, and a command removed from Build() stops being
/// callable over HTTP at the same moment it stops appearing in Discord.
/// </remarks>
public static class DiscordSlashCommandCatalog
{
    public static IReadOnlyList<CommandDescriptor> All { get; } = BuildDescriptors();

    /// <summary>
    /// Finds one command by group and name. <paramref name="group"/> is null for a plain
    /// subcommand such as <c>status</c>, or the group name for <c>vm start</c>.
    /// </summary>
    public static CommandDescriptor? Find(string? group, string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        return All.FirstOrDefault(descriptor =>
            string.Equals(descriptor.Command, command, StringComparison.OrdinalIgnoreCase)
            && string.Equals(descriptor.Group ?? "", group ?? "", StringComparison.OrdinalIgnoreCase));
    }

    private static List<CommandDescriptor> BuildDescriptors()
    {
        var descriptors = new List<CommandDescriptor>();
        foreach (var built in DiscordSlashCommands.Build())
        {
            if (built is not SlashCommandProperties slash
                || slash.Name.Value != "d2r"
                || !slash.Options.IsSpecified)
            {
                continue;
            }

            foreach (var option in slash.Options.Value)
            {
                if (option.Type == ApplicationCommandOptionType.SubCommandGroup)
                {
                    foreach (var nested in option.Options ?? [])
                    {
                        descriptors.Add(Describe(option.Name, nested));
                    }
                }
                else if (option.Type == ApplicationCommandOptionType.SubCommand)
                {
                    descriptors.Add(Describe(group: null, option));
                }
            }
        }

        return descriptors;
    }

    private static CommandDescriptor Describe(string? group, ApplicationCommandOptionProperties subcommand)
    {
        var options = (subcommand.Options ?? [])
            .Select(option => new CommandOptionDescriptor(
                option.Name,
                option.Type.ToString(),
                option.IsRequired ?? false,
                option.Description,
                option.Choices is { Count: > 0 } choices
                    ? choices.Select(choice => choice.Value?.ToString() ?? "").ToArray()
                    : null))
            .ToArray();

        return new CommandDescriptor(
            group,
            subcommand.Name,
            group is null ? subcommand.Name : $"{group} {subcommand.Name}",
            subcommand.Description,
            options);
    }
}

public sealed record CommandDescriptor(
    string? Group,
    string Command,
    string Path,
    string Description,
    IReadOnlyList<CommandOptionDescriptor> Options);

public sealed record CommandOptionDescriptor(
    string Name,
    string Type,
    bool Required,
    string Description,
    IReadOnlyList<string>? Choices);
