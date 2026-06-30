using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FriendsAccordionDecisionTests
{
    [Theory]
    [InlineData(true, true, false, 3)]
    [InlineData(true, false, true, 0)]
    [InlineData(true, false, false, 1)]
    [InlineData(false, false, false, 2)]
    [InlineData(false, false, true, 0)]
    [InlineData(false, true, false, 3)]
    public void FriendsAccordionActionVerifiesWeakEvidenceBeforeToggling(
        bool openedDrawer,
        bool expandedEvidence,
        bool avoidToggleEvidence,
        int expected)
    {
        var action = VmOperations.ChooseFriendsAccordionAction(openedDrawer, expandedEvidence, avoidToggleEvidence);

        Assert.Equal((VmOperations.FriendsAccordionAction)expected, action);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void ExpansionVerificationRunsBeforeEveryRowScan(
        int action,
        bool expected)
    {
        var verify = VmOperations.ShouldVerifyFriendsExpansionAfterAction(
            (VmOperations.FriendsAccordionAction)action);

        Assert.Equal(expected, verify);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, true)]
    [InlineData(0, 2, false)]
    [InlineData(0, 3, true)]
    [InlineData(2, 2, true)]
    public void FriendsListExpansionRejectsSingleRowFalsePositive(
        int visibleRows,
        int markerRows,
        bool expected)
    {
        Assert.Equal(expected, VmOperations.IsFriendsListExpandedByEvidence(visibleRows, markerRows));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(0, 2, true)]
    [InlineData(1, 0, true)]
    public void FriendsListRowEvidenceUsesWeakProofOnlyToAvoidToggleAfterOpen(
        int visibleRows,
        int markerRows,
        bool expected)
    {
        Assert.Equal(expected, VmOperations.HasFriendsListRowEvidence(visibleRows, markerRows));
    }
}
