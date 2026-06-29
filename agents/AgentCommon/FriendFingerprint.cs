namespace AgentCommon;

public sealed record FriendFingerprintComparison(
    bool Comparable,
    double AverageDifference,
    double SignalAverageDifference,
    int SignalPixels,
    int TotalPixels)
{
    public static FriendFingerprintComparison NotComparable { get; } = new(
        Comparable: false,
        AverageDifference: double.PositiveInfinity,
        SignalAverageDifference: double.PositiveInfinity,
        SignalPixels: 0,
        TotalPixels: 0);
}

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
        var comparison = Compare(a, b);
        return comparison.Comparable
            && comparison.AverageDifference <= toleranceFraction * 255;
    }

    public static FriendFingerprintComparison Compare(FriendFingerprint? a, FriendFingerprint? b)
    {
        if (a is null || b is null)
        {
            return FriendFingerprintComparison.NotComparable;
        }

        if (a.GridColumns != b.GridColumns
            || a.GridRows != b.GridRows
            || a.Samples.Length != b.Samples.Length
            || a.Samples.Length == 0
            || a.Samples.Length % 3 != 0
            || a.Samples.Length != a.GridColumns * a.GridRows * 3
            || b.Samples.Length != b.GridColumns * b.GridRows * 3)
        {
            return FriendFingerprintComparison.NotComparable;
        }

        long totalDifference = 0;
        long signalDifference = 0;
        var signalPixels = 0;
        for (var i = 0; i < a.Samples.Length; i++)
        {
            totalDifference += Math.Abs(a.Samples[i] - b.Samples[i]);
        }

        for (var i = 0; i < a.Samples.Length; i += 3)
        {
            var aLuminance = GetLuminance(a.Samples[i], a.Samples[i + 1], a.Samples[i + 2]);
            var bLuminance = GetLuminance(b.Samples[i], b.Samples[i + 1], b.Samples[i + 2]);
            if (aLuminance < 45 && bLuminance < 45)
            {
                continue;
            }

            signalPixels++;
            signalDifference += Math.Abs(a.Samples[i] - b.Samples[i]);
            signalDifference += Math.Abs(a.Samples[i + 1] - b.Samples[i + 1]);
            signalDifference += Math.Abs(a.Samples[i + 2] - b.Samples[i + 2]);
        }

        var averageDifference = totalDifference / (double)a.Samples.Length;
        var signalAverageDifference = signalPixels > 0
            ? signalDifference / (signalPixels * 3.0)
            : averageDifference;

        return new FriendFingerprintComparison(
            Comparable: true,
            AverageDifference: averageDifference,
            SignalAverageDifference: signalAverageDifference,
            SignalPixels: signalPixels,
            TotalPixels: a.Samples.Length / 3);
    }

    private static double GetLuminance(byte red, byte green, byte blue)
    {
        return (red * 0.2126) + (green * 0.7152) + (blue * 0.0722);
    }
}
