namespace AgentCommon;

// Multi-alt bind-in-game stores one serialized nametag fingerprint per line in the agent's
// leader-template.txt; a pre-multi single-template file is simply a one-entry list, so old
// files keep working unmodified. These helpers keep the parsing/mutation rules identical
// between the agent's file I/O and the tests: only lines that parse as valid fingerprints
// survive, order is preserved (bind order is the rolodex order the host reports and locks
// by), and duplicates collapse by exact serialized equality. Near-identical re-captures of
// the same name are NOT deduplicated - they coexist harmlessly because they match and miss
// together, and collapsing them would need a similarity threshold with its own failure modes.
public static class PartyNameFingerprintList
{
    public static IReadOnlyList<string> Normalize(string? content)
    {
        var result = new List<string>();
        foreach (var line in (content ?? "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0
                || PartyNameFingerprint.FromBase64(trimmed) is null
                || result.Contains(trimmed, StringComparer.Ordinal))
            {
                continue;
            }

            result.Add(trimmed);
        }

        return result;
    }

    public static IReadOnlyList<string> Append(IReadOnlyList<string> existing, string fingerprint)
    {
        var trimmed = fingerprint.Trim();
        if (PartyNameFingerprint.FromBase64(trimmed) is null
            || existing.Contains(trimmed, StringComparer.Ordinal))
        {
            return existing;
        }

        var result = new List<string>(existing) { trimmed };
        return result;
    }

    public static IReadOnlyList<string> Remove(IReadOnlyList<string> existing, string fingerprint)
    {
        var trimmed = fingerprint.Trim();
        return existing.Where(entry => !string.Equals(entry, trimmed, StringComparison.Ordinal)).ToArray();
    }

    public static string Serialize(IReadOnlyList<string> fingerprints)
    {
        return string.Join("\n", fingerprints);
    }
}
