using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

public sealed class PushFoldPolicy : IPushFoldPolicy
{
    private readonly PolicyWeights weights;

    public PushFoldPolicy(PolicyWeights? weights = null)
    {
        this.weights = weights ?? PolicyWeights.Default;
    }

    // A declared riichi is a hard threat; the model's tenpai estimate is soft (it climbs
    // with every discard, so late in a hand it exceeds the threshold for everyone) and
    // only decides for hands that are still far from tenpai. Live 2026-09-19: the soft
    // estimate alone folded a tenpai into 1-shanten with nobody in riichi.
    public PushFoldStance Evaluate(StateSnapshot state, IOpponentModel opponents, DiscardCandidate best, out Reason reason)
    {
        var threat = Enumerable.Range(1, 3).Max(opponents.TenpaiProbability);
        var riichi = state.Seats.Any(s => s.Seat != 0 && s.Riichi);
        var cheap = best.Value < this.weights.FoldMinValue;
        var fold = best.ShantenAfter switch
        {
            0 => riichi && cheap && best.Ukeire < 3,
            1 => riichi && cheap,
            _ => (riichi || threat >= this.weights.FoldTenpaiThreshold) && best.ShantenAfter >= this.weights.FoldMinShanten,
        };
        var threatText = riichi ? "opponent riichi" : $"opponent tenpai {threat:P0}";
        reason = new Reason("push/fold", fold
            ? $"Fold: {threatText}, {best.ShantenAfter}-shanten, value {best.Value:0.#}; choose lowest deal-in risk."
            : $"Push: {best.ShantenAfter}-shanten, value {best.Value:0.#}, {threatText}.");
        return fold ? PushFoldStance.Fold : PushFoldStance.Push;
    }
}
