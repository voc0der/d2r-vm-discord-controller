using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FriendsAccordionDecisionTests
{
    [Theory]
    [InlineData(true, true, 2)]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 2)]
    public void FriendsAccordionActionOnlyExpandsWhenRowEvidenceIsMissing(
        bool openedDrawer,
        bool expandedEvidence,
        int expected)
    {
        var action = VmOperations.ChooseFriendsAccordionAction(openedDrawer, expandedEvidence);

        Assert.Equal((VmOperations.FriendsAccordionAction)expected, action);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void ExpansionVerificationRunsBeforeEveryRowScan(
        int action,
        bool expected)
    {
        var verify = VmOperations.ShouldVerifyFriendsExpansionAfterAction(
            (VmOperations.FriendsAccordionAction)action);

        Assert.Equal(expected, verify);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void FriendsListExpansionRequiresMultipleVisibleRows(int visibleRows, bool expected)
    {
        Assert.Equal(expected, VmOperations.IsFriendsListExpandedByVisibleRows(visibleRows));
    }
}
