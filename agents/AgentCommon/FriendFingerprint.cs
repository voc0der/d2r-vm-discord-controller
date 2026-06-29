namespace AgentCommon;

// A small grid of RGB samples taken from a friends-drawer row's name-text area, used to recognize
// a specific bound friend across all VMs without ever needing to OCR or store a real image file -
// same grid-sample-and-compare shape as every other on-screen classifier in this codebase, just
// generalized to "does this look like the same thing we captured before" instead of a fixed
// lum/grey/dark/orange threshold.
public sealed record FriendFingerprint(int GridColumns, int GridRows, byte[] Samples)
{
    public static FriendFingerprint? FromBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        var parts = base64.Trim().Split(':', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var columns) || !int.TryParse(parts[1], out var rows))
        {
            return null;
        }

        try
        {
            var samples = Convert.FromBase64String(parts[2]);
            return new FriendFingerprint(columns, rows, samples);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public string ToBase64()
    {
        return $"{GridColumns}:{GridRows}:{Convert.ToBase64String(Samples)}";
    }

    // 0.12 was picked to absorb ordinary rendering noise (anti-aliasing, the row's normal/hover
    // background tint) while still telling visually distinct names apart - same kind of threshold
    // tuning as the rest of D2RScreenClassifier, not derived from a live capture yet. Revisit
    // against real reference captures the same way pixel-classifier-catalog.md documents the
    // others if this turns out too loose/tight in practice.
    public const double DefaultToleranceFraction = 0.12;

    public static bool IsMatch(FriendFingerprint? a, FriendFingerprint? b, double toleranceFraction = DefaultToleranceFraction)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (a.GridColumns != b.GridColumns || a.GridRows != b.GridRows || a.Samples.Length != b.Samples.Length || a.Samples.Length == 0)
        {
            return false;
        }

        long totalDifference = 0;
        for (var i = 0; i < a.Samples.Length; i++)
        {
            totalDifference += Math.Abs(a.Samples[i] - b.Samples[i]);
        }

        var averageDifference = totalDifference / (double)a.Samples.Length;
        return averageDifference <= toleranceFraction * 255;
    }
}
