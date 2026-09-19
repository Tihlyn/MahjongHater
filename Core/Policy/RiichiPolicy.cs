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
        var why = state.OurRiichi ? "Already in riichi."
            : state.IsOpen ? "Riichi requires a closed hand."
            : best.ShantenAfter != 0 ? "Hand is not tenpai."
            : state.WallRemaining < this.weights.RiichiMinWall ? "Too few tiles remain in the wall."
            : state.Seats.Any(s => s.Seat != 0 && s.Riichi) && best.Ukeire < 4 ? "Bad wait against an opponent's riichi."
            : best.Ukeire == 0 ? "Wait is dead (no live winning tiles)."
            : best.Ukeire < this.weights.RiichiMinUkeire ? "Too few live winning tiles."
            : null;
        reason = new Reason("riichi", why ?? $"Declare riichi with {best.Ukeire} live winning tiles.");
        return why is null;
    }
}
