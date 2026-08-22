using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

// End-to-end check of PartyMemberSlots' name-band geometry + PartyNameFingerprint against the real
// 1366x768/rotw_ingame_party_members_7.png reference screenshot, sampled through
// FullCaptureRegionSampler.CapturePixelRegion exactly the way WindowsInput.CapturePixelRegion
// samples the live screen - the same "prove it against a real capture" role
// PartyMemberCountReferenceTests plays for the portrait-count classifier.
//
// Replaces the party_members_0..3.png set, which was captured in legacy graphics. Two things
// changed with the modern HUD, both load-bearing here:
//
// - The name band no longer sits over the black pillarbox bar. It sits over the live game world,
//   so its backdrop moves between frames. What keeps that from mattering is that the band is only
//   ever sampled for a slot the frame check has ALREADY confirmed occupied (see
//   CountOtherPartyMembers' visibleMembers bound), so a name band over open terrain is not a state
//   production can reach.
// - The text is dimmer and warmer than legacy's near-white, which is what forced the green-blue cap
//   in IsNameTextColor open from 55 to 70; see the comment there.
//
// The seven names in the full-party capture are a wider discrimination set than the legacy corpus
// managed: 42 different-name pairs against the old 8, and they cover the hard case directly - Grid,
// Ras and Shar are 3-4 character names whose glyph boxes are only 16-19px wide, which is where two
// different names look most alike.
//
// The 1- and 2-member captures supply the other half of the calibration, which a single capture
// could not: Calypso appears in all three and Trinity in two of them - in slot 2 of one and slot 7
// of another - so the same name is measured across different slots AND different scene backgrounds,
// which is the pair that produced legacy's 0.757 same-name floor.
public sealed class PartyNameFingerprintReferenceTests
{
    private const string Capture = "rotw_ingame_party_members_7.png";
    private const string OneMember = "rotw_ingame_party_members_1.png";
    private const string TwoMembers = "rotw_ingame_party_members_2.png";

    // Who is in which slot at each party size, read off the captures. D2R keeps this panel SORTED,
    // so a member's slot index changes as others join and leave - see
    // SlotAssignmentIsNotStableAcrossPartyChanges below.
    private static readonly string[][] Occupants =
    [
        ["Calypso"],
        ["Calypso", "Trinity"],
        ["Calypso", "Shar", "Trinity"],
        ["Calypso", "Iammer", "Shar", "Trinity"],
        ["Calypso", "Iammer", "Odysseus", "Shar", "Trinity"],
        ["Calypso", "Iammer", "Odysseus", "Ras", "Shar", "Trinity"],
        ["Calypso", "Grid", "Iammer", "Odysseus", "Ras", "Shar", "Trinity"],
    ];

    private static string CaptureFor(int members) => $"rotw_ingame_party_members_{members}.png";

    // Slot -> the name actually rendered there, read off the capture.
    private static readonly (int Slot, string Name)[] NamedSlots =
    [
        (1, "Calypso"), (2, "Grid"), (3, "Iammer"), (4, "Odysseus"),
        (5, "Ras"), (6, "Shar"), (7, "Trinity"),
    ];

    [Theory]
    [InlineData(1, 37, 10, 43)] // Calypso
    [InlineData(2, 19, 8, 33)] // Grid
    [InlineData(3, 34, 8, 51)] // Iammer
    [InlineData(4, 44, 9, 56)] // Odysseus - longest name on file
    [InlineData(5, 16, 8, 27)] // Ras - fewest glyph bits of the seven
    [InlineData(6, 18, 8, 27)] // Shar
    [InlineData(7, 25, 9, 44)] // Trinity
    public void GlyphBoxesMatchTheMeasuredReferenceCapture(
        int slot, int expectedWidth, int expectedHeight, int expectedBits)
    {
        var template = CaptureTemplate(Capture, slot);

        Assert.NotNull(template);
        Assert.Equal(expectedWidth, template.Width);
        Assert.Equal(expectedHeight, template.Height);
        Assert.Equal(expectedBits, template.BitCount);
    }

