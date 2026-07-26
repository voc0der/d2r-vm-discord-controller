using AgentCommon;
using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FollowFingerprintSelectionTests
{
    [Fact]
    public void SelectFollowFingerprintMatchIgnoresUnusableDarkRows()
    {
        var selection = VmOperations.SelectFollowFingerprintMatch(
        [
            Match(row: 10, average: 0, signalAverage: 0, signalPixels: 0),
            Match(row: 2, average: 10, signalAverage: 20, signalPixels: 5),
            Match(row: 3, average: 16, signalAverage: 35, signalPixels: 5)
        ]);

        Assert.Equal(VmOperations.FollowFingerprintSelectionStatus.Selected, selection.Status);
        Assert.Equal(2, selection.Match?.Row);
    }

    [Fact]
    public void SelectFollowFingerprintMatchRejectsAmbiguousUsableRows()
    {
        var selection = VmOperations.SelectFollowFingerprintMatch(
        [
            Match(row: 2, average: 10, signalAverage: 20, signalPixels: 5),
            Match(row: 3, average: 11, signalAverage: 20.5, signalPixels: 5)
        ]);

        Assert.Equal(VmOperations.FollowFingerprintSelectionStatus.Ambiguous, selection.Status);
        Assert.Equal(2, selection.Match?.Row);
    }

    [Fact]
    public void SelectFollowFingerprintMatchRejectsWhenNoRowsAreUsable()
    {
        var selection = VmOperations.SelectFollowFingerprintMatch(
        [
            Match(row: 1, average: 0, signalAverage: 0, signalPixels: 0),
            Match(row: 2, average: 40, signalAverage: 95, signalPixels: 5)
        ]);

        Assert.Equal(VmOperations.FollowFingerprintSelectionStatus.NoUsableMatch, selection.Status);
        Assert.Null(selection.Match);
    }

    [Fact]
    public void SelectFollowFingerprintMatchAcceptsLiveRowOneScores()
    {
        var selection = VmOperations.SelectFollowFingerprintMatch(
        [
            Match(row: 1, average: 4.6, signalAverage: 55.2, signalPixels: 4),
            Match(row: 2, average: 25.7, signalAverage: 133.6, signalPixels: 9),
            Match(row: 3, average: 9.4, signalAverage: 101.4, signalPixels: 4),
            Match(row: 4, average: 24.2, signalAverage: 182.1, signalPixels: 6),
            Match(row: 5, average: 17.2, signalAverage: 153.6, signalPixels: 5),
            Match(row: 6, average: 7.2, signalAverage: 73.2, signalPixels: 4),
            Match(row: 7, average: 6.5, signalAverage: 123.5, signalPixels: 2),
            Match(row: 8, average: 11.0, signalAverage: 152.1, signalPixels: 3)
        ]);

        Assert.Equal(VmOperations.FollowFingerprintSelectionStatus.Selected, selection.Status);
        Assert.Equal(1, selection.Match?.Row);
    }

    [Fact]
    public void SelectFollowFingerprintMatchRejectsCloseLiveRowTwoScores()
    {
        var selection = VmOperations.SelectFollowFingerprintMatch(
        [
            Match(row: 1, average: 5.3, signalAverage: 126.2, signalPixels: 2),
            Match(row: 2, average: 12.5, signalAverage: 71.5, signalPixels: 8),
            Match(row: 3, average: 25.5, signalAverage: 147.4, signalPixels: 8),
            Match(row: 4, average: 24.2, signalAverage: 182.1, signalPixels: 6),
            Match(row: 5, average: 17.2, signalAverage: 153.6, signalPixels: 5),
            Match(row: 6, average: 7.2, signalAverage: 73.2, signalPixels: 4),
            Match(row: 7, average: 6.5, signalAverage: 123.5, signalPixels: 2),
            Match(row: 8, average: 6.5, signalAverage: 124.5, signalPixels: 2)
        ]);

        Assert.Equal(VmOperations.FollowFingerprintSelectionStatus.Ambiguous, selection.Status);
        Assert.Equal(2, selection.Match?.Row);
    }

    [Fact]
    public void FollowFingerprintMaxScanRowsStaysInsideVisibleFriendRows()
    {
        var ui = new D2RUiAutomationConfig
        {
            FriendRowFingerprintMaxScanRows = 10
        };

        Assert.Equal(8, VmOperations.GetFollowFingerprintMaxScanRows(ui));
    }

    [Fact]
    public void OldLowDetailFingerprintsCannotDriveAutoClicks()
    {
        var template = new FriendFingerprint(
            GridColumns: 12,
            GridRows: 4,
            Samples: new byte[12 * 4 * 3]);

        Assert.False(VmOperations.CanAutoClickFollowFingerprint(template));
    }

    [Fact]
    public void WiderDefaultFingerprintsCanDriveAutoClicks()
    {
        var defaults = new D2RUiAutomationConfig();
        var template = new FriendFingerprint(
            defaults.FriendRowFingerprintGridColumns,
            defaults.FriendRowFingerprintGridRows,
            new byte[defaults.FriendRowFingerprintGridColumns * defaults.FriendRowFingerprintGridRows * 3]);

        Assert.True(VmOperations.CanAutoClickFollowFingerprint(template));
    }

    // Reproduces the live failure that motivated raising the grid: with the bound name and a
    // similar one on adjacent rows, both cleared the usability gate 2.1 apart against a required
    // separation of 12, so follow-auto refused to click on four VMs at once. Everything else was
    // disqualified by signal average, which is what makes this ambiguity rather than no match.
    [Fact]
    public void TwoAdjacentSimilarNamesReproduceTheObservedAmbiguousStall()
    {
        var selection = VmOperations.SelectFollowFingerprintMatch(
        [
            Match(row: 1, average: 15.5, signalAverage: 112.5, signalPixels: 12),
            Match(row: 2, average: 17.9, signalAverage: 126.6, signalPixels: 13),
            Match(row: 3, average: 10.8, signalAverage: 110.8, signalPixels: 9),
            Match(row: 4, average: 9.2, signalAverage: 72.4, signalPixels: 11),
            Match(row: 5, average: 8.8, signalAverage: 70.3, signalPixels: 10),
            Match(row: 6, average: 12.2, signalAverage: 141.9, signalPixels: 7),
            Match(row: 7, average: 15.2, signalAverage: 159.6, signalPixels: 8),
            Match(row: 8, average: 13.3, signalAverage: 137.2, signalPixels: 8)
        ]);

        Assert.Equal(VmOperations.FollowFingerprintSelectionStatus.Ambiguous, selection.Status);
        Assert.Equal(5, selection.Match?.Row);
    }

    // The same fleet's fifth VM reported the other message from the same data shape - both
    // candidates over the signal gate - which is what confirms the thresholds are being read
    // correctly rather than the two outcomes being interchangeable.
    [Fact]
    public void BothCandidatesOverTheSignalGateReportNoUsableMatchInstead()
    {
        var selection = VmOperations.SelectFollowFingerprintMatch(
        [
            Match(row: 4, average: 10.5, signalAverage: 126.1, signalPixels: 7),
            Match(row: 5, average: 13.4, signalAverage: 96.2, signalPixels: 12)
        ]);

        Assert.Equal(VmOperations.FollowFingerprintSelectionStatus.NoUsableMatch, selection.Status);
    }

    // A template captured on the old grid can never match anything captured on the new one, so the
    // grid change forces a re-bind. The agent detects this explicitly and says so instead of
    // reporting a generic miss - this documents why that guard has to exist.
    [Fact]
    public void TemplateFromTheLegacyGridCannotCompareAgainstTheCurrentOne()
    {
        var legacy = new FriendFingerprint(24, 4, new byte[24 * 4 * 3]);
        var current = new FriendFingerprint(32, 4, new byte[32 * 4 * 3]);

        Assert.False(FriendFingerprint.Compare(legacy, current).Comparable);
    }

    private static VmOperations.FriendRowFingerprintMatch Match(
        int row,
        double average,
        double signalAverage,
        int signalPixels)
    {
        return new VmOperations.FriendRowFingerprintMatch(
            row,
            new FriendFingerprintComparison(
                Comparable: true,
                AverageDifference: average,
                SignalAverageDifference: signalAverage,
                SignalPixels: signalPixels,
                TotalPixels: 48));
    }
}
