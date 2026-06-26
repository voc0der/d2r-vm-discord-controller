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

    public static string NewGameName() => "d2r" + RandomString(6);

    public static string NewPassword() => RandomString(6);

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
