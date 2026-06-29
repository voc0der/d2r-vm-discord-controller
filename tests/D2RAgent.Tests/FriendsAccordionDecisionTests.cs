using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FriendsAccordionDecisionTests
{
    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 2)]
    public void FreshlyOpenedDrawerAlwaysExpandsFriendsAccordion(
        bool openedDrawer,
        bool expandedEvidence,
        int expected)
    {
        var action = VmOperations.ChooseFriendsAccordionAction(openedDrawer, expandedEvidence);

        Assert.Equal((VmOperations.FriendsAccordionAction)expected, action);
    }
}
