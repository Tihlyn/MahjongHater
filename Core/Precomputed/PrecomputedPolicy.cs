using MahjongHater.Core.Policy;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Precomputed;

public sealed class PrecomputedPolicy : IPolicy
{
    private readonly IPolicy fallback;
    private readonly PolicyTable table;
    private readonly PolicyWeights weights;
    private readonly string profile;
    private readonly IncrementalStateKey key = new();
    private readonly int minimumVisits;

    public PrecomputedPolicy(IPolicy fallback, PolicyTable table, PolicyWeights? weights = null, int minimumVisits = 8)
    {
        if (minimumVisits < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumVisits));
        this.fallback = fallback;
        this.table = table;
        this.weights = weights ?? PolicyWeights.Default;
        this.profile = BeliefState.Profile(this.weights);
        this.minimumVisits = minimumVisits;
    }

    public ActionChoice Choose(StateSnapshot state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var legal = DiscardActionSpace.Generate(state);
        if (legal.Count == 0)
            return this.Fallback(state, ct, "Position is outside the trained discard action space.");

        // Each worker owns its model: no interleaving update/query transactions.
        var model = new OpponentModel(this.weights);
        var beliefs = BeliefState.Capture(state, model);
        var identity = this.key.Update(state, beliefs, this.profile);
        ct.ThrowIfCancellationRequested();
        if (!this.table.TryGet(identity, out var stored))
            return this.Fallback(state, ct, "No precomputed entry for this belief state.");

        // Require complete coverage of current legal alternatives; never choose from
        // a partial row or execute a stale/corrupt action simply because it has high EV.
        if (stored.Count != legal.Count || stored.Any(a => a.Visits < this.minimumVisits || !legal.Contains(Tile.Parse(a.Tile))))
            return this.Fallback(state, ct, "Precomputed entry has incomplete legal actions or insufficient samples.");

        var ranked = stored.OrderByDescending(a => a.MeanUtility).ThenBy(a => Tile.Parse(a.Tile)).ToArray();
        var candidates = ranked.Select(a =>
        {
            var tile = Tile.Parse(a.Tile);
            var survival = beliefs.Aggregate(1d, (p, b) => p * (1 - b.Tenpai * b.Danger[TileHelpers.ToIndex(tile)]));
            var primary = model.PrimaryThreat();
            var danger = primary >= 1 ? model.Explain(tile, primary) : default;
            return new DiscardCandidate(tile, a.Shanten, a.Ukeire, 0, 0, 1 - survival, a.MeanUtility,
                $"offline estimate {a.MeanUtility:0} pts ({a.Visits} samples)")
            {
                Waits = a.Waits.Select(Tile.Parse).ToArray(),
                ExpectedValue = a.MeanUtility,
                DangerSeat = primary,
                Danger = primary >= 1 ? danger.Probability : 0,
                DangerRank = primary >= 1 ? danger.Rank : DangerRank.S,
                DangerNote = primary >= 1 ? danger.Why : string.Empty,
            };
        }).ToArray();
        ct.ThrowIfCancellationRequested();
        var best = candidates[0];
        return new ActionChoice(ActionKind.Discard, best.Tile, null, $"Discard {best.Tile}: {best.Note}.",
            [new Reason("precomputed", "Exact belief-state hit; highest offline estimated utility among all legal discards.")], candidates)
        {
            Hand = new HandSummary(best.ShantenAfter, best.Ukeire, best.Waits),
        };
    }

    private ActionChoice Fallback(StateSnapshot state, CancellationToken ct, string why)
    {
        var choice = this.fallback.Choose(state, ct);
        return choice with { Steps = [new Reason("precomputed", why + " Using the existing policy."), .. choice.Steps] };
    }
}
