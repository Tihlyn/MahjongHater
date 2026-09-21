using System.Text.Json;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Precomputed;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Simulation;

// Full simulation records cover all action types. Live integration remains scoped
// to positions whose complete per-tile legality the current reader can verify.
public sealed class SimulationLookupPolicy(IPolicy fallback, SimulationDatabase database, PolicyWeights? weights = null, int minimumVisits = 8) : IPolicy
{
    private readonly PolicyWeights weights = weights ?? PolicyWeights.Default;
    private readonly IncrementalStateKey key = new();

    public ActionChoice Choose(StateSnapshot state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var legal = DiscardActionSpace.Generate(state);
        if (legal.Count == 0)
            return this.Fallback(state, ct, "Current action space uses the existing policy.");
        var model = new OpponentModel(this.weights);
        var beliefs = BeliefState.Capture(state, model);
        var identity = this.key.Update(state, beliefs, database.Manifest.Profile);
        SimulationRecord? record;
        try
        {
            if (!database.TryExact(identity, out record))
                return this.Fallback(state, ct, "No exact entry in the simulation database.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return this.Fallback(state, ct, $"Simulation database read failed: {ex.Message}");
        }
        ct.ThrowIfCancellationRequested();
        if (record!.Actions.Length != legal.Count || record.Actions.Any(a => a.Visits < minimumVisits
            || a.Action.Kind != SimActionKind.Discard || a.Action.Tile is null || !legal.Any(t => t.ToString() == a.Action.Tile)))
            return this.Fallback(state, ct, "Simulation entry lacks complete, sufficiently sampled legal discards.");
        var primary = model.PrimaryThreat();
        var candidates = record.Actions.OrderByDescending(a => a.MeanUtility).ThenBy(a => a.Action.Key, StringComparer.Ordinal).Select(a =>
        {
            var tile = Tile.Parse(a.Action.Tile!);
            var danger = primary >= 1 ? model.Explain(tile, primary) : default;
            var survival = beliefs.Aggregate(1d, (p, b) => p * (1 - b.Tenpai * b.Danger[TileHelpers.ToIndex(tile)]));
            return new DiscardCandidate(tile, a.Shanten, a.Ukeire, 0, 0, 1 - survival, a.MeanUtility,
                $"simulated {a.MeanScore:0} pts ({a.Visits} samples)")
            {
                Waits = a.Waits.Select(Tile.Parse).ToArray(), ExpectedValue = a.MeanScore,
                WinProbability = a.Wins / (double)a.Visits, DangerSeat = primary,
                Danger = primary >= 1 ? danger.Probability : 0, DangerRank = primary >= 1 ? danger.Rank : DangerRank.S,
                DangerNote = primary >= 1 ? danger.Why : "",
            };
        }).ToArray();
        var best = candidates[0];
        return new ActionChoice(ActionKind.Discard, best.Tile, null, $"Discard {best.Tile}: {best.Note}.",
            [new Reason("simulation", "Exact database hit; selected by simulated end-of-hand utility.")], candidates)
        { Hand = new HandSummary(best.ShantenAfter, best.Ukeire, best.Waits) };
    }

    private ActionChoice Fallback(StateSnapshot state, CancellationToken ct, string why)
    {
        var result = fallback.Choose(state, ct);
        return result with { Steps = [new Reason("simulation", why), .. result.Steps] };
    }
}
