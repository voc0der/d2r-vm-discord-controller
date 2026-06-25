using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class MenuClickSafetyTests
{
    [Fact]
    public void InitialMenuPrepDoesNotEvaluateInGameSafetyGate()
    {
        var evaluated = false;

        var skip = VmOperations.ShouldSkipMenuClickForInGameSafety(
            guardAgainstInGame: false,
            mightAlreadyBeInGame: () =>
            {
                evaluated = true;
                return true;
            });

        Assert.False(skip);
        Assert.False(evaluated);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RecoveryMenuClicksHonorInGameSafetyGate(bool mightAlreadyBeInGame, bool expectedSkip)
    {
        var skip = VmOperations.ShouldSkipMenuClickForInGameSafety(
            guardAgainstInGame: true,
            mightAlreadyBeInGame: () => mightAlreadyBeInGame);

        Assert.Equal(expectedSkip, skip);
    }
}
