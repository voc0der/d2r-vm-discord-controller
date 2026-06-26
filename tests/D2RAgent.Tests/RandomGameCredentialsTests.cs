using System.Text.RegularExpressions;
using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

// create-game-all with no name and no template falls back to these (issue #20, item 3) so the
// command always works instead of erroring. D2R caps game names/passwords at 15 characters, so
// the regression here is really "stay short and unambiguous," not "be exactly this shape."
public sealed class RandomGameCredentialsTests
{
    private static readonly Regex GameNamePattern = new("^d2r[a-z2-9]{6}$");
    private static readonly Regex PasswordPattern = new("^[a-z2-9]{6}$");
    private static readonly char[] AmbiguousCharacters = ['0', '1', 'i', 'l', 'o'];

    [Fact]
    public void NewGameNameHasExpectedShapeAndStaysWellUnderD2RsFifteenCharLimit()
    {
        for (var i = 0; i < 200; i++)
        {
            var name = RandomGameCredentials.NewGameName();
            Assert.Matches(GameNamePattern, name);
            Assert.True(name.Length <= 15, $"\"{name}\" exceeds D2R's 15-char game name limit.");
        }
    }

    [Fact]
    public void NewPasswordHasExpectedShapeAndStaysWellUnderD2RsFifteenCharLimit()
    {
        for (var i = 0; i < 200; i++)
        {
            var password = RandomGameCredentials.NewPassword();
            Assert.Matches(PasswordPattern, password);
            Assert.True(password.Length <= 15, $"\"{password}\" exceeds D2R's 15-char password limit.");
        }
    }

    [Fact]
    public void NeverContainsVisuallyAmbiguousCharacters()
    {
        for (var i = 0; i < 200; i++)
        {
            var randomPart = RandomGameCredentials.NewGameName()["d2r".Length..] + RandomGameCredentials.NewPassword();
            foreach (var ambiguous in AmbiguousCharacters)
            {
                Assert.DoesNotContain(ambiguous, randomPart);
            }
        }
    }
}
