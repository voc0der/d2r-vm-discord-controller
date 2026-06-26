namespace AgentCommon;

public static class DiscordMessageTruncator
{
    public const int DiscordContentLimit = 2000;
    private const string Suffix = "... (truncated)";

    // Aggregating multiple accounts' failure text into one message (join-auto's per-attempt and
    // leave-failure reports) has no natural bound - a few accounts each returning a verbose
    // status-line-bearing exception message is enough to exceed Discord's hard 2000-char content
    // cap, which throws and previously took the whole loop down with it instead of just losing
    // some detail off the end of one message.
    public static string Truncate(string content, int limit = DiscordContentLimit)
    {
        if (content.Length <= limit)
        {
            return content;
        }

        var keep = Math.Max(limit - Suffix.Length, 0);
        return content[..keep] + Suffix;
    }
}
