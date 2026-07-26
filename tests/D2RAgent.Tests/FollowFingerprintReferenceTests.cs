using AgentCommon;
using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// End-to-end check of the follow-bind fingerprint geometry against a real 1366x768 lobby capture,
// using the same sampling math the agent uses on a live window. This is what turned "the names
// don't even look similar, this must be a bug" into a measurement: with the band that shipped in
// v0.2.210, binding any one of these eight friends produced a fingerprint that seven of the other
// rows also satisfied, because the band straddled the name text AND the status line beneath it.
public sealed class FollowFingerprintReferenceTests
{
    private const string Capture = "lobby_friends_list.png";

    // The same lobby a few minutes later, after ArnoSigma went offline. Offline friends sort below
    // online ones, so ArnoSigma dropped from row 1 to row 5 and pushed the bound friend (vocoder)
    // from row 4 up to row 3 - the ordinary case this whole mechanism exists to survive.
    private static readonly string[] ResortedCaptures =
    [
        "lobby_friends_list_resorted.png",
        "lobby_friends_list_resorted_2.png"
    ];

    private const int VisibleRows = 8;
    private const int BoundRowInCapture = 4;
    private const int BoundRowAfterResort = 3;

    private static FriendFingerprint Row(D2RUiAutomationConfig ui, int row, string capture = Capture)
    {
        var region = D2RUiCoordinateCatalog.GetFriendRowFingerprintRegion(ui, row);
        return FullCaptureRegionSampler.SampleFriendRowFingerprint(
            capture, region.Center, region.WidthRatio, region.HeightRatio, region.GridColumns, region.GridRows);
    }

    private static FriendRowFingerprintMatchSet ScanWithAlignmentSearch(
        D2RUiAutomationConfig ui,
        FriendFingerprint template,
        string capture)
    {
        var matches = new List<VmOperations.FriendRowFingerprintMatch>();
        for (var row = 1; row <= VisibleRows; row++)
        {
            var region = D2RUiCoordinateCatalog.GetFriendRowFingerprintRegion(ui, row);
            var (searchHeight, searchRows) = VmOperations.GetFollowFingerprintSearchBand(region);
            var extended = FullCaptureRegionSampler.SampleFriendRowFingerprint(
                capture, region.Center, region.WidthRatio, searchHeight, region.GridColumns, searchRows);
            matches.Add(new VmOperations.FriendRowFingerprintMatch(
                row,
                VmOperations.CompareFollowFingerprintAcrossAlignments(
                    template, extended.Samples, region.GridColumns, region.GridRows)));
        }

        return new FriendRowFingerprintMatchSet(matches);
    }

    private sealed record FriendRowFingerprintMatchSet(List<VmOperations.FriendRowFingerprintMatch> Matches);

    // The headline guarantee: bind any visible friend and no other visible friend can satisfy that
    // fingerprint. A row inside the gate is one that competes at follow time, which is what
    // produces "ambiguous; not clicking a friend row this cycle".
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void EveryVisibleFriendBindsToExactlyOneRow(int boundRow)
    {
        var ui = new D2RUiAutomationConfig();
        var bound = Row(ui, boundRow);

        var matches = new List<VmOperations.FriendRowFingerprintMatch>();
        for (var row = 1; row <= VisibleRows; row++)
        {
            matches.Add(new VmOperations.FriendRowFingerprintMatch(
                row, FriendFingerprint.Compare(bound, Row(ui, row))));
        }

        var selection = VmOperations.SelectFollowFingerprintMatch(matches);

        Assert.Equal(VmOperations.FollowFingerprintSelectionStatus.Selected, selection.Status);
        Assert.Equal(boundRow, selection.Match?.Row);
    }

    // The real end-to-end case, across two genuine captures: bind a friend at the row they occupy,
    // then find them after the list re-sorts. This is what was failing in production with
    // "not confidently found" on every VM - the bound friend had moved from row 4 to row 3, and
    // D2R's real row pitch differs enough from the configured ratio that the band lands on a name
    // about a pixel differently depending on which row it sits in. One pixel was the whole gap:
    // the correct row scored 95.4 against a 90 gate, and 0.0 one pixel up.
    [Theory]
    [InlineData("lobby_friends_list_resorted.png")]
    [InlineData("lobby_friends_list_resorted_2.png")]
    public void BoundFriendIsStillFoundAfterTheListResorts(string capture)
    {
        var ui = new D2RUiAutomationConfig();
        var bound = Row(ui, BoundRowInCapture);

        var selection = VmOperations.SelectFollowFingerprintMatch(
            ScanWithAlignmentSearch(ui, bound, capture).Matches);

        Assert.Equal(VmOperations.FollowFingerprintSelectionStatus.Selected, selection.Status);
        Assert.Equal(BoundRowAfterResort, selection.Match?.Row);
    }

