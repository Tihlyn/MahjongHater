using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using MahjongHater.Core.Simulation;

namespace MahjongHater.Core.Learning;

// Expert imitation as the attack ordering only: candidates keep every analyzer metric the
// budget, betaori and riichi layers read (shanten, ukeire, value points, danger), but inside
// the list the network's discard probability decides, not the analyzer score. When the
// position is outside the network's action space (open hand, claim, riichi lock…) the
// heuristic ordering is used unchanged. DecisionPolicy then applies the danger budget on
// top, so defense stays measured while tile efficiency follows the Phoenix players.
public sealed class LearnedDiscardPolicy(LearnedModel network, PolicyWeights? weights = null) : IDiscardPolicy
{
    private readonly HeuristicDiscardPolicy heuristic = new(weights: weights);

    public IReadOnlyList<DiscardCandidate> Rank(StateSnapshot state, IOpponentModel opponents, CancellationToken ct)
    {
        var candidates = this.heuristic.Rank(state, opponents, ct);
        if (candidates.Count < 2 || !network.Supports(state) || LearnedPolicy.Legal(state).Length == 0)
            return candidates;
        ct.ThrowIfCancellationRequested();
        var prediction = network.Predict(state, ct);
        double Logit(DiscardCandidate c)
        {
            var index = LearningFeatures.ActionIndex(SimAction.Make(SimActionKind.Discard, c.Tile));
            return index >= 0 ? prediction[index] : double.NegativeInfinity;
        }

        var maximum = candidates.Max(Logit);
        var denominator = candidates.Sum(c => Math.Exp(Logit(c) - maximum));
        return candidates
            .Select(c => c with { Score = Math.Exp(Logit(c) - maximum) / denominator, Note = $"{c.Note}; imitation {Math.Exp(Logit(c) - maximum) / denominator:P0}" })
            .OrderByDescending(c => c.Score).ThenBy(c => c.ShantenAfter).ThenBy(c => c.Tile)
            .ToArray();
    }
}
