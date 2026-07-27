using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// Two paths offer self_update - the authentication hook and the five-minute fleet sweep - and they
// used to keep separate "already offered" sets. After a master restart they line up on the same
// satellite: it reconnects and is offered an update within seconds, then the sweep's first pass
// runs while it is still restarting and still reporting the old version, so it is offered again.
// Two updaters then unpack the same release over the same directory at once, which is how a node
// produced two "update started" messages and stayed on the old build.
public sealed class SatelliteUpdateGateTests
{
    [Fact]
    public void SecondOfferForTheSameSatelliteIsRefusedWhileTheFirstIsInFlight()
    {
        var gate = new SatelliteUpdateGate();

        Assert.True(gate.TryBeginOffer("netrunner", "0.2.216"));
        Assert.False(gate.TryBeginOffer("netrunner", "0.2.216"));
    }

    [Fact]
    public void AnInFlightOfferBlocksEvenWhenTheReportedVersionDiffers()
    {
        var gate = new SatelliteUpdateGate();

        // A satellite keeps reporting its old version for the whole time it is updating and
        // restarting, but the point stands either way: one updater at a time, per satellite.
        Assert.True(gate.TryBeginOffer("netrunner", "0.2.216"));
        Assert.False(gate.TryBeginOffer("netrunner", "0.2.217"));
    }

    [Fact]
    public void AnAnsweredOfferIsNotRepeatedAtTheSameVersion()
    {
        var gate = new SatelliteUpdateGate();

        Assert.True(gate.TryBeginOffer("netrunner", "0.2.216"));
        gate.CompleteOffer("netrunner", "0.2.216", retryable: false);

        Assert.False(gate.TryBeginOffer("netrunner", "0.2.216"));
    }

    [Fact]
    public void ASatelliteBackOnAnOlderBuildIsOfferedAgain()
    {
        var gate = new SatelliteUpdateGate();

        Assert.True(gate.TryBeginOffer("netrunner", "0.2.216"));
        gate.CompleteOffer("netrunner", "0.2.216", retryable: false);

        Assert.True(gate.TryBeginOffer("netrunner", "0.2.217"));
    }

    [Fact]
    public void ATransportFailureIsRetriedRatherThanWrittenOff()
    {
        var gate = new SatelliteUpdateGate();

        Assert.True(gate.TryBeginOffer("netrunner", "0.2.216"));
        // Forgetting a failed offer matters because the version it was keyed on never changes
        // for a satellite whose update never ran - so it would otherwise never be asked again.
        gate.CompleteOffer("netrunner", "0.2.216", retryable: true);

        Assert.True(gate.TryBeginOffer("netrunner", "0.2.216"));
    }

    [Fact]
    public void DifferentSatellitesDoNotBlockEachOther()
    {
        var gate = new SatelliteUpdateGate();

        Assert.True(gate.TryBeginOffer("netrunner", "0.2.216"));
        Assert.True(gate.TryBeginOffer("adamsmasher", "0.2.216"));
        Assert.True(gate.TryBeginOffer("D2R_1", "0.2.216"));
    }

    [Fact]
    public void ConcurrentOffersYieldExactlyOneWinner()
    {
        var gate = new SatelliteUpdateGate();
        var winners = 0;

        Parallel.For(0, 64, _ =>
        {
            if (gate.TryBeginOffer("netrunner", "0.2.216"))
            {
                Interlocked.Increment(ref winners);
            }
        });

        Assert.Equal(1, winners);
    }
}
