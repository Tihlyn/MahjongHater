using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

public sealed class HeuristicDiscardPolicy : IDiscardPolicy
{
    private readonly HandAnalyzer analyzer;
    private readonly PolicyWeights weights;

    public HeuristicDiscardPolicy(HandAnalyzer? analyzer = null, PolicyWeights? weights = null)
    {
        this.analyzer = analyzer ?? new HandAnalyzer();
        this.weights = weights ?? PolicyWeights.Default;
    }

    public IReadOnlyList<DiscardCandidate> Rank(StateSnapshot state, IOpponentModel opponents, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (state.OurRiichi && (!state.DrawnTile.HasValue || !state.Hand.Contains(state.DrawnTile.Value)))
            return [];

        var hand = PolicyInput.MakeHand(state);
        if (state.OurRiichi && state.DrawnTile is { IsRedFive: true } redDraw
            && state.Hand.Any(t => !t.IsRedFive && TileHelpers.SameKind(t, redDraw)))
        {
            // Analyzer normally keeps the red copy. A forced red discard must not retain its value.
            hand.ClosedTiles[hand.ClosedTiles.IndexOf(redDraw)] = TileHelpers.Normalize(redDraw);
        }
        var result = this.analyzer.Analyze(hand, PolicyInput.Context(state), ct);
        var candidates = new List<DiscardCandidate>();
        if (!result.IsValid)
            return candidates;
        foreach (var option in result.Ranked)
        {
            ct.ThrowIfCancellationRequested();
            if (state.OurRiichi && !TileHelpers.SameKind(option.DiscardTile, state.DrawnTile!.Value))
                continue;
            var tile = state.OurRiichi ? state.DrawnTile!.Value : option.DiscardTile;
            var survival = 1d;
            for (var seat = 1; seat <= 3; seat++)
                survival *= 1 - opponents.TenpaiProbability(seat) * opponents.Danger(tile, seat);
            // Keep analyzer units: score = analyzerScore - riskWeight * expected points lost.
            // Shanten stays the primary key because the analyzer uses different score scales by shanten.
            var score = option.Score - this.weights.DealInRiskWeight * opponents.ExpectedDealInCost(tile);
            var note = state.OurRiichi ? "riichi locked: drawn tile"
                : option.OpenYakuRisk ? "open hand has no winning yaku"
                : option.Waits.Count > 0 ? $"waits: {string.Join(", ", option.Waits)}"
                : $"{option.Eval.ShantenAfter}-shanten";
            candidates.Add(new DiscardCandidate(tile, option.Eval.ShantenAfter, option.Eval.Ukeire,
                option.Eval.Ukeire2, option.ValueEstimate, Math.Clamp(1 - survival, 0, 1), score, note)
            {
                Waits = option.Waits,
            });
        }

        return candidates.OrderBy(c => c.ShantenAfter).ThenByDescending(c => c.Score).ThenBy(c => c.Tile).ToArray();
    }
}
