namespace D2RAgent;

internal static class WindowsProcessIdentity
{
    private static readonly string[] DefaultD2RProcessNames = ["D2R"];

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

    public static bool IsCurrentProcess(int processId)
    {
        return processId == Environment.ProcessId;
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
            yield return "Diablo II: Resurrected";
            yield break;
        }

        if (processName.Equals("Battle", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("Battle.net", StringComparison.OrdinalIgnoreCase)
            || processName.StartsWith("Battle.net ", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Battle.net";
        }
    }
}