    // Without the alignment search the same scan fails, which is what pins why the search exists.
    // If someone later decides it is unnecessary complexity, this turns red.
    [Fact]
    public void WithoutTheAlignmentSearchTheResortedListFindsNothing()
    {
        var ui = new D2RUiAutomationConfig();
        var bound = Row(ui, BoundRowInCapture);

        var matches = new List<VmOperations.FriendRowFingerprintMatch>();
        for (var row = 1; row <= VisibleRows; row++)
        {
            matches.Add(new VmOperations.FriendRowFingerprintMatch(
                row,
                FriendFingerprint.Compare(bound, Row(ui, row, ResortedCaptures[0]))));
        }

        var selection = VmOperations.SelectFollowFingerprintMatch(matches);

        Assert.Equal(VmOperations.FollowFingerprintSelectionStatus.NoUsableMatch, selection.Status);
    }

    // The search must stay narrow enough that it only absorbs banding error. Widen it and unrelated
    // rows start sliding into a false match, which would be worse than the miss it fixes.
    [Theory]
    [InlineData("lobby_friends_list_resorted.png")]
    [InlineData("lobby_friends_list_resorted_2.png")]
    public void AlignmentSearchDoesNotPullUnrelatedRowsIntoTheGate(string capture)
    {
        var ui = new D2RUiAutomationConfig();
        var bound = Row(ui, BoundRowInCapture);

        var inGate = ScanWithAlignmentSearch(ui, bound, capture).Matches
            .Where(match => VmOperations.IsUsableFollowFingerprintMatchForTests(match.Comparison))
            .Select(match => match.Row)
            .ToArray();

        Assert.Equal([BoundRowAfterResort], inGate);
    }

    // The separation has to survive small vertical drift, not just hold at one exact alignment.
    // A grid that only separates when perfectly aligned fails intermittently on live VMs, which
    // is precisely how this reached production: it worked on four VMs and stalled on the fifth.
    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SeparationSurvivesSmallVerticalDrift(int drewPixels)
    {
        var defaults = new D2RUiAutomationConfig();
        var drifted = new D2RUiAutomationConfig
        {
            FriendRowFingerprintOffsetY = defaults.FriendRowFingerprintOffsetY + (drewPixels / 768.0)
        };
        var bound = Row(defaults, 4);

        for (var row = 1; row <= VisibleRows; row++)
        {
            if (row == 4)
            {
                continue;
            }

            var comparison = FriendFingerprint.Compare(bound, Row(drifted, row));
            Assert.False(
                VmOperations.IsUsableFollowFingerprintMatchForTests(comparison),
                $"Row {row} satisfied the bound fingerprint at {drewPixels:+0;-0;0}px drift "
                    + $"(avg {comparison.AverageDifference:F1}, sig {comparison.SignalAverageDifference:F1}).");
        }
    }

    // The band must sit on the name and clear the status line under it. The status line changes
    // on its own ("In Menus" -> "Act I Hell") without the friend moving, and most rows show the
    // same text, so sampling it both destabilizes a bound friend's own score and pulls unrelated
    // rows toward each other.
    [Fact]
    public void SampledBandCoversTheNameTextAndNotTheStatusLine()
    {
        var ui = new D2RUiAutomationConfig();
        var region = D2RUiCoordinateCatalog.GetFriendRowFingerprintRegion(ui, 4);
        var top = (region.Center.Y - (region.HeightRatio / 2)) * 768;
        var bottom = (region.Center.Y + (region.HeightRatio / 2)) * 768;

        // Measured from lobby_friends_list.png: row 4's name occupies y 236-242 and its status
        // line starts at y 247.
        Assert.True(top <= 236, $"Band top {top:F1} must reach the top of the name text at y=236.");
        Assert.True(bottom >= 242, $"Band bottom {bottom:F1} must cover the name text through y=242.");
        Assert.True(bottom < 247, $"Band bottom {bottom:F1} must stay above the status line at y=247.");
    }

    // Guards the sampling budget: the grid must stay inside the range CaptureFingerprintGrid
    // clamps to, or the geometry the tests verify is not the geometry the agent captures.
    [Fact]
    public void ConfiguredGridStaysWithinTheCaptureClamp()
    {
        var ui = new D2RUiAutomationConfig();

        Assert.InRange(ui.FriendRowFingerprintGridColumns, 1, 64);
        Assert.InRange(ui.FriendRowFingerprintGridRows, 1, 64);
        Assert.True(VmOperations.CanAutoClickFollowFingerprint(
            new FriendFingerprint(
                ui.FriendRowFingerprintGridColumns,
                ui.FriendRowFingerprintGridRows,
                new byte[ui.FriendRowFingerprintGridColumns * ui.FriendRowFingerprintGridRows * 3])));
    }
}
