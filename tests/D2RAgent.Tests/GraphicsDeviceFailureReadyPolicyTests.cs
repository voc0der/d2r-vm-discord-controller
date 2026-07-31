using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class GraphicsDeviceFailureReadyPolicyTests
{
    [Fact]
    public void MissingDialogLeavesTheNormalReadyInputPathActive()
    {
        var action = VmOperations.ClassifyGraphicsDeviceFailureReadyAction(
            new D2RGraphicsDeviceFailureDismissalResult(Detected: false, DismissalSent: false));

        Assert.Equal(VmOperations.GraphicsDeviceFailureReadyAction.None, action);
    }

    [Fact]
    public void DetectedDialogSuppressesGenericInputWhenDismissalWasNotDelivered()
    {
        var action = VmOperations.ClassifyGraphicsDeviceFailureReadyAction(
            new D2RGraphicsDeviceFailureDismissalResult(Detected: true, DismissalSent: false));

        Assert.Equal(VmOperations.GraphicsDeviceFailureReadyAction.SuppressInput, action);
    }

    [Fact]
    public void VerifiedDismissalAuthorizesTheD2ROnlyRelaunchPath()
    {
        var action = VmOperations.ClassifyGraphicsDeviceFailureReadyAction(
            new D2RGraphicsDeviceFailureDismissalResult(Detected: true, DismissalSent: true));

        Assert.Equal(VmOperations.GraphicsDeviceFailureReadyAction.Relaunch, action);
    }
}
