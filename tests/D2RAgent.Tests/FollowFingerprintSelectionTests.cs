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
