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
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void AccordionToggleAvoidanceDependsOnReliabilityNotWeakRowEvidence(
        bool expandedEvidence,
        bool reliableEvidence,
        bool expected)
    {
        var avoid = VmOperations.ShouldAvoidFriendsAccordionToggle(expandedEvidence, reliableEvidence);

        Assert.Equal(expected, avoid);
    }

    [Theory]
    [InlineData(0, false, true, true, false)]
    [InlineData(0, false, false, false, false)]
    [InlineData(1, false, false, true, false)]
    [InlineData(2, false, false, true, false)]
    [InlineData(3, false, false, true, true)]
    [InlineData(3, false, true, true, false)]
    [InlineData(3, false, false, false, false)]
    [InlineData(3, true, false, true, false)]
    public void AccordionRecoveryOnlyRetogglesWhenStrongPriorExpansionDisappears(
        int action,
        bool expandedEvidence,
        bool rowEvidence,
        bool reliableEvidence,
        bool expected)
    {
        var recover = VmOperations.ShouldRecoverFriendsAccordionAfterVerification(
            (VmOperations.FriendsAccordionAction)action,
            expandedEvidence,
            rowEvidence,
            reliableEvidence);

        Assert.Equal(expected, recover);
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
    public void FriendsListRowEvidenceTracksWeakProofForRecoveryDecisions(
        int visibleRows,
        int markerRows,
        bool expected)
    {
        Assert.Equal(expected, VmOperations.HasFriendsListRowEvidence(visibleRows, markerRows));
    }
}
