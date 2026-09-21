using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

public sealed class RiichiPolicy : IRiichiPolicy
{
    private readonly PolicyWeights weights;

    public RiichiPolicy(PolicyWeights? weights = null)
    {
        this.weights = weights ?? PolicyWeights.Default;
    }

    public bool ShouldDeclare(StateSnapshot state, IOpponentModel opponents, DiscardCandidate best, out Reason reason)
    {
        var riichiSeats = state.Seats.Where(s => s.Seat != 0 && s.Riichi).ToList();
        var v2 = this.weights.DefenseModel == DefenseModel.V2;
        var chaseNeeds = riichiSeats.Any(s => s.Seat == state.DealerSeat) ? this.weights.ChaseBadWaitPointsVsDealer : this.weights.ChaseBadWaitPoints;
        var why = state.OurRiichi ? "Already in riichi."
            : state.IsOpen ? "Riichi requires a closed hand."
            : best.ShantenAfter != 0 ? "Hand is not tenpai."
            : state.WallRemaining < this.weights.RiichiMinWall ? "Too few tiles remain in the wall."
            : !v2 && riichiSeats.Count > 0 && best.Ukeire < 4 ? "Bad wait against an opponent's riichi."
            : v2 && riichiSeats.Count > 0 && best.Ukeire < this.weights.GoodWaitTiles && best.ValuePoints < chaseNeeds
                ? $"Bad wait against a riichi and only {best.ValuePoints:0} points: stay dama (chase needs {chaseNeeds:0})."
            : best.Ukeire == 0 ? "Wait is dead (no live winning tiles)."
            : best.Ukeire < this.weights.RiichiMinUkeire ? "Too few live winning tiles."
            : null;
        reason = new Reason("riichi", why ?? (riichiSeats.Count > 0 && best.Ukeire >= this.weights.GoodWaitTiles
            ? $"Chase the riichi: good wait with {best.Ukeire} live winning tiles."
            : $"Declare riichi with {best.Ukeire} live winning tiles."));
        return why is null;
    }
}