    // The guard that broke first when the HUD changed: with the pre-RoTW green-blue cap, the four
    // shortest names captured 12-20 bits against MinGlyphBits = 24 and produced no template at all,
    // so binding any of them silently failed. The shortest name here must clear the guard with room.
    [Fact]
    public void EveryNameClearsTheMinimumGlyphBitGuard()
    {
        foreach (var (slot, name) in NamedSlots)
        {
            var template = CaptureTemplate(Capture, slot);
            Assert.True(
                template is not null,
                $"{name} (slot {slot}) produced no glyph box; MinGlyphBits is {PartyNameFingerprint.MinGlyphBits}.");
            Assert.True(
                template!.BitCount >= PartyNameFingerprint.MinGlyphBits,
                $"{name} captured {template.BitCount} bits, under the {PartyNameFingerprint.MinGlyphBits} minimum.");
        }
    }

    // Real empty bands, which the solo captures cannot supply cleanly: on a party-less screen the
    // band is open terrain and a bright enough scene can produce a spurious glyph box (measured on
    // sitting_in_town3_lowestgfx.png). These are empty slots BELOW an occupied party, which is the
    // state production actually walks past, and they read zero matching pixels.
    [Theory]
    [InlineData(OneMember, 2)]
    [InlineData(OneMember, 3)]
    [InlineData(OneMember, 7)]
    [InlineData(TwoMembers, 3)]
    [InlineData(TwoMembers, 4)]
    [InlineData(TwoMembers, 7)]
    public void EmptySlotsBelowAnOccupiedPartyProduceNoGlyphBox(string fileName, int slot)
    {
        Assert.Equal(0, CaptureBand(fileName, slot).BitCount);
        Assert.Null(CaptureTemplate(fileName, slot));
    }

    // The counter-example, and the reason the occupancy guard exists rather than being an
    // efficiency measure: slot 6 of the 5-member capture is EMPTY, and its name band still returns
    // a full 48x9 glyph box of 88 bits - pure terrain, more bits than any real name in the corpus.
    // Reading name bands for slots the frame check has not confirmed would hand that to the matcher
    // as if it were a nametag.
    [Fact]
    public void AnEmptySlotsNameBandCanStillCaptureTerrainAsGlyphs()
    {
        var terrain = CaptureTemplate(CaptureFor(5), 6);

        Assert.NotNull(terrain);
        Assert.True(
            terrain!.BitCount > PartyNameFingerprint.MinGlyphBits,
            $"Expected the terrain band to clear MinGlyphBits; it captured {terrain.BitCount}.");
    }

