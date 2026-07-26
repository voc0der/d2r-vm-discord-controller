using System.Security.Cryptography;
using System.Text;

namespace AgentCommon;

// The host is the authority for follow-bind fingerprints; every VM agent holds a replica in its
// own follow-template.txt and leader-template.txt. Both sides need to answer "does this agent
// already hold exactly what the host intends?" from a heartbeat field alone, without shipping
// the fingerprints themselves on every status frame.
//
// Canonicalization matters far more than the hash here. An agent whose file picked up a trailing
// newline, or whose leader list is the same nametags in the same order written by an older agent,
// must digest identically to the host's copy - otherwise reconciliation would re-push it forever.
// So the canonical form is exactly what the agent's own loaders already accept: a trimmed
// fingerprint for the friend row, and PartyNameFingerprintList's normalized/serialized form for
// the leader rolodex. Content that does not parse digests as None, which makes an unreadable or
// corrupt file look like "missing" and get repaired by the next push rather than sit undetected.
public static class FollowTemplateDigest
{
    public const string None = "none";

    public static string OfFriendTemplate(string? fingerprint)
    {
        var canonical = (fingerprint ?? "").Trim();
        return canonical.Length == 0 || FriendFingerprint.FromBase64(canonical) is null
            ? None
            : Compute(canonical);
    }

    public static string OfLeaderContent(string? content)
    {
        return OfLeaderList(PartyNameFingerprintList.Normalize(content));
    }

    public static string OfLeaderList(IReadOnlyList<string> fingerprints)
    {
        return fingerprints.Count == 0
            ? None
            : Compute(PartyNameFingerprintList.Serialize(fingerprints));
    }

    public static bool IsPresent(string? digest)
    {
        return !string.IsNullOrWhiteSpace(digest)
            && !string.Equals(digest, None, StringComparison.Ordinal);
    }

    // Truncated deliberately: this only ever distinguishes "same content" from "different
    // content" between two cooperating processes, and a short digest keeps the heartbeat small
    // and the monitor lines readable.
    private static string Compute(string canonical)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16]
            .ToLowerInvariant();
    }
}
