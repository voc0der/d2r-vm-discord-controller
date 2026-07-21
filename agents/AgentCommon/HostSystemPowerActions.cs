using System.Diagnostics;

namespace AgentCommon;

public enum HostSystemPowerAction
{
    Sleep,
    Shutdown,
    Restart
}

public static class HostSystemPowerActions
{
    public static HostSystemPowerAction ParseAction(string subcommandName)
    {
        return subcommandName switch
        {
            "sleep" => HostSystemPowerAction.Sleep,
            "shutdown" => HostSystemPowerAction.Shutdown,
            "restart" => HostSystemPowerAction.Restart,
            _ => throw new InvalidOperationException($"Unsupported system subcommand: {subcommandName}")
        };
    }

    public static string FormatQueuedMessage(HostSystemPowerAction action)
    {
        return action switch
        {
            HostSystemPowerAction.Sleep => "Putting the D2RHost Windows machine to sleep.",
            HostSystemPowerAction.Shutdown => "Shutting down the D2RHost Windows machine.",
            HostSystemPowerAction.Restart => "Restarting the D2RHost Windows machine.",
            _ => throw new InvalidOperationException($"Unsupported system action: {action}")
        };
    }

    public static string FormatDiscordAnnouncement(HostSystemPowerAction action, string requestedBy)
    {
        var suffix = string.IsNullOrWhiteSpace(requestedBy)
            ? ""
            : $" Requested by {requestedBy.Trim()}.";

        return action switch
        {
            HostSystemPowerAction.Sleep => $"D2RHost sleep requested.{suffix}",
            HostSystemPowerAction.Shutdown => $"D2RHost shutdown requested.{suffix}",
            HostSystemPowerAction.Restart => $"D2RHost restart requested.{suffix}",
            _ => throw new InvalidOperationException($"Unsupported system action: {action}")
        };
    }

    public static ProcessStartInfo CreateShutdownStartInfo(HostSystemPowerAction action)
    {
        var mode = action switch
        {
            HostSystemPowerAction.Shutdown => "/s",
            HostSystemPowerAction.Restart => "/r",
            _ => throw new InvalidOperationException($"{action} does not use shutdown.exe.")
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add("/t");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("D2ROps requested a D2RHost system power action.");
        return startInfo;
    }
}
