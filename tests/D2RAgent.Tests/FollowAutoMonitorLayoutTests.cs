using Discord;
using Xunit;

namespace D2RAgent.Tests;

// Private mode's monitor carries six buttons - Stop, -1, +1 VM, the join-delay toggle, the party
// mode toggle and Park - and Discord allows only five per action row.
//
// This pins the library behaviour the monitor depends on: ComponentBuilder must overflow the sixth
// button into a second row rather than throwing. A throw here would not cost one button, it would
// fail the whole monitor render - the buttons and the status text arrive as one message edit - so
// the control everyone uses to stop the run would disappear along with the one that did not fit.
public sealed class FollowAutoMonitorLayoutTests
{
    [Fact]
    public void TheFullPrivateModeButtonSetOverflowsIntoASecondRow()
    {
        var builder = new ComponentBuilder();
        builder.WithButton("Stop", "d2r:follow:auto-stop", ButtonStyle.Danger);
        builder.WithButton("-1", "d2r:follow:bots:remove", ButtonStyle.Secondary);
        builder.WithButton("+1 VM", "d2r:follow:bots:add", ButtonStyle.Success);
        builder.WithButton("+30s Delay", "d2r:follow:join-delay", ButtonStyle.Success);
        builder.WithButton("Public", "d2r:follow:party-mode", ButtonStyle.Success);
        builder.WithButton("Park", "d2r:follow:park", ButtonStyle.Primary);

        var built = builder.Build();
        var rows = built.Components.Cast<ActionRowComponent>().ToArray();

        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.True(row.Components.Count <= 5));
        Assert.Equal(6, rows.Sum(row => row.Components.Count));
    }
}
