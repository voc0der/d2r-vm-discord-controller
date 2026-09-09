namespace D2RHost;

public enum VmBootLogoVerdict
{
    /// <summary>The frame is not the Windows boot logo; this watchdog has nothing to say.</summary>
    NotBootLogo,

    /// <summary>The logo is there, but not for long enough yet. Every healthy boot passes through here.</summary>
    KeepWatching,

    /// <summary>The logo has held past the window a real boot clears. Cut power and start it again.</summary>
    HardPowerCut,

    /// <summary>Out of attempts; a person needs to look at this guest.</summary>
    GiveUp
}

public sealed record VmBootLogoAssessment(VmBootLogoVerdict Verdict, string Reason);

/// <summary>
/// Decides when a guest sitting on the Windows boot logo has been there long enough to be wedged.
///
/// This exists because every other signal lies about this failure. Captured from a guest that had
/// been frozen on the logo for over an hour, Hyper-V reported <c>Heartbeat: OK</c>,
/// <c>KvpOSName: Windows 10 Pro</c>, <c>Status: Operating normally</c>, and 21% CPU - readings
/// identical, field for field, to a sibling VM that was healthy and in-game at that moment. The
/// kernel and the integration services come up early enough to answer the hypervisor, so a boot
/// that hangs after them is invisible to every health signal the host can otherwise reach.
/// <see cref="VmHangRecoveryPolicy"/> refuses to cut a guest whose heartbeat answers, which is
/// right for the failure it was written for and leaves this one with no recovery at all.
///
/// So the screen is not a redundant signal here. It is the only one that distinguishes the two.
///
/// The bound that keeps this safe is time, not appearance. Every healthy VM shows this exact logo
/// on every boot, so a sighting means nothing on its own and a short trigger would power-cut the
/// whole fleet into a reboot loop. What separates wedged from normal is only that a real boot
/// leaves the logo behind and a wedged one does not, so the logo must be seen continuously across
/// a window comfortably longer than a cold boot before anything happens.
/// </summary>
public sealed class VmBootLogoPolicy
{
    // Measured from the real captures (docs/runbooks/assets/vm-console). The wedged frame scored
    // 0.984 near-black with 0.0125 cyan in a compact centred block filling 0.81 of its own
    // bounding box; the healthy in-game frame scored 0.717 near-black and 0.0 cyan. The gaps are
    // wide, so these sit well clear of both rather than hugging either.
    private const double MinNearBlackRatio = 0.90;
    private const double MinLogoCyanRatio = 0.002;
    private const double MaxLogoCyanRatio = 0.06;
    private const double MinCyanFillOfBox = 0.45;
    private const double MaxCyanBoxSideRatio = 0.35;
    private const double MinCyanCentroidX = 0.30;
    private const double MaxCyanCentroidX = 0.70;
    private const double MinCyanCentroidY = 0.20;
    private const double MaxCyanCentroidY = 0.60;

    private readonly VmBootLogoOptions _options;

    public VmBootLogoPolicy(VmBootLogoOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Whether one frame is the Windows boot logo: a near-black screen carrying a single compact,
    /// solidly-filled, roughly centred cyan block and nothing else.
    /// </summary>
    /// <remarks>
    /// Every clause is doing work against a specific false positive. Near-black rejects a game or
    /// desktop that merely contains something cyan. The cyan ratio band rejects both a stray pixel
    /// and a screen that is mostly cyan. The fill and box-size clauses reject cyan scattered across
    /// the frame - the logo is one solid block, not confetti. The centroid clauses reject a block
    /// parked in a corner, which the logo never is.
    /// </remarks>
    public static bool IsWindowsBootLogo(VmConsoleFrameStats stats)
    {
        return stats.NearBlackRatio >= MinNearBlackRatio
            && stats.LogoCyanRatio >= MinLogoCyanRatio
            && stats.LogoCyanRatio <= MaxLogoCyanRatio
            && stats.CyanFillOfBox >= MinCyanFillOfBox
            && stats.CyanBoxWidthRatio <= MaxCyanBoxSideRatio
            && stats.CyanBoxHeightRatio <= MaxCyanBoxSideRatio
            && stats.CyanCentroidX >= MinCyanCentroidX
            && stats.CyanCentroidX <= MaxCyanCentroidX
            && stats.CyanCentroidY >= MinCyanCentroidY
            && stats.CyanCentroidY <= MaxCyanCentroidY;
    }

    public VmBootLogoAssessment Assess(
        VmConsoleFrameStats? stats,
        TimeSpan logoHeldFor,
        int hardPowerCutsUsed)
    {
        if (!_options.Enabled)
        {
            return new VmBootLogoAssessment(
                VmBootLogoVerdict.NotBootLogo,
                "the boot-logo watchdog is disabled in host config (bootLogoWatchdog.enabled)");
        }

        if (stats is null || !IsWindowsBootLogo(stats))
        {
            return new VmBootLogoAssessment(
                VmBootLogoVerdict.NotBootLogo,
                "the guest's console is not showing the Windows boot logo");
        }

        if (logoHeldFor < _options.StuckAfter)
        {
            return new VmBootLogoAssessment(
                VmBootLogoVerdict.KeepWatching,
                $"the Windows boot logo has been up {Describe(logoHeldFor)}, and a boot is given "
                    + $"{Describe(_options.StuckAfter)} to get past it before that counts as wedged");
        }

        if (hardPowerCutsUsed >= _options.MaxHardPowerCuts)
        {
            return new VmBootLogoAssessment(
                VmBootLogoVerdict.GiveUp,
                $"the guest has been cut {_options.MaxHardPowerCuts} time(s) and is still coming back to the boot "
                    + "logo; something below the guest needs looking at and more cuts will not find it");
        }

        return new VmBootLogoAssessment(
            VmBootLogoVerdict.HardPowerCut,
            $"the guest's console has shown the Windows boot logo continuously for {Describe(logoHeldFor)}. "
                + "Hyper-V's heartbeat cannot see this - a boot that hangs after the integration services start "
                + "reports OK - so the screen is the only evidence, and a guest that never leaves the logo only "
                + "comes back on a power cut");
    }

    private static string Describe(TimeSpan span)
    {
        return span.TotalMinutes >= 1
            ? $"{span.TotalMinutes:N0}m"
            : $"{span.TotalSeconds:N0}s";
    }
}

public sealed record VmBootLogoOptions(
    bool Enabled,
    TimeSpan StuckAfter,
    int MaxHardPowerCuts);
