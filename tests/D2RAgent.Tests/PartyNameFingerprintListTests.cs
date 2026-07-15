using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class PartyNameFingerprintListTests
{
    private static string ValidFingerprint(byte seed)
    {
        var packed = new byte[3];
        packed[0] = seed;
        return new PartyNameFingerprint(8, 3, packed).ToBase64();
    }

    // A pre-multi single-template leader-template.txt is just a one-entry list - old files must
    // keep working across the multi-alt upgrade without a migration step.
    [Fact]
    public void SingleLineLegacyFileParsesAsOneEntry()
    {
        var fingerprint = ValidFingerprint(0x11);

        var list = PartyNameFingerprintList.Normalize(fingerprint);

        Assert.Equal(new[] { fingerprint }, list);
    }

    [Fact]
    public void NormalizeDropsInvalidLinesAndExactDuplicatesButKeepsOrder()
    {
        var first = ValidFingerprint(0x11);
        var second = ValidFingerprint(0x22);
        var content = $"{first}\n\nnot-a-fingerprint\n{second}\r\n{first}\n";

        var list = PartyNameFingerprintList.Normalize(content);

        Assert.Equal(new[] { first, second }, list);
    }

    [Fact]
    public void AppendAddsAtTheEndAndIgnoresDuplicatesAndGarbage()
    {
        var first = ValidFingerprint(0x11);
        var second = ValidFingerprint(0x22);
        var list = PartyNameFingerprintList.Normalize(first);

        list = PartyNameFingerprintList.Append(list, second);
        list = PartyNameFingerprintList.Append(list, second);
        list = PartyNameFingerprintList.Append(list, "garbage");

        Assert.Equal(new[] { first, second }, list);
    }

    // The bind-rollback path: removing one bad capture must leave every other bound alt intact.
    [Fact]
    public void RemoveDeletesOnlyTheMatchingEntry()
    {
        var first = ValidFingerprint(0x11);
        var second = ValidFingerprint(0x22);
        var third = ValidFingerprint(0x33);
        var list = PartyNameFingerprintList.Normalize($"{first}\n{second}\n{third}");

        list = PartyNameFingerprintList.Remove(list, second);

        Assert.Equal(new[] { first, third }, list);
    }

    [Fact]
    public void SerializeRoundTripsThroughNormalize()
    {
        var entries = new[] { ValidFingerprint(0x11), ValidFingerprint(0x22), ValidFingerprint(0x33) };
        var list = PartyNameFingerprintList.Normalize(string.Join("\n", entries));

        var roundTripped = PartyNameFingerprintList.Normalize(PartyNameFingerprintList.Serialize(list));

        Assert.Equal(entries, roundTripped);
    }
}
