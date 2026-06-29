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
            Match(row: 3, average: 11, signalAverage: 25, signalPixels: 5)
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
