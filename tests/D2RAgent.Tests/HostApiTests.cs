using AgentCommon;
using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

public sealed class HostApiKeyTests
{
    [Fact]
    public void EveryMintedKeyIsDistinct()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 200; i++)
        {
            Assert.True(keys.Add(HostApiKey.Generate().Key));
        }
    }

    [Fact]
    public void AMintedKeyIsPrefixedAndLongEnoughToBeUnguessable()
    {
        var generated = HostApiKey.Generate();

        Assert.StartsWith("d2rk_", generated.Key, StringComparison.Ordinal);
        // 32 random bytes, base64url, unpadded.
        Assert.Equal(43, generated.Key["d2rk_".Length..].Length);
    }

    [Fact]
    public void TheKeyIdIdentifiesTheKeyWithoutRevealingIt()
    {
        var generated = HostApiKey.Generate();

        Assert.Equal(generated.KeyId, HostApiKey.DescribeKey(generated.Key));
        Assert.EndsWith("...", generated.KeyId, StringComparison.Ordinal);
        // Six characters of a 43-character secret: enough to say which key is installed, nowhere
        // near enough to reconstruct it.
        Assert.Equal("d2rk_".Length + 6 + "...".Length, generated.KeyId.Length);
        // A displayed id must never be usable as a credential.
        Assert.False(HostApiKey.Matches(generated.KeyId, generated.Hash));
    }

    [Fact]
    public void TheStoredHashIsNotTheKey()
    {
        var generated = HostApiKey.Generate();

        Assert.NotEqual(generated.Key, generated.Hash);
        Assert.DoesNotContain(generated.Key["d2rk_".Length..], generated.Hash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, generated.Hash.Length);
    }

    [Fact]
    public void TheMintedKeyMatchesItsOwnHash()
    {
        var generated = HostApiKey.Generate();

        Assert.True(HostApiKey.Matches(generated.Key, generated.Hash));
    }

    [Fact]
    public void ADifferentKeyDoesNotMatch()
    {
        var generated = HostApiKey.Generate();
        var other = HostApiKey.Generate();

        Assert.False(HostApiKey.Matches(other.Key, generated.Hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentKeyNeverMatches(string? presented)
    {
        var generated = HostApiKey.Generate();

        Assert.False(HostApiKey.Matches(presented, generated.Hash));
    }

    [Fact]
    public void NoKeyMatchesAHostThatHasNotMintedOne()
    {
        Assert.False(HostApiKey.Matches(HostApiKey.Generate().Key, null));
        Assert.False(HostApiKey.Matches(HostApiKey.Generate().Key, ""));
    }

    // The hash is stored lower-case hex; a config hand-edited to upper case, or with stray
    // whitespace, must not silently stop every caller from authenticating.
    [Fact]
    public void AStoredHashIsMatchedCaseInsensitivelyAndTrimmed()
    {
        var generated = HostApiKey.Generate();

        Assert.True(HostApiKey.Matches(generated.Key, generated.Hash.ToUpperInvariant()));
        Assert.True(HostApiKey.Matches(generated.Key, $"  {generated.Hash}  "));
    }

    [Fact]
    public void TheKeyIsReadFromEitherHeaderForm()
    {
        Assert.Equal("abc", HostApiKey.ReadPresentedKey("Bearer abc", null));
        Assert.Equal("abc", HostApiKey.ReadPresentedKey("bearer abc", null));
        Assert.Equal("abc", HostApiKey.ReadPresentedKey(null, "abc"));
        Assert.Equal("abc", HostApiKey.ReadPresentedKey(null, "  abc  "));
        // A bare Authorization value with no scheme is accepted too - it is a common curl slip.
        Assert.Equal("abc", HostApiKey.ReadPresentedKey("abc", null));
    }

    [Fact]
    public void TheExplicitApiKeyHeaderWinsOverAuthorization()
    {
        Assert.Equal("chosen", HostApiKey.ReadPresentedKey("Bearer ignored", "chosen"));
    }

    [Fact]
    public void NoHeadersMeansNoKey()
    {
        Assert.Null(HostApiKey.ReadPresentedKey(null, null));
        Assert.Null(HostApiKey.ReadPresentedKey("", ""));
    }
}

public sealed class HostApiAuthorizationPolicyTests
{
    [Fact]
    public void AHostThatNeverEnabledTheApiRefusesEvenACorrectKey()
    {
        var generated = HostApiKey.Generate();

        Assert.Equal(
            HostApiAuthorization.Disabled,
            HostApiAuthorizationPolicy.Evaluate(enabled: false, generated.Hash, null, generated.Key));
    }

    // Enabled with nothing to check against must fail closed, not open.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEnabledApiWithNoStoredKeyRefusesEverything(string? storedHash)
    {
        Assert.Equal(
            HostApiAuthorization.Disabled,
            HostApiAuthorizationPolicy.Evaluate(enabled: true, storedHash, null, HostApiKey.Generate().Key));
    }

    [Fact]
    public void ACorrectKeyIsAllowedThroughEitherHeader()
    {
        var generated = HostApiKey.Generate();

        Assert.Equal(
            HostApiAuthorization.Allowed,
            HostApiAuthorizationPolicy.Evaluate(enabled: true, generated.Hash, null, generated.Key));
        Assert.Equal(
            HostApiAuthorization.Allowed,
            HostApiAuthorizationPolicy.Evaluate(enabled: true, generated.Hash, $"Bearer {generated.Key}", null));
    }

    [Fact]
    public void AWrongOrMissingKeyIsUnauthorizedRatherThanDisabled()
    {
        var generated = HostApiKey.Generate();

        Assert.Equal(
            HostApiAuthorization.Unauthorized,
            HostApiAuthorizationPolicy.Evaluate(enabled: true, generated.Hash, null, HostApiKey.Generate().Key));
        Assert.Equal(
            HostApiAuthorization.Unauthorized,
            HostApiAuthorizationPolicy.Evaluate(enabled: true, generated.Hash, null, null));
    }

    // Rotating a key must actually revoke the old one; that is the whole point of overwrite:true.
    [Fact]
    public void RotatingTheKeyLocksOutTheOldOne()
    {
        var original = HostApiKey.Generate();
        var replacement = HostApiKey.Generate();

        Assert.Equal(
            HostApiAuthorization.Unauthorized,
            HostApiAuthorizationPolicy.Evaluate(enabled: true, replacement.Hash, null, original.Key));
        Assert.Equal(
            HostApiAuthorization.Allowed,
            HostApiAuthorizationPolicy.Evaluate(enabled: true, replacement.Hash, null, replacement.Key));
    }
}

public sealed class DiscordSlashCommandCatalogTests
{
    // The catalog is what the API validates against and what GET /api/commands publishes. If it
    // ever stops covering Build(), the API silently loses commands Discord still offers.
    [Fact]
    public void EveryRegisteredSubcommandIsInTheCatalog()
    {
        var d2r = DiscordSlashCommands.Build()
            .OfType<Discord.SlashCommandProperties>()
            .Single(command => command.Name.Value == "d2r");

        var expected = new List<string>();
        foreach (var option in d2r.Options.Value)
        {
            if (option.Type == Discord.ApplicationCommandOptionType.SubCommandGroup)
            {
                expected.AddRange((option.Options ?? []).Select(nested => $"{option.Name} {nested.Name}"));
            }
            else if (option.Type == Discord.ApplicationCommandOptionType.SubCommand)
            {
                expected.Add(option.Name);
            }
        }

        Assert.Equal(
            expected.OrderBy(path => path, StringComparer.Ordinal),
            DiscordSlashCommandCatalog.All.Select(descriptor => descriptor.Path)
                .OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public void APlainSubcommandResolvesWithNoGroup()
    {
        var descriptor = DiscordSlashCommandCatalog.Find(group: null, "dclone");

        Assert.NotNull(descriptor);
        Assert.Null(descriptor!.Group);
        Assert.Equal("dclone", descriptor.Path);
        Assert.Contains(descriptor.Options, option => option.Name == "bots");
    }

    [Fact]
    public void AGroupedSubcommandNeedsItsGroup()
    {
        Assert.NotNull(DiscordSlashCommandCatalog.Find("vm", "start"));
        // "start" alone is /d2r start, a different command from /d2r vm start - and it must not
        // be reachable by omitting the group.
        var ungrouped = DiscordSlashCommandCatalog.Find(group: null, "start");
        Assert.NotNull(ungrouped);
        Assert.Null(ungrouped!.Group);
        Assert.Contains(ungrouped.Options, option => option.Name == "all");
    }

    [Fact]
    public void TheApiConfigCommandIsRegistered()
    {
        var descriptor = DiscordSlashCommandCatalog.Find("config", "api");

        Assert.NotNull(descriptor);
        Assert.Contains(descriptor!.Options, option => option.Name == "enabled" && option.Required);
        Assert.Contains(descriptor.Options, option => option.Name == "overwrite" && !option.Required);
    }

    [Fact]
    public void AnUnknownCommandDoesNotResolve()
    {
        Assert.Null(DiscordSlashCommandCatalog.Find(group: null, "definitely-not-a-command"));
        Assert.Null(DiscordSlashCommandCatalog.Find("vm", "dclone"));
        Assert.Null(DiscordSlashCommandCatalog.Find(group: null, null));
        Assert.Null(DiscordSlashCommandCatalog.Find(group: null, "  "));
    }

    [Fact]
    public void DifficultyChoicesArePublishedSoACallerCanDiscoverThem()
    {
        var descriptor = DiscordSlashCommandCatalog.Find(group: null, "dclone");
        var difficulty = Assert.Single(descriptor!.Options, option => option.Name == "difficulty");

        Assert.NotNull(difficulty.Choices);
        Assert.Contains("hell", difficulty.Choices!);
    }
}

public sealed class ApiCommandSinkTests
{
    // The handlers' own flow is "defer, then edit the original response", which reaches the sink
    // as repeated SetMessage calls. The last one is what the operator would have read in Discord.
    [Fact]
    public void TheLastPrimaryReplyIsTheAnswer()
    {
        var sink = new ApiCommandSink();
        sink.SetMessage("Queued.");
        sink.SetMessage("Done.");

        Assert.Equal("Done.", sink.ToResult(ok: true).Message);
    }

    [Fact]
    public void FollowUpsAreKeptInOrderBesideTheAnswer()
    {
        var sink = new ApiCommandSink();
        sink.SetMessage("leave complete: 2 succeeded, 1 failed.");
        sink.AddDetail("leave failed for hc3: agent offline.");
        sink.AddDetail("leave complete.");

        var result = sink.ToResult(ok: true);

        Assert.Equal("leave complete: 2 succeeded, 1 failed.", result.Message);
        Assert.Equal(
            new[] { "leave failed for hc3: agent offline.", "leave complete." },
            result.Details);
    }

    [Fact]
    public void ACommandThatSaidNothingStillAnswers()
    {
        Assert.Equal("Command completed.", new ApiCommandSink().ToResult(ok: true).Message);
        Assert.Equal("boom", new ApiCommandSink().ToResult(ok: false, error: "boom").Message);
    }

    [Fact]
    public void AnAttachedFileIsCarriedAsBase64()
    {
        var sink = new ApiCommandSink();
        var bytes = new byte[] { 1, 2, 3, 4 };
        sink.AttachFile("hc1-screenshot.png", "image/png", bytes);

        var file = sink.ToResult(ok: true).File;

        Assert.NotNull(file);
        Assert.Equal("hc1-screenshot.png", file!.FileName);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(bytes, Convert.FromBase64String(file.Base64));
    }

    [Fact]
    public void TheErrorNameIsCarriedSeparatelyFromTheMessage()
    {
        var sink = new ApiCommandSink();
        sink.SetMessage("Command failed: no such account.");

        var result = sink.ToResult(ok: false, error: "InvalidOperationException");

        Assert.False(result.Ok);
        Assert.Equal("Command failed: no such account.", result.Message);
        Assert.Equal("InvalidOperationException", result.Error);
    }
}
