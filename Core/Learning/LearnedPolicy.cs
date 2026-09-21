using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using MahjongHater.Core.Simulation;

namespace MahjongHater.Core.Learning;

// Expert imitation is a distribution over legal discard/riichi actions, not EV.
// Unsupported live action spaces go through the complete existing policy.
public sealed class LearnedPolicy(IPolicy fallback, LearnedModel network, PolicyWeights? weights = null) : IPolicy
{
    public ActionChoice Choose(StateSnapshot state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var actions = Legal(state);
        if (!network.Supports(state) || actions.Length == 0) return fallback.Choose(state, ct);
        var prediction = network.Predict(state, ct);
        var maximum = actions.Max(a => prediction[LearningFeatures.ActionIndex(a)]);
        var denominator = actions.Sum(a => Math.Exp(prediction[LearningFeatures.ActionIndex(a)] - maximum));
        var ordered = actions.OrderByDescending(a => prediction[LearningFeatures.ActionIndex(a)]).ThenBy(a => a.Key, StringComparer.Ordinal).ToArray();
        var best = ordered[0];
        var probability = Math.Exp(prediction[LearningFeatures.ActionIndex(best)] - maximum) / denominator;
        var opponents = new LearnedOpponentModel(network, weights);
        opponents.Update(state);
        var candidates = new HeuristicDiscardPolicy(weights: weights).Rank(state, opponents, ct);
        var tile = Tile.Parse(best.Tile!);
        candidates = candidates.OrderBy(c => c.Tile == tile ? 0 : 1).ToArray();
        return new ActionChoice(best.Kind == SimActionKind.Riichi ? ActionKind.Riichi : ActionKind.Discard, tile, null,
            $"{best.Kind} {tile}: learned policy ({probability:P0} among legal actions).",
            [new Reason("learned", "Expert imitation; probability is not expected value or a guarantee of correctness."),
             new Reason("opponents", string.Join("; ", Enumerable.Range(1, 3).Select(s => $"seat {s}: tenpai {opponents.TenpaiProbability(s):P0}, ~{opponents.Value(s):0} pts")))], candidates);
    }

    public static SimAction[] Legal(StateSnapshot state)
    {
        // Closed draw decisions only: the live reader does not yet expose complete
        // kuikae/per-tile call restrictions. Winning and kan prompts use fallback.
        if (!state.LayoutHealthy || state.Seats.Count != 4 || state.Seats.Any(s => !s.DiscardsVerified)
            || state.Phase is not (GamePhase.OurTurn or GamePhase.SelfDeclare) || !state.Can(LegalAction.Discard)
            || (state.Legal & ~(LegalAction.Discard | LegalAction.Riichi | LegalAction.Pass)) != 0
            || state.OurMelds.Count != 0 || state.OurRiichi || state.Us.Riichi || state.Hand.Count != 14
            || state.DrawnTile is not { } draw || !state.Hand.Contains(draw) || state.CallTile is not null || state.CallShapes.Count != 0)
            return [];
        var result = new List<SimAction>();
        foreach (var tile in state.Hand.Distinct().Order())
        {
            result.Add(SimAction.Make(SimActionKind.Discard, tile));
            if (state.Can(LegalAction.Riichi) && state.Us.Score >= 1000 && state.WallRemaining >= 4)
            {
                var kept = state.Hand.ToList();
                kept.Remove(tile);
                if (SimTiles.Waits(kept, []) != 0) result.Add(SimAction.Make(SimActionKind.Riichi, tile));
            }
        }
        return result.ToArray();
    }
}
