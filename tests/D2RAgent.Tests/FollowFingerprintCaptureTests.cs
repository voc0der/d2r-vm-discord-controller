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

    // Gated rather than slept so the abandoned capture's bounded-call slot is returned before the
    // test finishes; see BoundedCallSlots.
    [Fact]
    public void TryCaptureFriendFingerprintSamplesTimesOut()
    {
        using var release = new ManualResetEventSlim(false);

        var samples = VmOperations.TryCaptureFriendFingerprintSamples(
            () =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                return [1, 2, 3];
            },
            timeoutMs: 20);

        Assert.Null(samples);

        release.Set();
        BoundedCallSlots.WaitForAll();
    }
}
