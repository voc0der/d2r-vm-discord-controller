namespace D2RAgent;

/// <summary>
/// Decides whether right-clicking a friend row actually opened the context menu, by comparing the
/// pixels under the prospective "Join Game" point before and after the click.
/// </summary>
/// <remarks>
/// <para>
/// The Join Game point is derived arithmetically - the row's own position plus the fixed offset
/// measured for row 1 - so it is a prediction, not an observation. When the menu does not open,
/// that prediction points at whatever the Friends pane draws underneath. At the shipped defaults
/// (friendRowStart 0.180/0.180, friendRowHeight 0.049, friendContextJoinGame 0.278/0.344) row 7
/// resolves to (0.278, 0.638) = (380, 490) at 1366x768, which is inside the Add Friend button.
/// The existing guard only rejected points outside the pane horizontally, so this passed.
/// </para>
/// <para>
/// A differential test is used rather than a fingerprint of the menu itself, because the menu's
/// appearance is exactly the kind of art that an expansion re-skins, while "an overlay appeared
/// where there was none" stays true regardless. It also handles the case where the menu opens
/// upward near the bottom of the pane: the predicted point does not change, the probe refuses,
/// and the client reports it instead of clicking something arbitrary.
/// </para>
/// <para>
/// The region sampled is deliberately static UI. Nothing in the Friends pane animates, so any
/// meaningful change is the menu. The thresholds are set well above sampling noise but far below
/// the difference between a menu row and flat panel art.
/// </para>
/// </remarks>
internal static class FriendContextMenuProbe
{
    /// <summary>Width of the sampled band, as a ratio of the window - a menu row, not a pixel.</summary>
    internal const double RegionWidthRatio = 0.070;

    /// <summary>Height of the sampled band, as a ratio of the window.</summary>
    internal const double RegionHeightRatio = 0.022;

    internal const int RegionSampleGrid = 7;

    /// <summary>Mean-luminance change that counts as "something was drawn here".</summary>
    internal const double MinimumLuminanceDelta = 6.0;

    /// <summary>
    /// Contrast change that counts on its own, for a menu whose average luminance happens to
    /// match the art it covers but whose text does not.
    /// </summary>
    internal const double MinimumStdDevDelta = 5.0;

    /// <summary>
    /// True only when the sampled region demonstrably changed. An unreadable sample on either
    /// side returns false: without evidence the menu opened, the caller must not click.
    /// </summary>
    public static bool MenuAppeared(ScreenRegionStats? before, ScreenRegionStats? after)
    {
        if (before is null || after is null || before.Samples <= 0 || after.Samples <= 0)
        {
            return false;
        }

        return Math.Abs(after.AverageLuminance - before.AverageLuminance) >= MinimumLuminanceDelta
            || Math.Abs(after.LuminanceStdDev - before.LuminanceStdDev) >= MinimumStdDevDelta;
    }
}
