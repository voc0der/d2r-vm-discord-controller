using System.Text.Json;
using AgentCommon;
using D2RHost;
using Microsoft.Data.Sqlite;
using Xunit;

namespace D2RAgent.Tests;

// The host is the authority for the follow bind and every VM agent holds a replica. These cover
// the two halves that make that safe: both sides canonicalizing content identically before
// digesting it, and the push planner deciding to act only on genuine divergence.
public sealed class FollowTemplateSyncTests
{
    private static string FriendFingerprint(byte seed)
    {
        return new FriendFingerprint(2, 2, [seed, 0x10, 0x20, 0x30]).ToBase64();
    }

    private static string LeaderFingerprint(byte seed)
    {
        return new PartyNameFingerprint(8, 3, [seed, 0x00, 0x00]).ToBase64();
    }

    private static AdvertisedFollowTemplates Advertise(string friend, IReadOnlyList<string> leaders)
    {
        return new AdvertisedFollowTemplates(
            true,
            FollowTemplateDigest.OfFriendTemplate(friend),
            FollowTemplateDigest.OfLeaderList(leaders));
    }

    // Both halves authoritative: the operator has bound (or explicitly unbound) friend row and
    // in-game nametags, which is the state after any full bind or /d2r follow bind:false.
    private static FollowTemplateState FullBind(string? friend, params string[] leaders)
    {
        return new FollowTemplateState(friend, leaders, "D2R_1", FriendRecorded: true, LeaderRecorded: true);
    }

    // Friend row bound, nametag rolodex never recorded by this host - the state right after a
    // host that predates template sync takes its first friend-row bind.
    private static FollowTemplateState FriendOnlyBind(string friend)
    {
        return new FollowTemplateState(friend, [], "D2R_1", FriendRecorded: true, LeaderRecorded: false);
    }

    // The agent writes its file with File.WriteAllText and reads it back through loaders that
    // trim; if a stray newline changed the digest the host would re-push the same fingerprint on
    // every single sweep, forever.
    [Fact]
    public void FriendDigestIgnoresSurroundingWhitespace()
    {
        var fingerprint = FriendFingerprint(0x41);

        Assert.Equal(
            FollowTemplateDigest.OfFriendTemplate(fingerprint),
            FollowTemplateDigest.OfFriendTemplate($"  {fingerprint}\r\n"));
    }

    [Fact]
    public void UnparseableOrEmptyContentDigestsAsNone()
    {
        Assert.Equal(FollowTemplateDigest.None, FollowTemplateDigest.OfFriendTemplate(null));
        Assert.Equal(FollowTemplateDigest.None, FollowTemplateDigest.OfFriendTemplate("   "));
        Assert.Equal(FollowTemplateDigest.None, FollowTemplateDigest.OfFriendTemplate("not-a-fingerprint"));
        Assert.Equal(FollowTemplateDigest.None, FollowTemplateDigest.OfLeaderContent("garbage"));
        Assert.False(FollowTemplateDigest.IsPresent(FollowTemplateDigest.None));
    }

    // The leader file is a rolodex whose order is the order the host locks nametags by, so two
    // agents holding the same names in a different order are genuinely divergent.
    [Fact]
    public void LeaderDigestIsOrderSensitiveButSurvivesLegacyFileFormatting()
    {
        var first = LeaderFingerprint(0x11);
        var second = LeaderFingerprint(0x22);

        Assert.NotEqual(
            FollowTemplateDigest.OfLeaderList([first, second]),
            FollowTemplateDigest.OfLeaderList([second, first]));
        Assert.Equal(
            FollowTemplateDigest.OfLeaderList([first, second]),
            FollowTemplateDigest.OfLeaderContent($"{first}\r\n{second}\n\n{first}\n"));
    }

    [Fact]
    public void AdvertisedDigestsParseTheAgentStatusShape()
    {
        var statusJson = JsonSerializer.Serialize(new
        {
            d2rRunning = true,
            followTemplates = new
            {
                friendDigest = "abc123",
                leaderDigest = "def456",
                leaderCount = 2,
                error = (string?)null
            }
        });

        var advertised = FollowTemplateStore.ReadAdvertisedDigests(statusJson);

        Assert.True(advertised.Known);
        Assert.Equal("abc123", advertised.FriendDigest);
        Assert.Equal("def456", advertised.LeaderDigest);
    }

