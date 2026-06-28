using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FriendFingerprintTests
{
    private static FriendFingerprint MakeFingerprint(params byte[] samples)
    {
        return new FriendFingerprint(GridColumns: samples.Length, GridRows: 1, Samples: samples);
    }

    [Fact]
    public void IdenticalFingerprintsMatch()
    {
        var a = MakeFingerprint(10, 20, 30, 200, 150, 90);
        var b = MakeFingerprint(10, 20, 30, 200, 150, 90);

        Assert.True(FriendFingerprint.IsMatch(a, b));
    }

    [Fact]
    public void SmallRenderingNoiseStillMatches()
    {
        var a = MakeFingerprint(10, 20, 30, 200, 150, 90);
        var b = MakeFingerprint(12, 18, 33, 198, 153, 88);

        Assert.True(FriendFingerprint.IsMatch(a, b));
    }

    [Fact]
    public void SubstantiallyDifferentFingerprintsDoNotMatch()
    {
        var a = MakeFingerprint(10, 20, 30, 200, 150, 90);
        var b = MakeFingerprint(240, 230, 220, 5, 8, 3);

        Assert.False(FriendFingerprint.IsMatch(a, b));
    }

    [Fact]
    public void MismatchedGridShapeDoesNotMatch()
    {
        var a = new FriendFingerprint(GridColumns: 4, GridRows: 1, Samples: [10, 20, 30, 40]);
        var b = new FriendFingerprint(GridColumns: 2, GridRows: 1, Samples: [10, 20]);

        Assert.False(FriendFingerprint.IsMatch(a, b));
    }

    [Fact]
    public void NullFingerprintsDoNotMatch()
    {
        var a = MakeFingerprint(10, 20, 30);

        Assert.False(FriendFingerprint.IsMatch(a, null));
        Assert.False(FriendFingerprint.IsMatch(null, a));
        Assert.False(FriendFingerprint.IsMatch(null, null));
    }

    [Fact]
    public void RoundTripsThroughBase64()
    {
        var original = MakeFingerprint(1, 2, 3, 4, 5, 6, 250, 251, 252);

        var restored = FriendFingerprint.FromBase64(original.ToBase64());

        Assert.NotNull(restored);
        Assert.Equal(original.GridColumns, restored!.GridColumns);
        Assert.Equal(original.GridRows, restored.GridRows);
        Assert.Equal(original.Samples, restored.Samples);
    }

    [Fact]
    public void FromBase64RejectsGarbageWithoutThrowing()
    {
        Assert.Null(FriendFingerprint.FromBase64("not-a-valid-fingerprint"));
        Assert.Null(FriendFingerprint.FromBase64(""));
        Assert.Null(FriendFingerprint.FromBase64(null));
        Assert.Null(FriendFingerprint.FromBase64("4:4:not-valid-base64!!"));
    }
}
