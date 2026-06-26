using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// End-to-end check of PartyMemberSlots + PartyFrameClassifier against the real reference
// screenshots (issue #20, item 6: docs/runbooks/assets/d2r-ui/1366x768/party_members_0..3.png),
// sampled through FullCaptureRegionSampler exactly the way WindowsInput.SamplePartyFrameRatio
// samples the live screen. This is what actually proves the geometry+threshold combination
// reads back the right count from a real capture, not just that the isolated unit math is
// internally consistent.
public sealed class PartyMemberCountReferenceTests
{
    [Theory]
    [InlineData("party_members_0.png", 0)]
    [InlineData("party_members_1.png", 1)]
    [InlineData("party_members_2.png", 2)]
    [InlineData("party_members_3.png", 3)]
    public void CountsMatchTheReferenceScreenshotsExactly(string fileName, int expectedOtherMembers)
    {
        Assert.Equal(expectedOtherMembers, CountOtherPartyMembers(fileName));
    }

    // Mirrors the "scan slots in order, stop at the first miss" shape the real heartbeat
    // consumer will use - D2R fills slots left-to-right with no gaps, confirmed by every
    // reference screenshot above.
    private static int CountOtherPartyMembers(string fileName)
    {
        for (var slot = 1; slot <= PartyMemberSlots.MaxSlots; slot++)
        {
            var ratio = FullCaptureRegionSampler.SamplePartyFrameRatio(
                fileName,
                PartyMemberSlots.GetSlotTopEdgeCenter(slot),
                PartyMemberSlots.EdgeWidthRatio,
                PartyMemberSlots.EdgeHeightRatio);
            if (ratio < PartyMemberSlots.FrameRatioThreshold)
            {
                return slot - 1;
            }
        }

        return PartyMemberSlots.MaxSlots;
    }
}
