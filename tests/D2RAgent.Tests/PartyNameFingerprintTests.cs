using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class PartyNameFingerprintTests
{
    [Fact]
    public void SerializationRoundTripsExactly()
    {
        var original = BuildMask(10, 4, (x, y) => (x + y) % 3 == 0);

        var restored = PartyNameFingerprint.FromBase64(original.ToBase64());

        Assert.NotNull(restored);
        Assert.Equal(original.Width, restored.Width);
        Assert.Equal(original.Height, restored.Height);
        Assert.Equal(original.PackedBits, restored.PackedBits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-fingerprint")]
    [InlineData("pn1:0:4:AA==")]
    [InlineData("pn1:4:0:AA==")]
    [InlineData("pn1:-4:4:AA==")]
    [InlineData("pn1:600:600:AA==")]
    [InlineData("pn1:8:2:%%%")]
    [InlineData("pn2:8:2:AAA=")]
    [InlineData("24:4:AAAA")]
    public void FromBase64RejectsInvalidInput(string? serialized)
    {
        Assert.Null(PartyNameFingerprint.FromBase64(serialized));
    }

    [Fact]
    public void FromBase64RejectsPackedLengthMismatch()
    {
        // 8x2 = 16 bits = 2 packed bytes; three bytes must not deserialize.
        Assert.Null(PartyNameFingerprint.FromBase64($"pn1:8:2:{Convert.ToBase64String(new byte[3])}"));
    }

    [Fact]
    public void FromPixelsRejectsMismatchedBufferLength()
    {
        Assert.Null(PartyNameFingerprint.FromPixels(new byte[10], 2, 2));
    }

    [Fact]
    public void FromPixelsClassifiesNameTextPixels()
    {
        // One near-white "name" pixel (the color measured on every reference capture) among
        // dark scene pixels.
        var rgb = new byte[4 * 3];
        rgb[3] = 245;
        rgb[4] = 244;
        rgb[5] = 243;

        var mask = PartyNameFingerprint.FromPixels(rgb, 4, 1);

        Assert.NotNull(mask);
        Assert.False(mask.GetBit(0, 0));
        Assert.True(mask.GetBit(1, 0));
        Assert.False(mask.GetBit(2, 0));
        Assert.Equal(1, mask.BitCount);
    }

    [Theory]
    [InlineData(245, 244, 243, true)] // measured name text
    [InlineData(30, 90, 40, false)] // grass
    [InlineData(255, 140, 20, false)] // torch flame: bright but saturated
    [InlineData(60, 60, 60, false)] // dark stone: grey but dim
    [InlineData(160, 150, 120, true)] // dimmed antialiased glyph edge still counts
    public void IsNameTextColorMatchesTheMeasuredProfile(byte red, byte green, byte blue, bool expected)
    {
        Assert.Equal(expected, PartyNameFingerprint.IsNameTextColor(red, green, blue));
    }

    [Fact]
    public void CropToGlyphBoxReturnsNullBelowMinGlyphBits()
    {
        var sparse = BuildMask(30, 10, (x, y) => x == 3 && y is >= 2 and < 5);

        Assert.Null(sparse.CropToGlyphBox());
    }

    [Fact]
    public void CropToGlyphBoxCropsToTheExactBitBoundingBox()
    {
        // A dense 8x4 block at offset (5,3) inside a 30x10 band.
        var band = BuildMask(30, 10, (x, y) => x is >= 5 and < 13 && y is >= 3 and < 7);

        var cropped = band.CropToGlyphBox();

        Assert.NotNull(cropped);
        Assert.Equal(8, cropped.Width);
        Assert.Equal(4, cropped.Height);
        Assert.Equal(32, cropped.BitCount);
        Assert.True(cropped.GetBit(0, 0));
        Assert.True(cropped.GetBit(7, 3));
    }

    [Fact]
    public void BestScoreInFindsTheTemplateAtAnyOffset()
    {
        var glyph = BuildMask(6, 5, (x, y) => (x + y) % 2 == 0);
        var band = BuildMask(40, 12, (x, y) =>
            x is >= 21 and < 27 && y is >= 4 and < 9 && ((x - 21) + (y - 4)) % 2 == 0);

        Assert.Equal(1.0, glyph.BestScoreIn(band), 9);
    }

    [Fact]
    public void BestScoreInIsZeroWhenTheBandIsSmallerThanTheTemplate()
    {
        var template = BuildMask(10, 5, (_, _) => true);
        var band = BuildMask(8, 5, (_, _) => true);

        Assert.Equal(0.0, template.BestScoreIn(band));
    }

    [Fact]
    public void BestScoreInIsZeroForAnEmptyBand()
    {
        var template = BuildMask(6, 4, (_, _) => true);
        var band = BuildMask(20, 8, (_, _) => false);

        Assert.Equal(0.0, template.BestScoreIn(band));
    }

    [Fact]
    public void ExtraBrightPixelsInsideTheWindowLowerTheScore()
    {
        var template = BuildMask(6, 4, (x, _) => x < 3);
        var noisyBand = BuildMask(6, 4, (_, _) => true);

        // Window bits = 24, template bits = 12, intersection = 12 -> dice 24/36.
        Assert.Equal(2.0 * 12 / 36, template.BestScoreIn(noisyBand), 9);
    }

    [Fact]
    public void IsMatchHandlesNullsAndThreshold()
    {
        var mask = BuildMask(6, 4, (_, _) => true);

        Assert.False(PartyNameFingerprint.IsMatch(null, mask));
        Assert.False(PartyNameFingerprint.IsMatch(mask, null));
        Assert.True(PartyNameFingerprint.IsMatch(mask, mask));
    }

    private static PartyNameFingerprint BuildMask(int width, int height, Func<int, int, bool> bit)
    {
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (bit(x, y))
                {
                    var index = ((y * width) + x) * 3;
                    rgb[index] = 245;
                    rgb[index + 1] = 244;
                    rgb[index + 2] = 243;
                }
            }
        }

        var mask = PartyNameFingerprint.FromPixels(rgb, width, height);
        Assert.NotNull(mask);
        return mask;
    }
}
