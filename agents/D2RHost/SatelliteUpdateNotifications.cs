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
                + "cannot stop the host process. Stop its VMs and reboot that PC locally once (or "
                + "replace the worker files manually); its startup updater will then apply the latest "
                + "release. The master deliberately will not send a remote host restart until the "
                + "worker advertises VM-safe power transitions."
            : "";
        return $"{kind} update started for `{agentId}`{versions}.{log}{bootstrap}";
    }
}
