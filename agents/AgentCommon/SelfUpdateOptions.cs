namespace AgentCommon;

public sealed record SelfUpdateOptions(
    string AppName,
    string AssetName,
    string[] RestartArgs)
{
    public string Owner { get; init; } = "voc0der";
    public string Repository { get; init; } = "d2r-vm-discord-controller";
    public string? RestartScheduledTaskName { get; init; }
    public string? CompletionMarkerPath { get; init; }

    public static SelfUpdateOptions D2RHost(string[] restartArgs)
    {
        return new SelfUpdateOptions("D2RHost", "D2RHost-win-x64.zip", restartArgs)
        {
            RestartScheduledTaskName = "D2R Host Controller"
        };
    }

    public static SelfUpdateOptions D2RAgent(string[] restartArgs)
    {
        return new SelfUpdateOptions("D2RAgent", "D2RAgent-win-x64.zip", restartArgs)
        {
            RestartScheduledTaskName = "D2R VM Agent"
        };
    }
}
