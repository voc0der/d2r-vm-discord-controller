using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

/// <summary>
/// Discord reports "The application did not respond" when nothing acknowledges an interaction
/// within three seconds, and Discord.NET runs handlers inline on the gateway task - so one handler
/// that works before it answers fails its own command and everything queued behind it. Every
/// handler therefore acknowledges first, through a helper whose two rules are easy to undo by
/// accident.
/// </summary>
public sealed class InteractionAcknowledgementTests
{
    [Fact]
    public void SlashCommandDefersAsItsOwnEphemeralReply()
    {
        Assert.Equal(
            InteractionAcknowledgement.DeferAsNewEphemeralReply,
            DiscordBot.ChooseAcknowledgement(hasResponded: false, isComponent: false));
    }

    /// <summary>
    /// A button must not take the plain DeferAsync path. That acknowledges as
    /// DeferredUpdateMessage, which makes the clicked message the interaction's original response,
    /// so the handler's reply would overwrite the follow-auto monitor or the quick-action prompt
    /// the button is attached to.
    /// </summary>
    [Fact]
    public void ButtonDefersWithoutClaimingTheMessageItWasClickedOn()
    {
        Assert.Equal(
            InteractionAcknowledgement.DeferComponentWithoutClaimingItsMessage,
            DiscordBot.ChooseAcknowledgement(hasResponded: false, isComponent: true));
    }

    /// <summary>
    /// Idempotence is what lets a handler acknowledge before its own pre-flight work and still
    /// hand off to a runner that would otherwise defer - acknowledging twice throws. It also
    /// preserves the choice of a handler that deliberately deferred as an update because it means
    /// to replace its own message.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnAlreadyAcknowledgedInteractionIsLeftAlone(bool isComponent)
    {
        Assert.Equal(
            InteractionAcknowledgement.None,
            DiscordBot.ChooseAcknowledgement(hasResponded: true, isComponent));
    }
}
