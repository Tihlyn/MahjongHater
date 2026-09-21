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
        var primary = opponents.PrimaryThreat();
        var v2 = this.weights.DefenseModel == DefenseModel.V2;
        int[]? visible = null;
        if (v2)
        {
            visible = new int[34];
            foreach (var t in state.Hand.Concat(state.SeenForAnalyzer()).Concat(state.OurMelds.SelectMany(m => m.Tiles)).Concat(state.DoraIndicators))
                visible[TileHelpers.ToIndex(t)]++;
        }

        foreach (var option in result.Ranked)
        {
            ct.ThrowIfCancellationRequested();
            if (state.OurRiichi && !TileHelpers.SameKind(option.DiscardTile, state.DrawnTile!.Value))
                continue;
            var tile = state.OurRiichi ? state.DrawnTile!.Value : option.DiscardTile;
            var survival = 1d;
            for (var seat = 1; seat <= 3; seat++)
                survival *= 1 - opponents.TenpaiProbability(seat) * opponents.Danger(tile, seat);
            var risk = Math.Clamp(1 - survival, 0, 1);
            var note = state.OurRiichi ? "riichi locked: drawn tile"
                : option.OpenYakuRisk ? "open hand has no winning yaku"
                : option.Waits.Count > 0 ? $"waits: {string.Join(", ", option.Waits)}"
                : $"{option.Eval.ShantenAfter}-shanten";
            var explained = primary >= 1 ? opponents.Explain(tile, primary) : default;

            double score, valuePoints = 0, winProbability = 0, ev = 0;
            var minPoints = 0;
            if (v2)
            {
                // Everything in points per hand: what the hand is worth times the chance of
                // cashing it, minus what the dangerous discards a push costs are expected to lose.
                if (option.Eval.ShantenAfter == 0)
                    (valuePoints, minPoints) = HandValue.TenpaiPoints(state, tile, option.Waits, visible!, this.weights, ct);
                else
                    valuePoints = HandValue.EstimatePoints(state, option.ValueEstimate, this.weights);
                winProbability = HandValue.WinProbability(option.Eval.ShantenAfter, option.Eval.Ukeire, option.Eval.Ukeire2, state.WallRemaining, this.weights);
                var cost = opponents.ExpectedDealInCost(tile);
                ev = winProbability * valuePoints - this.weights.PushExposureTurns * cost;
                // The budget/betaori layer decides defense; here risk only breaks ties unless
                // point-EV ranking is switched on.
                score = this.weights.RankByPointEv ? ev
                    : option.Score - cost * (option.Eval.ShantenAfter == 0 ? this.weights.TenpaiRiskTieBreakPerPoint : this.weights.RiskTieBreakPerPoint);
            }
            else
            {
                // Legacy units: analyzer score minus expected points lost.
                score = option.Score - this.weights.DealInRiskWeight * opponents.ExpectedDealInCost(tile);
            }

            candidates.Add(new DiscardCandidate(tile, option.Eval.ShantenAfter, option.Eval.Ukeire,
                option.Eval.Ukeire2, option.ValueEstimate, risk, score, note)
            {
                Waits = option.Waits,
                Danger = primary >= 1 ? explained.Probability : 0,
                DangerRank = primary >= 1 ? explained.Rank : DangerRank.S,
                DangerNote = primary >= 1 ? explained.Why : string.Empty,
                DangerSeat = primary,
                ValuePoints = valuePoints,
                MinPoints = minPoints,
                WinProbability = winProbability,
                ExpectedValue = ev,
            });
        }

        // Shanten stays the primary key (the push/fold layer decides when to give one up);
        // inside a level the point-EV (v2) or the analyzer score (legacy) orders the tiles.
        return candidates.OrderBy(c => c.ShantenAfter).ThenByDescending(c => c.Score).ThenBy(c => c.Tile).ToArray();
    }
}
