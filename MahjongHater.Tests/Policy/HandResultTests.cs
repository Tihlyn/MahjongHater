using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.Policy;

// One row per finished hand is what the live A/B reads (tools/candidate_bars.py bar3), so the
// mapping from "last in-play snapshot + how the hand ended" to that row is covered here. The
// reader used to build it from a field it had already cleared, which threw on every recap
// screen and left hand_results.csv empty.
public class HandResultTests
{
    private static readonly DateTime T0 = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private static StateSnapshot LastInPlay()
    {
        var seats = StateSnapshot.Empty.Seats.ToList();
        seats[0] = seats[0] with { Discards = TestTiles.Parse("1z2z3z4z"), DiscardCount = 6 };
        seats[1] = seats[1] with { Riichi = true, RiichiDiscardIndex = 0, Discards = TestTiles.Parse("5m") };
        seats[3] = seats[3] with { Riichi = true, RiichiDiscardIndex = 0, Discards = TestTiles.Parse("9p") };
        return StateSnapshot.Empty with
        {
            Phase = GamePhase.OurTurn, Hand = TestTiles.Parse("123m456p789s1122z"), Seats = seats,
            RoundWind = Wind.South, HandNumber = 3, OurRiichi = true,
        };
    }

    [Fact]
    public void Round_end_row_carries_the_snapshot_fields_and_the_deciding_policy()
    {
        var row = HandResult.FromRoundEnd(LastInPlay(), winnerSeat: 2, winByRon: true, ronVictimSeat: 0,
            ourTenpaiAtDraw: null, scoreDelta: -8000, population: "human", policy: "learned-guarded", utc: T0);
        Assert.Equal("dealin", row.Outcome);
        Assert.Equal("South", row.Round);
        Assert.Equal(3, row.HandNumber);
        Assert.Equal(6, row.OurDiscards);              // the struct's count wins over the parsed river
        Assert.True(row.OurRiichi);
        Assert.Equal(2, row.RiichiSeats);              // opponents only
        Assert.Equal(-8000, row.Delta);
        Assert.Equal("learned-guarded", row.Model);
        Assert.Equal("human", row.Population);
        Assert.Equal(HandResult.CsvHeader.Split(',').Length, row.ToCsv().Split(',').Length);
        Assert.Contains("learned-guarded", row.ToCsv());
    }

    [Theory]
    [InlineData(0, true, 0, null, "win-ron")]
    [InlineData(0, false, -1, null, "win-tsumo")]
    [InlineData(2, true, 0, null, "dealin")]
    [InlineData(2, true, 1, null, "other-ron")]
    [InlineData(2, false, -1, null, "tsumo-loss")]
    [InlineData(-1, false, -1, true, "draw-tenpai")]
    [InlineData(-1, false, -1, false, "draw-noten")]
    [InlineData(-1, false, -1, null, "draw")]
    public void Outcomes_cover_every_way_a_hand_ends(int winner, bool byRon, int victim, bool? tenpaiAtDraw, string expected) =>
        Assert.Equal(expected, HandResult.FromRoundEnd(LastInPlay(), winner, byRon, victim, tenpaiAtDraw, 0, "human", "V2", T0).Outcome);
}
