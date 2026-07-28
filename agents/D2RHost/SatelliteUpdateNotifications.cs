using AgentCommon;

namespace D2RHost;

internal static class SatelliteUpdateNotifications
{
    public static string FormatStarted(
        string agentId,
        bool isNode,
        string? reportedVersion,
        string? currentVersion,
        string? latestVersion,
        string? logPath)
    {
        var kind = isNode ? "D2RHost worker node" : "D2R VM Agent";
        var versions = !string.IsNullOrWhiteSpace(currentVersion)
            && !string.IsNullOrWhiteSpace(latestVersion)
                ? $" {currentVersion} -> {latestVersion}"
                : "";
        var log = string.IsNullOrWhiteSpace(logPath) ? "" : $"\nLog: `{logPath}`";
        var bootstrap = isNode && WorkerNodeCompatibility.NeedsSelfUpdateBootstrap(reportedVersion)
            ? $"\nThis worker is still running v{AgentVersion.Display(reportedVersion)}, whose updater "
                + "cannot stop the host process. Restart just that PC once with "
                + $"`/d2r system restart node:{agentId}` (or reboot it locally); its startup updater "
                + "will then apply the latest release."
            : "";
        return $"{kind} update started for `{agentId}`{versions}.{log}{bootstrap}";
    }
}
