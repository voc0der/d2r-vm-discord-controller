using AgentCommon;

namespace D2RHost;

/// <summary>
/// Compatibility boundaries used only for the legacy worker self-update bootstrap path. Physical
/// host power safety is capability-negotiated from the current heartbeat, never version-guessed.
/// </summary>
internal static class WorkerNodeCompatibility
{
    internal const string SelfUpdateExitVersion = "0.2.220";

    public static bool NeedsSelfUpdateBootstrap(string? reportedVersion)
    {
        return AgentVersion.IsOlderRelease(reportedVersion, SelfUpdateExitVersion);
    }
}
