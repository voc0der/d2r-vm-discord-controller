namespace AgentCommon;

// A binary mask of the near-white name text D2R draws under a party-bar portrait, used to
// recognize the bound leader's character name in-game from any VM. This deliberately does NOT
// reuse FriendFingerprint's raw-RGB grid comparison: the friends drawer draws names on a fixed
// dark panel, so raw RGB averages work there, but the party bar draws names straight over the
// game world - the background behind the same name changes every game and every screen, and a
// measured same-name/different-background pair scored 32.8 average difference against
// FriendFingerprint's 30.6 match cutoff (see docs/runbooks/pixel-classifier-catalog.md). Only
// the white glyph pixels are stable, so this classifies each pixel as "name text or not" and
// compares the resulting glyph shapes instead.
//
// The same measurements showed D2R renders the same name at slightly different stroke weights
// and on one of two vertical baselines depending on neighbor-name collisions (see
// PartyMemberSlots in D2RAgent for the band geometry), so a template is cropped to its glyph
// bounding box at bind time and matched by sliding it across a captured band mask and taking
// the best Dice overlap - alignment problems become the slide's job instead of a capture-time
// assumption.
public sealed record PartyNameFingerprint(int Width, int Height, byte[] PackedBits)
{
    // Reference-capture calibration across party_members_1..3.png (same math as
    // PartyNameFingerprintReferenceTests): worst same-name score 0.762 (the same name rendered
    // thin on the lower baseline vs bold on the upper), best different-name score 0.551, empty
    // bands 0.0. 0.65 sits roughly midway with ~0.1 margin to both failure directions.
    public const double MatchThreshold = 0.65;

    // The shortest legal D2R character name (2 chars) still renders well above this many glyph
    // pixels; an empty or portrait-only band measured exactly 0 across every reference capture.
    // This is a "did we actually capture a name" guard for bind time, not a match tuning knob.
    public const int MinGlyphBits = 24;

    private const string SerializationPrefix = "pn1";
    private const int MaxDimension = 512;

    // Name text measured (245,244,243) on every reference capture regardless of scene; the
    // luminance floor is 120 rather than something tighter because the staggered lower-baseline
    // rendering antialiases the same glyphs dimmer (rows measured only above lum 100-120 there).
    // The channel-difference caps reject bright saturated scene pixels (torches, gold frames)
    // while keeping the slightly warm white D2R uses.
    public static bool IsNameTextColor(byte red, byte green, byte blue)
    {
        var luminance = (red * 0.2126) + (green * 0.7152) + (blue * 0.0722);
        return luminance > 120
            && Math.Abs(red - green) < 35
            && Math.Abs(green - blue) < 55;
    }

    // Builds a full-band mask from an RGB pixel block (3 bytes per pixel, row-major), e.g. a
    // WindowsInput.CapturePixelRegion result. Callers crop with CropToGlyphBox for a template,
    // or keep the full band as the probe side of IsMatch.
    public static PartyNameFingerprint? FromPixels(byte[] rgbPixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgbPixels.Length != width * height * 3)
        {
            return null;
        }

        var packed = new byte[PackedLength(width, height)];
        for (var i = 0; i < width * height; i++)
        {
            if (IsNameTextColor(rgbPixels[i * 3], rgbPixels[(i * 3) + 1], rgbPixels[(i * 3) + 2]))
            {
                packed[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return new PartyNameFingerprint(width, height, packed);
    }

    public int BitCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < Width * Height; i++)
            {
                if (GetBit(i % Width, i / Width))
                {
                    count++;
                }
            }

            return count;
        }
    }

    public bool GetBit(int x, int y)
    {
        var index = (y * Width) + x;
        return (PackedBits[index / 8] & (0x80 >> (index % 8))) != 0;
    }

    // Crops to the glyph bounding box so the stored template carries no dependency on where in
    // the band the name happened to sit (slot centering jitters +-1px and the baseline staggers
    // ~12px). Returns null when the band doesn't plausibly contain a name at all.
    public PartyNameFingerprint? CropToGlyphBox()
    {
        int minX = Width, minY = Height, maxX = -1, maxY = -1, bits = 0;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (!GetBit(x, y))
                {
                    continue;
                }

                bits++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (bits < MinGlyphBits)
        {
            return null;
        }

        var croppedWidth = maxX - minX + 1;
        var croppedHeight = maxY - minY + 1;
        var packed = new byte[PackedLength(croppedWidth, croppedHeight)];
        for (var y = 0; y < croppedHeight; y++)
        {
            for (var x = 0; x < croppedWidth; x++)
            {
                if (GetBit(minX + x, minY + y))
                {
                    var index = (y * croppedWidth) + x;
                    packed[index / 8] |= (byte)(0x80 >> (index % 8));
                }
            }
        }

        return new PartyNameFingerprint(croppedWidth, croppedHeight, packed);
    }

    // Slides this template over every position inside the band mask and returns the best Dice
    // overlap (2*intersection / (template bits + band bits inside the window)). Band bits
    // outside the current window don't count against the score, so a stray bright pixel
    // elsewhere in the band can't sink an otherwise clean match; extra bright pixels inside the
    // window do lower it, which is the conservative direction (a missed match makes follow-auto
    // fall back to player-count behavior, not act on a wrong name).
    public double BestScoreIn(PartyNameFingerprint band)
    {
        if (band.Width < Width || band.Height < Height)
        {
            return 0.0;
        }

        var templateBits = BitCount;
        if (templateBits == 0)
        {
            return 0.0;
        }

        var best = 0.0;
        for (var offsetY = 0; offsetY <= band.Height - Height; offsetY++)
        {
            for (var offsetX = 0; offsetX <= band.Width - Width; offsetX++)
            {
                var intersection = 0;
                var windowBits = 0;
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        if (!band.GetBit(offsetX + x, offsetY + y))
                        {
                            continue;
                        }

                        windowBits++;
                        if (GetBit(x, y))
                        {
                            intersection++;
                        }
                    }
                }

                var denominator = templateBits + windowBits;
                if (denominator > 0)
                {
                    best = Math.Max(best, 2.0 * intersection / denominator);
                }
            }
        }

        return best;
    }

    public static bool IsMatch(PartyNameFingerprint? template, PartyNameFingerprint? band)
    {
        return template is not null
            && band is not null
            && template.BestScoreIn(band) >= MatchThreshold;
    }

    public string ToBase64()
    {
        return $"{SerializationPrefix}:{Width}:{Height}:{Convert.ToBase64String(PackedBits)}";
    }

    public static PartyNameFingerprint? FromBase64(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return null;
        }

        var parts = serialized.Trim().Split(':', 4);
        if (parts.Length != 4
            || !string.Equals(parts[0], SerializationPrefix, StringComparison.Ordinal)
            || !int.TryParse(parts[1], out var width)
            || !int.TryParse(parts[2], out var height)
            || width <= 0 || height <= 0
            || width > MaxDimension || height > MaxDimension)
        {
            return null;
        }

        try
        {
            var packed = Convert.FromBase64String(parts[3]);
            return packed.Length == PackedLength(width, height)
                ? new PartyNameFingerprint(width, height, packed)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static int PackedLength(int width, int height)
    {
        return ((width * height) + 7) / 8;
    }
}
