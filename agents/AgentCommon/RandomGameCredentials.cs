using System.Security.Cryptography;

namespace AgentCommon;

// Floor for /d2r create-game-all with no name and no template configured (issue #20, item 3):
// always produces something joinable rather than erroring. Classic-engine D2R game names and
// passwords are capped at 15 characters, so these stay short enough to never bump into that
// regardless of what's prepended/appended by a caller.
public static class RandomGameCredentials
{
    // Excludes 0/1/i/l/o so a name read off a screenshot isn't ambiguous.
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    // /d2r dclone opens one game per bot as fast as the fleet can create them, and every name is
    // posted for other players to type in by hand. Seven characters off this alphabet is ~27
    // billion names - wide enough that neither a parallel park nor somebody else's game on the
    // same realm is likely to take the name first (a collision costs a whole create/retry round
    // trip, which is the expensive part of parking) - and still short enough to read off Discord.
    public const int DcloneGameNameLength = 7;

    // One character. The password is only there to keep a random passer-by from wandering into
    // the game before the operator's group does; anyone being handed the name is being handed
    // this with it.
    public const int DclonePasswordLength = 1;

    public static string NewGameName() => "d2r" + RandomString(6);

    public static string NewPassword() => RandomString(6);

    public static string NewDcloneGameName() => RandomString(DcloneGameNameLength);

    public static string NewDclonePassword() => RandomString(DclonePasswordLength);

    private static string RandomString(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }
}
