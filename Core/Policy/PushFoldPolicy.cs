using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

public sealed class PushFoldPolicy : IPushFoldPolicy
{
    private readonly PolicyWeights weights;

    public PushFoldPolicy(PolicyWeights? weights = null)
    {
        this.weights = weights ?? PolicyWeights.Default;
    }

    public PushFoldStance Evaluate(StateSnapshot state, IOpponentModel opponents, DiscardCandidate best, out Reason reason)
    {
        var threat = Enumerable.Range(1, 3).Max(opponents.TenpaiProbability);
        var fold = threat >= this.weights.FoldTenpaiThreshold
            && (best.ShantenAfter >= this.weights.FoldMinShanten || best.Value < this.weights.FoldMinValue);
        reason = new Reason("push/fold", fold
            ? $"Fold: opponent tenpai {threat:P0}, {best.ShantenAfter}-shanten, value {best.Value:0.#}; choose lowest deal-in risk."
            : $"Push: {best.ShantenAfter}-shanten, value {best.Value:0.#}, opponent tenpai {threat:P0}.");
        return fold ? PushFoldStance.Fold : PushFoldStance.Push;
    }
}
