using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

[Collection(BoundedCallCollection.Name)]
public sealed class FollowFingerprintCaptureTests
{
    [Fact]
    public void TryCaptureFriendFingerprintSamplesReturnsCapturedBytes()
    {
        var samples = VmOperations.TryCaptureFriendFingerprintSamples(
            () => [1, 2, 3],
            timeoutMs: 1000);

        Assert.Equal([1, 2, 3], samples);
    }

    [Fact]
    public void TryCaptureFriendFingerprintSamplesTimesOut()
    {
        var samples = VmOperations.TryCaptureFriendFingerprintSamples(
            () =>
            {
                Thread.Sleep(250);
                return [1, 2, 3];
            },
            timeoutMs: 20);

        Assert.Null(samples);
    }
}
