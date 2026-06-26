namespace AgentCommon;

// Backs /d2r template (issue #20, item 5): lets create-game-all/join-all mint predictable
// numbered game names (netrunner1, netrunner2, ...) instead of requiring name/password on every
// call. The minted count is in-memory only and resets when the host restarts, matching the
// issue's own "Reset on app quit" spec for the counter.
public sealed class GameNameTemplate
{
    private int _mintedCount;

    public GameNameTemplate(string name, string? password)
    {
        Name = name;
        Password = password;
    }

    public string Name { get; }
    public string? Password { get; }

    // create-game-all: always advances to a brand new number - every no-flag call makes a
    // distinct new game.
    public (string Name, string? Password) MintNext()
    {
        var number = Interlocked.Increment(ref _mintedCount);
        return ($"{Name}{number}", Password);
    }

    // join-all: join whatever create-game-all most recently minted, or netrunner1 if nothing
    // has been minted yet this session ("starting at netrunner1" per the issue) - never advances
    // the counter itself, since joining doesn't create a new game.
    public (string Name, string? Password) Current()
    {
        var number = Math.Max(1, Volatile.Read(ref _mintedCount));
        return ($"{Name}{number}", Password);
    }
}