    // The documented assumption this corpus overturned. The pre-RoTW comment claimed D2R "fills
    // slot 1 first and never reorders"; the ladder shows the opposite. Adding a member INSERTS them
    // in sorted position and pushes everyone below down a slot: Trinity occupies slot 2 at a
    // two-member party and slot 7 at a full one, and Shar was inserted ABOVE Trinity rather than
    // appended after it. Every capture is a strict alphabetical subsequence.
    //
    // No production code depends on slot stability - FollowAutoPulse scans every visible band and
    // scores all bound nametags against each - and this test exists so that stays true. Caching
    // "the leader was in slot 3" would break the moment anyone else joined.
    [Fact]
    public void SlotAssignmentIsNotStableAcrossPartyChanges()
    {
        var trinityAtTwo = Array.IndexOf(Occupants[1], "Trinity") + 1;
        var trinityAtSeven = Array.IndexOf(Occupants[6], "Trinity") + 1;
        Assert.Equal(2, trinityAtTwo);
        Assert.Equal(7, trinityAtSeven);

        // And the occupant lists are what the pixels actually say, not a transcription.
        for (var members = 1; members <= 7; members++)
        {
            for (var slot = 1; slot <= members; slot++)
            {
                var expected = Occupants[members - 1][slot - 1];
                var band = CaptureBand(CaptureFor(members), slot);

                var bestName = "";
                var bestScore = 0.0;
                for (var i = 0; i < NamedSlots.Length; i++)
                {
                    var score = CaptureTemplate(Capture, NamedSlots[i].Slot)!.BestScoreIn(band);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestName = NamedSlots[i].Name;
                    }
                }

                Assert.True(
                    bestName == expected && bestScore >= PartyNameFingerprint.MatchThreshold,
                    $"{members}-member capture slot {slot}: expected {expected}, best match was {bestName} at {bestScore:F3}.");
            }
        }
    }

    [Fact]
    public void EveryNameMatchesItsOwnBand()
    {
        foreach (var (slot, name) in NamedSlots)
        {
            var template = CaptureTemplate(Capture, slot);
            Assert.NotNull(template);
            Assert.True(
                PartyNameFingerprint.IsMatch(template, CaptureBand(Capture, slot)),
                $"{name} did not match its own band, scoring {template!.BestScoreIn(CaptureBand(Capture, slot)):F3}.");
        }
    }

    // All 42 different-name pairs. This is the property the follow-auto pulse actually depends on:
    // a bound member's template must not match somebody else's row.
    [Fact]
    public void NoNameMatchesAnyOtherNamesBand()
    {
        foreach (var (slotA, nameA) in NamedSlots)
        {
            var template = CaptureTemplate(Capture, slotA);
            Assert.NotNull(template);

            foreach (var (slotB, nameB) in NamedSlots)
            {
                if (slotA == slotB)
                {
                    continue;
                }

                var score = template!.BestScoreIn(CaptureBand(Capture, slotB));
                Assert.False(
                    score >= PartyNameFingerprint.MatchThreshold,
                    $"{nameA} scored {score:F3} against {nameB}'s band, at/above the {PartyNameFingerprint.MatchThreshold} threshold.");
            }
        }
    }

    // The same-name side of the calibration, measured across captures rather than within one.
    // Calypso is in slot 1 of all three captures against three different scene backdrops; Trinity
    // is in slot 7 of the full party and slot 2 of the two-member capture, so it also crosses a
    // slot boundary. Every one of these scores 0.988 or better - comfortably tighter than the
    // legacy corpus's 0.757 floor, because modern draws all names on one baseline and so has no
    // thin-stroke staggered variant to match against.
    [Theory]
    [InlineData(Capture, 1, OneMember, 1, "Calypso, full party vs solo companion")]
    [InlineData(OneMember, 1, Capture, 1, "Calypso, reverse")]
    [InlineData(Capture, 1, TwoMembers, 1, "Calypso, full party vs two-member")]
    [InlineData(OneMember, 1, TwoMembers, 1, "Calypso, one-member vs two-member")]
    [InlineData(Capture, 7, TwoMembers, 2, "Trinity, slot 7 vs slot 2")]
    [InlineData(TwoMembers, 2, Capture, 7, "Trinity, slot 2 vs slot 7")]
    public void TheSameNameMatchesAcrossSlotsAndSceneBackgrounds(
        string templateFile, int templateSlot, string bandFile, int bandSlot, string because)
    {
        var template = CaptureTemplate(templateFile, templateSlot);
        var band = CaptureBand(bandFile, bandSlot);

        Assert.NotNull(template);
        Assert.True(
            PartyNameFingerprint.IsMatch(template, band),
            $"{because}: scored {template!.BestScoreIn(band):F3}, below the {PartyNameFingerprint.MatchThreshold} threshold.");
    }

    // The real leave-detection flow, now reproducible on the modern HUD: everyone but Calypso left,
    // so every other bound name must fail to match the one band still on screen. This is the
    // scenario the legacy party_glitch_* captures were kept for - a bound member's template
    // matching a DIFFERENT remaining name is what wedged follow-auto in
    // watch-follow-auto-20260717-124344.log.
    [Fact]
    public void DepartedMembersAreAbsentFromTheOneRemainingBand()
    {
        var remaining = CaptureBand(OneMember, 1);

        foreach (var (slot, name) in NamedSlots)
        {
            if (name == "Calypso")
            {
                continue;
            }

            var template = CaptureTemplate(Capture, slot);
            Assert.NotNull(template);

            var score = template!.BestScoreIn(remaining);
            Assert.False(
                PartyNameFingerprint.IsMatch(template, remaining),
                $"Departed {name} scored {score:F3} against the remaining band.");
            Assert.True(
                PartyNameFingerprint.MatchThreshold - score >= 0.15,
                $"Departed {name} leaves under 0.15 false-match margin: {score:F3} vs {PartyNameFingerprint.MatchThreshold}.");
        }
    }

    // Documents the calibration margin MatchThreshold (0.65) rests on so a future threshold or
    // classifier change that erodes it fails loudly. Measured across all three captures: the worst
    // same-name pair is 0.988 and the worst different-name pair is Grid vs Shar at 0.433 - two
    // 4-character names, which is exactly where confusion should peak. 0.65 sits between them with
    // 0.338 of margin above and 0.217 below.
    [Fact]
    public void ThresholdKeepsAMarginToBothFailureDirections()
    {
        var worstSameName = 1.0;
        foreach (var (templateFile, templateSlot, bandFile, bandSlot) in new[]
        {
            (Capture, 1, OneMember, 1), (OneMember, 1, Capture, 1),
            (Capture, 1, TwoMembers, 1), (TwoMembers, 1, Capture, 1),
            (OneMember, 1, TwoMembers, 1), (TwoMembers, 1, OneMember, 1),
            (Capture, 7, TwoMembers, 2), (TwoMembers, 2, Capture, 7),
        })
        {
            worstSameName = Math.Min(
                worstSameName,
                CaptureTemplate(templateFile, templateSlot)!.BestScoreIn(CaptureBand(bandFile, bandSlot)));
        }

        Assert.True(
            worstSameName - PartyNameFingerprint.MatchThreshold >= 0.15,
            $"Same-name floor {worstSameName:F3} is within 0.15 of the {PartyNameFingerprint.MatchThreshold} threshold.");

        AssertDifferentNameCeiling();
    }

    private static void AssertDifferentNameCeiling()
    {
        var bestDifferentName = 0.0;
        foreach (var (slotA, _) in NamedSlots)
        {
            foreach (var (slotB, _) in NamedSlots)
            {
                if (slotA == slotB)
                {
                    continue;
                }

                bestDifferentName = Math.Max(
                    bestDifferentName,
                    CaptureTemplate(Capture, slotA)!.BestScoreIn(CaptureBand(Capture, slotB)));
            }
        }

        Assert.True(
            PartyNameFingerprint.MatchThreshold - bestDifferentName >= 0.15,
            $"Different-name ceiling {bestDifferentName:F3} is within 0.15 of the {PartyNameFingerprint.MatchThreshold} threshold.");
    }

    private static PartyNameFingerprint CaptureBand(string fileName, int slot)
    {
        var region = FullCaptureRegionSampler.CapturePixelRegion(
            fileName,
            PartyMemberSlots.GetSlotNameBandCenter(slot),
            PartyMemberSlots.NameBandWidthRatio,
            PartyMemberSlots.NameBandHeightRatio);
        var mask = PartyNameFingerprint.FromPixels(region.Rgb, region.Width, region.Height);
        Assert.NotNull(mask);
        return mask;
    }

    private static PartyNameFingerprint? CaptureTemplate(string fileName, int slot)
    {
        return CaptureBand(fileName, slot).CropToGlyphBox();
    }
}
