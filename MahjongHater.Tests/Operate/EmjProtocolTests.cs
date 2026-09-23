using MahjongHater.Core.Operate;
using Xunit;

namespace MahjongHater.Tests.Operate;

// The protocol is data read off the game's own traffic (docs/research/ADDON_PROTOCOL_2026_09_23.md).
// These tests pin the measured values so a later "tidy-up" cannot quietly reintroduce a guess,
// and enforce the one rule that makes the map safe to act on: notifications are not commands.
public class EmjProtocolTests
{
    [Fact]
    public void Commands_carry_the_heads_the_capture_recorded()
    {
        Assert.Equal([7, 5], EmjProtocol.DiscardSlot(5));
        Assert.Equal([11, 1], EmjProtocol.SelectCallRow(1));
        Assert.Equal([14], EmjProtocol.AdvanceRecap());
        Assert.Equal([12, 0], EmjProtocol.PickChiShape(0));
        Assert.Equal([12, 1], EmjProtocol.PickChiShape(1));
        Assert.Equal([15, 76057], EmjProtocol.PointAtTile(76057));
    }

    [Fact]
    public void The_draw_slot_is_addressable_and_nothing_beyond_it_is()
    {
        Assert.Equal([7, 13], EmjProtocol.DiscardSlot(EmjProtocol.MaxSlot));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmjProtocol.DiscardSlot(14));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmjProtocol.DiscardSlot(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmjProtocol.SelectCallRow(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmjProtocol.PointAtTile(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmjProtocol.PickChiShape(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmjProtocol.PickChiShape(4));
    }

    // The addon fires these itself on state transitions. Replaying one as input is how another
    // plugin reportedly parked the addon in state 32, so they are named to stay un-replayable.
    [Theory]
    [InlineData(EmjProtocol.HandStarted)]
    [InlineData(EmjProtocol.HandEnded)]
    [InlineData(EmjProtocol.Dismissed)]
    [InlineData(EmjProtocol.Closed)]
    public void Notifications_are_recognised(int head) => Assert.True(EmjProtocol.IsNotification(head));

    [Theory]
    [InlineData(EmjProtocol.Discard)]
    [InlineData(EmjProtocol.CallRow)]
    [InlineData(EmjProtocol.RecapNext)]
    [InlineData(EmjProtocol.Pointer)]
    [InlineData(EmjProtocol.ChiShape)]
    public void Commands_are_not_notifications(int head) => Assert.False(EmjProtocol.IsNotification(head));

    // The chooser's buttons: shape i is node 5+i with ButtonClick param 9+i, cancel is node 11
    // with param 8 - the layout paths the plugin already carried, now confirmed against a live
    // chooser rather than assumed.
    [Fact]
    public void The_chooser_buttons_are_where_the_layout_says()
    {
        Assert.Equal("1/46/52", EmjProtocol.ChooserPath);
        Assert.Equal(9, EmjProtocol.ChiShapeFirstParam);
        Assert.Equal(8, EmjProtocol.ChiCancelParam);
    }

    // Ending a match lives in a different addon than the plugin used to search.
    [Fact]
    public void The_match_ending_control_is_the_result_addon_not_a_label_in_Emj()
    {
        Assert.Equal("EmjTotalResult", EmjProtocol.ResultAddon);
        Assert.Equal("EmjRankResult", EmjProtocol.RankResultAddon);
        Assert.Equal(26u, EmjProtocol.ResultCloseNodeId);
        Assert.Equal(0, EmjProtocol.ResultCloseParam);
    }
}
