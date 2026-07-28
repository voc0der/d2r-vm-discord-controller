using AgentCommon;

namespace D2RHost;

/// <summary>
/// Compatibility boundaries for commands whose older worker implementations acknowledged work
/// they could not finish.
/// </summary>
internal static class WorkerNodeCompatibility
{
    internal const string ReliableSleepVersion = "0.2.217";
    internal const string SelfUpdateExitVersion = "0.2.220";

    /// <summary>
    /// Returns an operator-facing reason a worker must not be trusted with sleep, or null when its
    /// reported build contains the synchronous privilege/capability preparation path.
    /// </summary>
    public static string? GetSleepBlockReason(string nodeId, string? reportedVersion)
    {
        if (!AgentVersion.TryCompareReleases(
                reportedVersion,
                ReliableSleepVersion,
                out var comparison))
        {
            return "Worker version is unknown, so the master cannot verify that it contains the "
                + $"v{ReliableSleepVersion} sleep fix. Restart just that worker once with "
                + $"`/d2r system restart node:{nodeId}`, then retry fleet sleep.";
        }

        if (comparison >= 0)
        {
            return null;
        }

        var version = AgentVersion.Display(reportedVersion);
        var updateHint = AgentVersion.IsOlderRelease(version, SelfUpdateExitVersion)
            ? " Its pre-v0.2.220 updater cannot stop the running worker process by itself."
            : "";
        return $"Worker is running D2RHost v{version}, which predates the v{ReliableSleepVersion} "
            + $"sleep fix and cannot reliably suspend.{updateHint} Restart just that worker once "
            + $"with `/d2r system restart node:{nodeId}` (or reboot that PC) so its startup updater "
            + "can apply the latest release, then retry.";
    }

    public static bool NeedsSelfUpdateBootstrap(string? reportedVersion)
    {
        return AgentVersion.IsOlderRelease(reportedVersion, SelfUpdateExitVersion);
    }
}
