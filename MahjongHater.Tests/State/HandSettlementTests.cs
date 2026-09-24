using System.Text.Json;
using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.State;

public class HandSettlementTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 13, 0, 0, DateTimeKind.Utc);

    private static AtkFrame Win(int tsumo = 0)
        => AtkFrame.OfInts(32, 0, 0, 0, 0, 0, 0, 0, tsumo).WithString(2, "South 2 North Wind");

    [Fact]
    public void Captured_wins_and_draws_wait_for_payment_and_attribute_the_correct_seat()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(StructFixture.FixturesDirectory,
            "emj_hand_settlements_20260923.json")));
        var hands = json.RootElement.GetProperty("hands").EnumerateArray().ToArray();
        Assert.Equal(6, hands.Length);
        foreach (var hand in hands)
        {
            var tracker = new EventTracker();
            foreach (var ev in hand.GetProperty("events").EnumerateArray())
            {
                var values = ev.GetProperty("values").EnumerateArray().ToArray();
                var frame = new AtkFrame(ev.GetProperty("count").GetInt32(),
                    values.Select(v => v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0).ToArray(),
                    values.Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : null).ToArray(),
                    values.Select(v => v.ValueKind == JsonValueKind.Number).ToArray());
                tracker.OnRefresh(frame, ev.GetProperty("utc").GetDateTime());
                if (frame.EventType != 29)
                {
                    Assert.Null(tracker.Settlement);
                    Assert.Equal(-1, tracker.LastWinnerSeat);
                    Assert.Equal(0, tracker.WinsThisSession);
                }
            }
            var settled = Assert.IsType<HandSettlement>(tracker.Settlement);
            Assert.True(settled.OutcomeKnown);
            Assert.Equal(hand.GetProperty("winner").GetInt32(), tracker.LastWinnerSeat);
            Assert.Equal(hand.GetProperty("victim").GetInt32(), tracker.RonVictimSeat);
            Assert.Equal(hand.GetProperty("delta").GetInt32(), tracker.LastScoreDelta);
            var row = HandResult.FromRoundEnd(StateSnapshot.Empty, settled.WinnerSeat, settled.WinByRon,
                settled.RonVictimSeat, null, settled.SeatDeltas[0], "npc", "V2", Now, settled.OutcomeKnown);
            Assert.Equal(hand.GetProperty("outcome").GetString(), row.Outcome);
            Assert.Equal(hand.GetProperty("delta").GetInt32(), row.Delta);
        }
    }

    [Theory]
    [InlineData(false, -39, 0, 39, 0, 2, 0, "dealin")]
    [InlineData(false, 20, 0, -20, 0, 0, 2, "win-ron")]
    [InlineData(true, 60, -20, -20, -20, 0, -1, "win-tsumo")]
    [InlineData(true, -20, 60, -20, -20, 1, -1, "tsumo-loss")]
    [InlineData(false, 0, 20, 0, -20, 1, 3, "other-ron")]
    public void Settlement_covers_every_winning_seat_and_payment(bool tsumo, int a, int b, int c, int d,
        int winner, int victim, string outcome)
    {
        var t = new EventTracker();
        t.OnRefresh(Win(tsumo ? 1 : 0), Now);
        t.OnRefresh(AtkFrame.OfInts(29, a, b, c, d), Now.AddSeconds(5));
        var s = Assert.IsType<HandSettlement>(t.Settlement);
        Assert.Equal(winner, s.WinnerSeat);
        Assert.Equal(victim, s.RonVictimSeat);
        Assert.Equal(outcome, HandResult.Classify(s.WinnerSeat, s.WinByRon, s.RonVictimSeat, null));
        Assert.Equal(a * 100, t.LastScoreDelta);
    }

    [Fact]
    public void Positive_draw_payment_is_not_a_win_even_with_one_positive_seat()
    {
        var t = new EventTracker();
        t.OnRefresh(AtkFrame.OfInts(31, 0, 0, 0, 1), Now);
        t.OnRefresh(AtkFrame.OfInts(29, 30, -10, -10, -10), Now);
        Assert.True(t.Settlement!.IsDraw);
        Assert.True(t.Settlement.OutcomeKnown);
        Assert.Equal(-1, t.LastWinnerSeat);
        Assert.Equal(0, t.WinsThisSession);
        Assert.Equal(3000, t.LastScoreDelta);
    }

    [Theory]
    [InlineData(20, 20, -40, 0)] // multiple winners are not mapped
    [InlineData(20, -10, -10, 0)] // ron with multiple payers is not mapped
    [InlineData(0, 0, 0, 0)]
    public void Ambiguous_win_payments_are_unknown(int a, int b, int c, int d)
    {
        var s = HandSettlement.Read(AtkFrame.OfInts(29, a, b, c, d), true, false)!;
        Assert.False(s.OutcomeKnown);
        var row = HandResult.FromRoundEnd(StateSnapshot.Empty, s.WinnerSeat, s.WinByRon,
            s.RonVictimSeat, null, s.SeatDeltas[0], "human", "V2", Now, s.OutcomeKnown);
        Assert.Equal("unknown", row.Outcome);
    }

    [Fact]
    public void Missing_or_unrecognized_announcement_does_not_guess_a_win_or_draw()
    {
        foreach (var announcement in new[] { AtkFrame.OfInts(32, 0), Win(2), AtkFrame.OfInts(31, 9, 0, 0, 1) })
        {
            var t = new EventTracker();
            t.OnRefresh(announcement, Now);
            t.OnRefresh(AtkFrame.OfInts(29, 20, -20, 0, 0), Now);
            Assert.False(t.Settlement!.OutcomeKnown);
            Assert.Equal(-1, t.LastWinnerSeat);
            Assert.Equal(0, t.WinsThisSession);
        }
    }

    [Fact]
    public void Truncated_untyped_or_overflowing_payments_do_not_finalize_a_hand()
    {
        var t = new EventTracker();
        t.OnRefresh(Win(), Now);
        foreach (var frame in new[] { AtkFrame.OfInts(29, 20, -20),
            AtkFrame.OfInts(29, 20, -20, 0, 0).WithString(4, "0"),
            AtkFrame.OfInts(29, int.MaxValue, int.MinValue, 0, 0) })
        {
            t.OnRefresh(frame, Now);
            Assert.Null(t.Settlement);
            Assert.Equal(0, t.WinsThisSession);
        }
        t.OnRefresh(AtkFrame.OfInts(29, 20, -20, 0, 0), Now);
        Assert.True(t.Settlement!.OutcomeKnown);
    }

    [Fact]
    public void Repeated_score_and_win_screens_do_not_double_count_or_clear_the_settlement()
    {
        var t = new EventTracker();
        t.OnRefresh(Win(), Now);
        var payment = AtkFrame.OfInts(29, 20, 0, -20, 0);
        t.OnRefresh(payment, Now);
        var settled = t.Settlement;
        t.OnRefresh(payment, Now);
        t.OnRefresh(Win(), Now);
        Assert.Same(settled, t.Settlement);
        Assert.Equal(0, t.LastWinnerSeat);
        Assert.Equal(2000, t.LastScoreDelta);
        Assert.Equal(1, t.WinsThisSession);
        Assert.Equal(0, t.LossesThisSession);
    }

    [Fact]
    public void New_hand_cannot_reuse_the_previous_winner_or_announcement()
    {
        var t = new EventTracker();
        t.OnRefresh(Win(), Now);
        t.OnRefresh(AtkFrame.OfInts(29, 20, 0, -20, 0), Now);
        t.OnRefresh(AtkFrame.OfInts(21, 13, 0), Now);
        Assert.Null(t.Settlement);
        Assert.Null(t.LastWinScreen);
        Assert.Null(t.LastRecap);
        Assert.Equal(-1, t.LastWinnerSeat);
        t.OnRefresh(AtkFrame.OfInts(29, 20, 0, -20, 0), Now);
        Assert.False(t.Settlement!.OutcomeKnown);
        Assert.Equal(1, t.WinsThisSession);
    }

    [Fact]
    public void Payment_overrules_an_inconsistent_last_discard_and_drops_its_tile()
    {
        var t = new EventTracker();
        t.OnRefresh(AtkFrame.OfInts(8, 0, 76041), Now);
        t.OnRefresh(Win(), Now);
        Assert.NotNull(t.RonTile);
        t.OnRefresh(AtkFrame.OfInts(29, 0, -39, 39, 0), Now);
        Assert.Equal(1, t.RonVictimSeat);
        Assert.Null(t.RonTile);
    }
}
