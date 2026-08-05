using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class StatefulKeyDeliveryTests
{
    [Fact]
    public void SuccessfulWindowDeliveryDoesNotAlsoSendTheVisibleKey()
    {
        var windowDeliveries = 0;
        var visibleDeliveries = 0;

        VmOperations.SendSingleStatefulKey(
            () =>
            {
                windowDeliveries++;
                return true;
            },
            () => visibleDeliveries++);

        Assert.Equal(1, windowDeliveries);
        Assert.Equal(0, visibleDeliveries);
    }

    [Fact]
    public void MissingWindowTargetFallsBackToOneVisibleKey()
    {
        var windowDeliveries = 0;
        var visibleDeliveries = 0;

        VmOperations.SendSingleStatefulKey(
            () =>
            {
                windowDeliveries++;
                return false;
            },
            () => visibleDeliveries++);

        Assert.Equal(1, windowDeliveries);
        Assert.Equal(1, visibleDeliveries);
    }
}
