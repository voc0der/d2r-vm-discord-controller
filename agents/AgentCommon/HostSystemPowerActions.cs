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

    /// <summary>
    /// Whether a /d2r system subcommand covers every online D2RHost node when the caller
    /// does not scope it. Sleep is the "done for the night" action and the post-follow
    /// Sleep button already parked the whole fleet, so the slash command matches it: a
    /// worker left awake keeps drawing power and running its VMs long after the master
    /// parked, and nobody remembers to type all:true. Shutdown and restart stay
    /// master-only, because those are maintenance aimed at one box.
    /// </summary>
    public static bool DefaultsToEveryNode(HostSystemPowerAction action)
    {
        return action == HostSystemPowerAction.Sleep;
    }

    /// <summary>
    /// Resolves whether to target every online node. An explicit all flag always wins, so
    /// all:false is the escape hatch that keeps sleep on the master alone, and naming a
    /// node keeps the action scoped to it.
    /// </summary>
    public static bool ResolveEveryNode(HostSystemPowerAction action, bool? allFlag, bool nodeRequested)
    {
        return allFlag ?? (!nodeRequested && DefaultsToEveryNode(action));
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

    /// <summary>
    /// Builds the <c>shutdown.exe</c> invocation for an action, including hibernation, which is
    /// how sleep is served on a machine with no legacy S1-S3 standby state.
    /// </summary>
    public static ProcessStartInfo CreateShutdownStartInfo(
        HostSystemPowerAction action,
        bool hibernateForSleep = false)
    {
        var mode = action switch
        {
            HostSystemPowerAction.Shutdown => "/s",
            HostSystemPowerAction.Restart => "/r",
            HostSystemPowerAction.Sleep when hibernateForSleep => "/h",
            _ => throw new InvalidOperationException($"{action} does not use shutdown.exe.")
        };

        if (mode == "/h")
        {
            // shutdown.exe rejects /t and /c alongside /h.
            var hibernateStartInfo = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            hibernateStartInfo.ArgumentList.Add("/h");
            return hibernateStartInfo;
        }

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
