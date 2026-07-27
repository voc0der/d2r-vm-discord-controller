using System.Reflection;

namespace AgentCommon;

/// <summary>
/// The build string a process reports about itself.
/// </summary>
/// <remarks>
/// Agents advertise <see cref="AssemblyInformationalVersionAttribute"/> in their hello frame
/// ("0.2.215"), so anything that displays or compares a fleet member's build has to read the
/// same attribute. The master's own node line used to use <c>Assembly.GetName().Version</c>
/// instead, which renders the four-part "0.2.215.0" - close enough to look right in health
/// output, and different enough that comparing it against a worker's reported string said
/// "behind" for every node in a perfectly up-to-date fleet.
/// </remarks>
public static class AgentVersion
{
    public static string Current()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AgentVersion).Assembly;
        return assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "0.0.0";
    }

    /// <summary>
    /// Whether <paramref name="reported"/> names a build other than <paramref name="expected"/>.
    /// Unknown versions are never called out: an agent too old to advertise one is a separate
    /// problem, and guessing "behind" would put a permanent false warning in health output.
    /// </summary>
    public static bool IsDifferentBuild(string? reported, string? expected)
    {
        return !string.IsNullOrWhiteSpace(reported)
            && !string.IsNullOrWhiteSpace(expected)
            && !string.Equals(Normalize(reported), Normalize(expected), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The version as it should be shown to an operator: the release number, without the commit
    /// SHA a CI publish appends.
    /// </summary>
    /// <remarks>
    /// The raw string is "0.2.218+f07de7001f8f01a10c30b0ea57e3aa76e8a9d022" - forty characters of
    /// SHA per line. With one line per node and per account, that pushed status output past
    /// Discord's message limit and got it truncated, which made the fleet's versions harder to
    /// read than when they were not shown at all.
    /// </remarks>
    public static string Display(string? version)
    {
        return string.IsNullOrWhiteSpace(version) ? "unknown" : Normalize(version);
    }

    /// <summary>
    /// Trims the build metadata a CI publish can append (for example "0.2.215+abc1234"), so a
    /// worker built from the same tag is not reported as a different build than the master.
    /// </summary>
    private static string Normalize(string version)
    {
        var trimmed = version.Trim();
        var plus = trimmed.IndexOf('+');
        return plus < 0 ? trimmed : trimmed[..plus];
    }
}