    // An agent whose build predates the digest field must read as "cannot tell", never as
    // "holds nothing" - the latter would push to it on every sweep.
    [Fact]
    public void StatusWithoutTemplateDigestsIsUnknownRatherThanEmpty()
    {
        Assert.False(FollowTemplateStore.ReadAdvertisedDigests(null).Known);
        Assert.False(FollowTemplateStore.ReadAdvertisedDigests("{\"d2rRunning\":true}").Known);
        Assert.False(FollowTemplateStore.ReadAdvertisedDigests("not json").Known);
    }

    // Upgrade safety: on the first start after this feature ships the table is empty while agents
    // already hold a working bind from an older one. Treating that as an authoritative empty state
    // would clear every VM and silently destroy the operator's bind.
    [Fact]
    public void UnrecordedHostStateNeverPushesAnything()
    {
        var pushes = FollowTemplateStore.PlanPushes(
            FollowTemplateState.NotRecorded,
            Advertise(FriendFingerprint(0x41), [LeaderFingerprint(0x11)]),
            lastPushed: null);

        Assert.Empty(pushes);
    }

    [Fact]
    public void AgentHoldingTheAuthoritativeBindIsLeftAlone()
    {
        var friend = FriendFingerprint(0x41);
        var leader = LeaderFingerprint(0x11);

        var pushes = FollowTemplateStore.PlanPushes(
            FullBind(friend, leader),
            Advertise(friend, [leader]),
            lastPushed: null);

        Assert.Empty(pushes);
    }

    // The headline case: a VM that was offline during the bind, or added to the fleet afterwards,
    // comes up holding nothing at all.
    [Fact]
    public void AgentMissingEverythingReceivesBothTemplates()
    {
        var friend = FriendFingerprint(0x41);
        var leader = LeaderFingerprint(0x11);

        var pushes = FollowTemplateStore.PlanPushes(
            FullBind(friend, leader),
            Advertise("", []),
            lastPushed: null);

        Assert.Equal(
            ["follow_set_template", "follow_set_leader_template"],
            pushes.Select(push => push.Command).ToArray());
    }

    // A diverged rolodex is replaced wholesale rather than appended to, so an agent that missed
    // one bind and gained a stale one converges exactly instead of accumulating both.
    [Fact]
    public void StaleLeaderListIsReplacedNotAppended()
    {
        var friend = FriendFingerprint(0x41);
        var current = LeaderFingerprint(0x11);
        var stale = LeaderFingerprint(0x99);

        var pushes = FollowTemplateStore.PlanPushes(
            FullBind(friend, current),
            Advertise(friend, [stale]),
            lastPushed: null);

        var push = Assert.Single(pushes);
        Assert.Equal("follow_set_leader_template", push.Command);
        var args = JsonSerializer.SerializeToElement(push.Args);
        Assert.False(args.GetProperty("append").GetBoolean());
        Assert.Equal(current, args.GetProperty("fingerprint").GetString());
    }

    // An unbind has to reach VMs that were offline when it happened, or they would rejoin a later
    // session still following a friend the operator deliberately dropped.
    [Fact]
    public void RecordedUnbindClearsAnAgentThatStillHoldsATemplate()
    {
        var pushes = FollowTemplateStore.PlanPushes(
            FullBind(null),
            Advertise(FriendFingerprint(0x41), [LeaderFingerprint(0x11)]),
            lastPushed: null);

        var push = Assert.Single(pushes);
        Assert.Equal("follow_clear_template", push.Command);
    }

    [Fact]
    public void RecordedUnbindLeavesAnAlreadyClearAgentAlone()
    {
        var pushes = FollowTemplateStore.PlanPushes(
            FullBind(null),
            Advertise("", []),
            lastPushed: null);

        Assert.Empty(pushes);
    }

    // The push takes effect immediately but the agent's next heartbeat is up to an interval away
    // and still carries the pre-push digest. Without the grace window the sweep would re-push the
    // same content every cycle.
    [Fact]
    public void FreshPushIsNotRepeatedWhileTheHeartbeatIsStillCatchingUp()
    {
        var friend = FriendFingerprint(0x41);
        var state = FriendOnlyBind(friend);

        var pushes = FollowTemplateStore.PlanPushes(
            state,
            Advertise("", []),
            new FollowTemplateStore.PushRecord(
                state.FriendDigest,
                state.LeaderDigest,
                DateTimeOffset.UtcNow));

        Assert.Empty(pushes);
    }

