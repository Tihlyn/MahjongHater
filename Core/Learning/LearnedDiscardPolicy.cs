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
        var legal = LearnedPolicy.Legal(state);
        var riichiLegal = legal.Where(a => a.Kind == SimActionKind.Riichi).Select(a => a.Tile).ToHashSet(StringComparer.Ordinal);
        // The network scores "discard t" and "riichi with t" separately; the tile's weight is
        // the sum of both (the riichi layer decides the declaration afterwards).
        var maximum = prediction.Take(network.Actions).Max();
        double Weight(DiscardCandidate c)
        {
            var discard = network.ActionIndex(SimAction.Make(SimActionKind.Discard, c.Tile));
            if (discard < 0) return 0;
            var weight = Math.Exp(prediction[discard] - maximum);
            if (riichiLegal.Contains(c.Tile.ToString()))
                weight += Math.Exp(prediction[discard + 37] - maximum);
            return weight;
        }

        var denominator = candidates.Sum(Weight);
        if (denominator <= 0) return candidates;
        return candidates
            .Select(c => c with { Score = Weight(c) / denominator, Note = $"{c.Note}; imitation {Weight(c) / denominator:P0}" })
            .OrderByDescending(c => c.Score).ThenBy(c => c.ShantenAfter).ThenBy(c => c.Tile)
            .ToArray();
    }
}
