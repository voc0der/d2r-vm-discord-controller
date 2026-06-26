using AgentCommon;
using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// QuitIfCharacterScreenIdleAsync used to trust GetActivitySnapshot() (only updated by an
// automated command explicitly calling MarkLobbyOrGameInteraction/MarkCharacterScreenIdle) right
// up to the Alt+F4 decision. A join-all attempt that fails its own entry check and returns
// without marking, then the client recovers or gets joined some other way, left that cache
// pointing at a stale CharacterScreenIdle with the original timestamp forever - the exact "killed
// while actually in a game" gap from issue #20, item 1. The fix takes one live look via the same
// DetectVisibleActivitySnapshot /d2r status already trusts over this cache, and resyncs through
// ReconcileActivityFromLiveSnapshot on a mismatch. This pins that resync is a faithful, complete
// overwrite - the part of the fix that doesn't require the real Win32 screen classifier to verify.
public sealed class ActivityReconciliationTests
{
    private static VmOperations NewVmOperations() => new(new VmAgentConfig());

    [Fact]
    public void ReconcileOverwritesCachedSnapshotWithEveryFieldFromTheLiveOne()
    {
        var vm = NewVmOperations();
        var live = new VmOperations.ActivitySnapshot(
            VmOperations.D2RActivityState.LobbyOrGame,
            CharacterScreenIdleSinceUtc: null,
            LastLobbyOrGameInteractionUtc: DateTimeOffset.UtcNow,
            Reason: "Detected lobby or in-game UI.");

        vm.ReconcileActivityFromLiveSnapshot(live);

        Assert.Equal(live, vm.GetActivitySnapshot());
    }

    [Fact]
    public void ReconcilingToCharacterScreenIdleClearsAnyPriorLobbyInteractionTimestamp()
    {
        var vm = NewVmOperations();
        vm.ReconcileActivityFromLiveSnapshot(new VmOperations.ActivitySnapshot(
            VmOperations.D2RActivityState.LobbyOrGame,
            CharacterScreenIdleSinceUtc: null,
            LastLobbyOrGameInteractionUtc: DateTimeOffset.UtcNow,
            Reason: "Detected lobby or in-game UI."));

        var idleSince = DateTimeOffset.UtcNow.AddMinutes(-5);
        vm.ReconcileActivityFromLiveSnapshot(new VmOperations.ActivitySnapshot(
            VmOperations.D2RActivityState.CharacterScreenIdle,
            CharacterScreenIdleSinceUtc: idleSince,
            LastLobbyOrGameInteractionUtc: null,
            Reason: "Detected character screen."));

        var snapshot = vm.GetActivitySnapshot();
        Assert.Equal(VmOperations.D2RActivityState.CharacterScreenIdle, snapshot.State);
        Assert.Equal(idleSince, snapshot.CharacterScreenIdleSinceUtc);
        Assert.Null(snapshot.LastLobbyOrGameInteractionUtc);
    }
}