    // Once the grace expires the agent's own report is trusted again, so a replica that really did
    // lose its file after a successful push is repaired instead of being assumed good forever.
    [Fact]
    public void ExpiredGracePeriodTrustsTheAgentReportAgain()
    {
        var friend = FriendFingerprint(0x41);
        var state = FriendOnlyBind(friend);

        var pushes = FollowTemplateStore.PlanPushes(
            state,
            Advertise("", []),
            new FollowTemplateStore.PushRecord(
                state.FriendDigest,
                state.LeaderDigest,
                DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10)));

        Assert.Equal(["follow_set_template"], pushes.Select(push => push.Command).ToArray());
    }

    // An agent too old to advertise digests cannot be diffed, so the host falls back to what it
    // last pushed that agent - otherwise every sweep would push it again forever.
    [Fact]
    public void OlderAgentFallsBackToWhatTheHostLastPushedIt()
    {
        var friend = FriendFingerprint(0x41);
        var state = FriendOnlyBind(friend);
        var stalePush = new FollowTemplateStore.PushRecord(
            state.FriendDigest,
            state.LeaderDigest,
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));

        Assert.Empty(FollowTemplateStore.PlanPushes(state, AdvertisedFollowTemplates.Unknown, stalePush));
        Assert.Equal(
            ["follow_set_template"],
            FollowTemplateStore.PlanPushes(state, AdvertisedFollowTemplates.Unknown, lastPushed: null)
                .Select(push => push.Command)
                .ToArray());
    }

    // Re-running the friend-row bind must not disturb a nametag rolodex this host has never
    // recorded. Without the per-half tracking the first friend bind after upgrading would have
    // published an authoritative empty leader list and wiped every agent's in-game binds.
    [Fact]
    public void FriendRebindLeavesAnUnrecordedNametagRolodexAlone()
    {
        var friend = FriendFingerprint(0x41);

        var pushes = FollowTemplateStore.PlanPushes(
            FriendOnlyBind(friend),
            Advertise(friend, [LeaderFingerprint(0x11), LeaderFingerprint(0x22)]),
            lastPushed: null);

        Assert.Empty(pushes);
    }

    // bind-in-game:0 empties the rolodex but keeps the friend bind. The empty list has to be
    // authoritative or the next sweep would hand the nametags straight back to every agent that
    // just cleared them - while the friend row, untouched, must not be disturbed.
    [Fact]
    public void ClearedNametagsAreAuthoritativeWithoutDisturbingTheFriendBind()
    {
        var friend = FriendFingerprint(0x41);
        var state = new FollowTemplateState(
            friend, [], "D2R_1", FriendRecorded: true, LeaderRecorded: true);

        var pushes = FollowTemplateStore.PlanPushes(
            state,
            Advertise(friend, [LeaderFingerprint(0x11)]),
            lastPushed: null);

        var push = Assert.Single(pushes);
        Assert.Equal("follow_clear_leader_template", push.Command);
    }

    [Fact]
    public void BindSurvivesAHostRestart()
    {
        using var fixture = new TemporaryDatabase();
        var friend = FriendFingerprint(0x41);
        var leader = LeaderFingerprint(0x11);

        var first = new AppDb(fixture.Config);
        Assert.False(first.GetFollowTemplates().Recorded);
        first.SaveFollowTemplates(FullBind(friend, leader) with { BoundAccountKey = "D2R_3" });

        var afterRestart = new AppDb(fixture.Config).GetFollowTemplates();

        Assert.True(afterRestart.Recorded);
        Assert.Equal(friend, afterRestart.FriendFingerprint);
        Assert.Equal([leader], afterRestart.LeaderFingerprints);
        Assert.Equal("D2R_3", afterRestart.BoundAccountKey);
    }

    // A cleared bind must persist as a recorded empty state, not decay back into "never bound" -
    // that distinction is the only thing telling the sweep whether to clear replicas or stay out
    // of the way.
    [Fact]
    public void ClearedBindPersistsAsRecordedRatherThanUnrecorded()
    {
        using var fixture = new TemporaryDatabase();
        var database = new AppDb(fixture.Config);
        database.SaveFollowTemplates(FullBind(FriendFingerprint(0x41)));

        database.SaveFollowTemplates(FullBind(null));
        var state = new AppDb(fixture.Config).GetFollowTemplates();

        Assert.True(state.FriendRecorded);
        Assert.True(state.LeaderRecorded);
        Assert.False(state.HasFriendTemplate);
        Assert.Equal(FollowTemplateDigest.None, state.FriendDigest);
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "d2r-follow-template-tests-" + Guid.NewGuid().ToString("N"));

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            Config = new HostConfig
            {
                DisableDiscord = true,
                DatabasePath = Path.Combine(_directory, "master.sqlite")
            };
        }

        public HostConfig Config { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
