namespace AgentCommon;

public static class PlayerCountDropPollPolicy
{
    public const int MinPollSeconds = 6;
    public const int MidPollSeconds = 12;
    public const int MaxPollSeconds = 15;

    private const double FastGameMinutes = 1.0;
    private const double MidGameMinutes = 5.0;
    private const double SlowGameMinutes = 10.0;

    public static TimeSpan GetDelay(DateTimeOffset? autoStartedUtc, int currentGameNumber, DateTimeOffset nowUtc)
    {
        if (autoStartedUtc is null || currentGameNumber <= 1 || nowUtc <= autoStartedUtc.Value)
        {
            return TimeSpan.FromSeconds(MaxPollSeconds);
        }

        var elapsed = nowUtc - autoStartedUtc.Value;
        return GetDelay(TimeSpan.FromTicks(elapsed.Ticks / currentGameNumber));
    }

    public static TimeSpan GetDelay(TimeSpan averageGameLength)
    {
        if (averageGameLength <= TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(MaxPollSeconds);
        }

        var averageMinutes = averageGameLength.TotalMinutes;
        var seconds = averageMinutes switch
        {
            <= FastGameMinutes => MinPollSeconds,
            <= MidGameMinutes => Lerp(
                MinPollSeconds,
                MidPollSeconds,
                (averageMinutes - FastGameMinutes) / (MidGameMinutes - FastGameMinutes)),
            <= SlowGameMinutes => Lerp(
                MidPollSeconds,
                MaxPollSeconds,
                (averageMinutes - MidGameMinutes) / (SlowGameMinutes - MidGameMinutes)),
            _ => MaxPollSeconds
        };

        return TimeSpan.FromSeconds(Math.Clamp(
            (int)Math.Round(seconds, MidpointRounding.AwayFromZero),
            MinPollSeconds,
            MaxPollSeconds));
    }

    private static double Lerp(int start, int end, double amount)
    {
        return start + ((end - start) * amount);
    }
}
