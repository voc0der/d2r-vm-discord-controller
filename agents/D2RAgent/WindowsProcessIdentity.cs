namespace D2RAgent;

internal static class WindowsProcessIdentity
{
    private static readonly string[] DefaultD2RProcessNames = ["D2R"];
    private const string DiabloWindowTitle = "Diablo II: Resurrected";

    public static string[] GetConfiguredProcessNames(
        string primaryProcessName,
        IEnumerable<string>? additionalProcessNames)
    {
        return NormalizeProcessNames(new[] { primaryProcessName }.Concat(additionalProcessNames ?? []));
    }

    public static string[] GetD2RProcessNames(
        string primaryProcessName,
        IEnumerable<string>? additionalProcessNames)
    {
        return NormalizeProcessNames(DefaultD2RProcessNames
            .Concat(new[] { primaryProcessName })
            .Concat(additionalProcessNames ?? []));
    }

    public static string[] NormalizeProcessNames(IEnumerable<string>? processNames)
    {
        return (processNames ?? [])
            .Select(NormalizeProcessName)
            .Where(processName => !string.IsNullOrWhiteSpace(processName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string[] GetWindowTitleNeedles(IEnumerable<string> processNames)
    {
        return NormalizeProcessNames(processNames)
            .SelectMany(GetWindowTitleNeedlesForProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // A strict exact-name search that finds nothing is indistinguishable from "not running" -
    // it could just as easily be a renamed/wrapped build. The fallback scan widens to a
    // substring match against every running process, but only against substrings that are
    // actually relevant to what was being searched for: a D2R search must never be satisfied by
    // a Battle.net-shaped process name, and vice versa, or the fallback would misreport one
    // product as the other.
    public static string[] GetFallbackProcessNameNeedles(IEnumerable<string> processNames)
    {
        return NormalizeProcessNames(processNames)
            .SelectMany(GetFallbackProcessNameNeedlesForProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsWindowTitleMatch(IEnumerable<string> processNames, string? actualProcessName, string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return false;
        }

        var names = NormalizeProcessNames(processNames);
        var normalizedProcessName = NormalizeProcessName(actualProcessName);
        if (names.Any(IsD2RProcessName))
        {
            if (!windowTitle.Contains(DiabloWindowTitle, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (IsD2RProcessName(normalizedProcessName))
            {
                return true;
            }

            return string.IsNullOrWhiteSpace(normalizedProcessName)
                && windowTitle.Equals(DiabloWindowTitle, StringComparison.OrdinalIgnoreCase);
        }

        return GetWindowTitleNeedles(names)
            .Any(needle => windowTitle.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCurrentProcess(int processId)
    {
        return processId == Environment.ProcessId;
    }

    public static bool IsD2RProcessName(string? processName)
    {
        var normalized = NormalizeProcessName(processName);
        return normalized.Equals("D2R", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("D2R_", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return "";
        }

        var trimmed = processName.Trim().Trim('"');
        var lastSlash = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        var fileName = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
    }

    private static IEnumerable<string> GetWindowTitleNeedlesForProcessName(string processName)
    {
        if (processName.Equals("D2R", StringComparison.OrdinalIgnoreCase)
            || processName.StartsWith("D2R_", StringComparison.OrdinalIgnoreCase))
        {
            yield return DiabloWindowTitle;
            yield break;
        }

        if (processName.Equals("Battle", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("Battle.net", StringComparison.OrdinalIgnoreCase)
            || processName.StartsWith("Battle.net ", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Battle.net";
        }
    }

    private static IEnumerable<string> GetFallbackProcessNameNeedlesForProcessName(string processName)
    {
        if (IsD2RProcessName(processName))
        {
            yield return "d2r";
            yield return "diablo";
            yield break;
        }

        if (processName.Equals("Battle", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("Battle.net", StringComparison.OrdinalIgnoreCase)
            || processName.StartsWith("Battle.net ", StringComparison.OrdinalIgnoreCase))
        {
            yield return "battle.net";
            yield return "battlenet";
            yield return "blizzard";
        }
    }
}
