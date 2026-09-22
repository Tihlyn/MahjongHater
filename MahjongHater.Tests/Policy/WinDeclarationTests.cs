using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.Policy;

// 2026-09-22, 16:19:42 → 16:20:40 in dalamud.old.log: we declared riichi on a 6m/9m wait,
// ankan'd 3z, and then answered the game's own Ron window on 6m with Pass
// ("No discard is currently legal."). The hand ran out as an exhaustive draw. The call
// window was read correctly, so the decline came from the win evaluation scoring our
// (drifted) hand as no win. The game only offers a win it will pay out, so the offer is
// authoritative: see docs/research/WIN_OFFERS_2026_09_22.md.
public class WinDeclarationTests
{
    private static SeatState[] Seats() =>
        [.. Enumerable.Range(0, 4).Select(i => SeatState.Empty(i) with { Score = 25000 })];

    private static StateSnapshot RonOffer(string closed, params Meld[] melds)
    {
        var seats = Seats();
        seats[0] = seats[0] with { Riichi = true, RiichiDiscardIndex = 0, Discards = TestTiles.Parse("7m"), Melds = melds };
        return StateSnapshot.Empty with
        {
            Sequence = 1,
            Phase = GamePhase.CallPrompt,
            Hand = TestTiles.Parse(closed),
            OurMelds = melds,
            Seats = seats,
            OurRiichi = true,
            Legal = LegalAction.Ron | LegalAction.Pass,
            CallTile = Tile.Parse("6m"),
            CallFromSeat = 3,
            CallOptions = ["Ron"],
            CallWindowConfirmed = true,
            SeatWind = Wind.East,
            RoundWind = Wind.East,
            WallRemaining = 15,
        };
    }

    private static Meld Ankan(string tile) => new(MeldType.Ankan, TestTiles.Parse(new string(tile[0], 4) + tile[1]).ToArray(), false);

    [Fact]
    public void Declares_ron_on_a_riichi_wait()
    {
        var choice = new DecisionPolicy().Choose(RonOffer("78m345s55p222z333z"), CancellationToken.None);
        Assert.Equal(ActionKind.Ron, choice.Kind);
        Assert.Equal(Tile.Parse("6m"), choice.Tile);
        Assert.DoesNotContain(choice.Steps, s => s.Stage == DecisionPolicy.WinReadDisagrees);
    }

    [Fact]
    public void Declares_ron_after_a_concealed_kan()
    {
        var choice = new DecisionPolicy().Choose(RonOffer("78m345s55p222z", Ankan("3z")), CancellationToken.None);
        Assert.Equal(ActionKind.Ron, choice.Kind);
        Assert.DoesNotContain(choice.Steps, s => s.Stage == DecisionPolicy.WinReadDisagrees);
    }

    // The live failure: the hand we hold cannot be made into a winning hand with the offered
    // tile (here it is short, exactly as a post-kan read that kept the kan tiles would be).
    // The game offered the win, so it is taken - and the disagreement is recorded, because
    // the hand read is what actually needs repairing.
    [Fact]
    public void Takes_an_offered_ron_our_own_read_cannot_explain_and_flags_it()
    {
        var choice = new DecisionPolicy().Choose(RonOffer("19m19p19s1234z"), CancellationToken.None);
        Assert.Equal(ActionKind.Ron, choice.Kind);
        var flagged = Assert.Single(choice.Steps, s => s.Stage == DecisionPolicy.WinReadDisagrees);
        Assert.Contains("the game offered it", flagged.Display);
        Assert.Contains("riichi=True", flagged.Display);
    }

    [Fact]
    public void Takes_an_offered_tsumo_our_own_read_cannot_explain()
    {
        var state = RonOffer("19m19p19s1234z") with
        {
            Phase = GamePhase.SelfDeclare,
            Legal = LegalAction.Tsumo | LegalAction.Discard,
            CallTile = null,
            CallOptions = ["Tsumo"],
            DrawnTile = Tile.Parse("4z"),
        };
        var choice = new DecisionPolicy().Choose(state, CancellationToken.None);
        Assert.Equal(ActionKind.Tsumo, choice.Kind);
        Assert.Contains(choice.Steps, s => s.Stage == DecisionPolicy.WinReadDisagrees);
    }
}
