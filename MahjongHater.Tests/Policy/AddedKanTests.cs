using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.Policy;

// 2026-09-23, 12:09:07 → 12:09:17: we ponned 4z, drew the fourth 4z, the game offered the
// added kan (a self-declare "Kan" window) — and the plugin discarded that 4z instead. The
// option code does not say which kind of kan it is, and mapping it to AnKan alone made every
// added kan invisible: CallPolicy looks for four copies in the closed hand and finds three of
// them sitting in the meld.
public class AddedKanTests
{
    private static StateSnapshot PonnedThenDrewTheFourth(LegalAction legal)
    {
        var pon = new Meld(MeldType.Pon, TestTiles.Parse("444z").ToArray(), true);
        var seats = Enumerable.Range(0, 4).Select(i => SeatState.Empty(i) with { Score = 25000 }).ToArray();
        seats[0] = seats[0] with { Melds = [pon] };
        return StateSnapshot.Empty with
        {
            Sequence = 1,
            Phase = GamePhase.SelfDeclare,
            // 11 closed + the pon = 14: two complete runs, a pair, a partial, and the fourth
            // 4z sitting as a floater, so the added kan cannot cost shanten or waits.
            Hand = TestTiles.Parse("234m567m99p23s4z"),
            DrawnTile = Tile.Parse("4z"),
            OurMelds = [pon],
            Seats = seats,
            Legal = legal,
            CallOptions = ["Kan"],
            CallWindowConfirmed = true,
            SeatWind = Wind.East,
            RoundWind = Wind.East,
            WallRemaining = 40,
        };
    }

    // What the plugin used to be told: only a concealed kan is legal, which this hand cannot
    // make, so no kan option exists at all and the turn becomes a plain discard.
    [Fact]
    public void An_ankan_only_offer_hides_the_added_kan()
    {
        var state = PonnedThenDrewTheFourth(LegalAction.AnKan | LegalAction.Discard);
        var options = new CallPolicy().Evaluate(state, new OpponentModel(), CancellationToken.None);
        Assert.False(options.Accept);
    }

    // What the game actually means by "Kan" on our own turn: whichever kan this hand supports.
    [Fact]
    public void Marking_both_kinds_legal_surfaces_the_added_kan()
    {
        var state = PonnedThenDrewTheFourth(LegalAction.AnKan | LegalAction.ShouMinKan | LegalAction.Discard);
        var options = new CallPolicy().Evaluate(state, new OpponentModel(), CancellationToken.None);
        Assert.True(options.Accept);
        Assert.Equal(ActionKind.ShouMinKan, options.Kind);
        Assert.Equal(Tile.Parse("4z"), options.Meld!.Tiles[0]);
    }
}
