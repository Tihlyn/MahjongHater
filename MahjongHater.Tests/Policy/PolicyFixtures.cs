using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;

namespace MahjongHater.Tests.Policy;

internal static class PolicyFixtures
{
    public static StateSnapshot State(string tiles = "123m456m4578p447s1z", LegalAction legal = LegalAction.Discard)
    {
        var hand = TestTiles.Parse(tiles);
        return StateSnapshot.Empty with
        {
            Sequence = 1,
            Phase = GamePhase.OurTurn,
            Hand = hand,
            DrawnTile = hand.Count > 0 ? hand[^1] : null,
            Legal = legal,
            SeatWind = Wind.South,
        };
    }

    public static StateSnapshot Seat(StateSnapshot state, int seat = 1, string discards = "", bool riichi = false,
        int riichiIndex = -1, params Meld[] melds)
    {
        var seats = state.Seats.ToArray();
        seats[seat] = new SeatState(seat, TestTiles.Parse(discards), melds, riichi, riichiIndex, 25000);
        return state with { Seats = seats };
    }

    public static DiscardCandidate Candidate(string tile = "1z", int shanten = 1, int ukeire = 8,
        double value = 3, double risk = 0, double score = 100) =>
        new(Tile.Parse(tile), shanten, ukeire, 0, value, risk, score, "test candidate");

    public static OpponentModel Model(StateSnapshot state, PolicyWeights? weights = null)
    {
        var model = new OpponentModel(weights);
        model.Update(state);
        return model;
    }

    public sealed class FixedDiscards(params DiscardCandidate[] candidates) : IDiscardPolicy
    {
        public IReadOnlyList<DiscardCandidate> Rank(StateSnapshot state, IOpponentModel opponents, CancellationToken ct) => candidates;
    }

    public sealed class FixedCalls(bool accept) : ICallPolicy
    {
        public CallDecision Evaluate(StateSnapshot state, IOpponentModel opponents, CancellationToken ct) => accept
            ? new CallDecision(true, ActionKind.Pon, Meld.MakePon(Tile.Parse("5z"), true), new Reason("call", "Accept test call."))
            : CallDecision.Decline("Decline test call.");
    }
}
