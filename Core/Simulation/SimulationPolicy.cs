using MahjongHater.Core.Policy;

namespace MahjongHater.Core.Simulation;

public enum SimulationPolicyMode { Guideline, Existing }

public sealed class SimulationPolicy
{
    private readonly PolicyWeights weights;
    private readonly SimulationPolicyMode mode;
    private readonly OpponentModel opponents;
    private readonly DecisionPolicy existing;
    private readonly CallPolicy calls = new();

    public SimulationPolicy(SimulationPolicyMode mode = SimulationPolicyMode.Guideline, PolicyWeights? weights = null)
    {
        this.mode = mode;
        this.weights = weights ?? PolicyWeights.Default;
        this.opponents = new OpponentModel(this.weights);
        this.existing = new DecisionPolicy(weights: this.weights);
    }

    public SimAction Choose(SimulationObservation observation, IReadOnlyList<SimAction> legal, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (legal.Count == 0)
            throw new ArgumentException("Decision has no legal actions.");
        if (legal.Count == 1)
            return legal[0];
        var win = legal.FirstOrDefault(a => a.Kind is SimActionKind.Tsumo or SimActionKind.Ron);
        if (win is not null)
            return win;
        var s = observation.Snapshot;
        if (this.mode == SimulationPolicyMode.Existing)
        {
            var chosen = Match(this.existing.Choose(s, ct), legal);
            if (chosen is not null)
                return chosen;
        }
        this.opponents.Update(s);
        var abort = legal.FirstOrDefault(a => a.Kind == SimActionKind.NineTerminals);
        if (abort is not null && Shanten.Calculate(s.Hand.ToList(), s.OurMelds.Count) >= 4)
            return abort;
        if (legal.Any(a => a.Kind is SimActionKind.Pon or SimActionKind.Chi or SimActionKind.OpenKan or SimActionKind.ClosedKan or SimActionKind.AddedKan))
        {
            var call = this.calls.Evaluate(s, this.opponents, ct);
            if (call.Accept)
            {
                var matched = Match(new ActionChoice(call.Kind, s.CallTile ?? call.Meld?.Tiles[0], call.Meld, "", [], []), legal);
                if (matched is not null)
                    return matched;
            }
        }
        var discards = legal.Where(a => a.Kind == SimActionKind.Discard).ToArray();
        if (discards.Length == 0)
            return legal.First(a => a.Kind == SimActionKind.Pass);
        var visible = new int[34];
        foreach (var tile in s.Hand.Concat(s.SeenForAnalyzer()).Concat(s.OurMelds.SelectMany(m => m.Tiles)).Concat(s.DoraIndicators))
            visible[TileHelpers.ToIndex(tile)]++;
        var counts = new int[34];
        foreach (var tile in s.Hand)
            counts[TileHelpers.ToIndex(tile)]++;
        var available = visible.Select(n => Math.Max(0, 4 - n)).ToArray();
        var evaluations = Shanten.EvaluateDiscards(counts, s.OurMelds.Count, available, ct).ToDictionary(e => TileHelpers.ToIndex(e.Discard));
        var threat = this.opponents.PrimaryThreat();
        var candidates = discards.Select(a =>
        {
            var tile = Tile.Parse(a.Tile!);
            var e = evaluations[TileHelpers.ToIndex(tile)];
            var kept = s.Hand.ToList();
            kept.Remove(tile);
            var dora = kept.Concat(s.OurMelds.SelectMany(m => m.Tiles)).Sum(t => (t.IsRedFive ? 1 : 0)
                + s.DoraIndicators.Count(i => TileHelpers.SameKind(TileDangerModel.DoraOf(i), t)));
            var value = HandValue.EstimatePoints(s, dora, this.weights);
            var probability = HandValue.WinProbability(e.ShantenAfter, e.Ukeire, 0, s.WallRemaining, this.weights);
            var cost = this.opponents.ExpectedDealInCost(tile);
            return new DiscardCandidate(tile, e.ShantenAfter, e.Ukeire, 0, dora, 0,
                probability * value - this.weights.PushExposureTurns * cost, "simulation guideline")
            {
                ValuePoints = value, WinProbability = probability, ExpectedValue = probability * value - cost,
                Danger = threat >= 1 ? this.opponents.Danger(tile, threat) : 0, DangerSeat = threat,
            };
        }).OrderBy(c => c.ShantenAfter).ThenByDescending(c => c.Score).ThenBy(c => c.Tile).ToArray();
        var budget = new PushFoldPolicy(this.weights).Decide(s, this.opponents, candidates);
        var eligible = budget.NoThreat ? candidates : candidates.Where(c => budget.ThreatSeats.All(seat => this.opponents.Danger(c.Tile, seat) <= budget.MaxDanger)).ToArray();
        var best = eligible.Length > 0 ? eligible[0] : candidates.OrderBy(c => this.opponents.ExpectedDealInCost(c.Tile)).ThenBy(c => c.ShantenAfter).First();
        var riichi = legal.FirstOrDefault(a => a.Kind == SimActionKind.Riichi && a.Tile == best.Tile.ToString());
        if (eligible.Length > 0 && riichi is not null && new RiichiPolicy(this.weights).ShouldDeclare(s, this.opponents, best, out _))
            return riichi;
        return discards.First(a => a.Tile == best.Tile.ToString());
    }

    private static SimAction? Match(ActionChoice choice, IReadOnlyList<SimAction> legal)
    {
        var kind = choice.Kind switch
        {
            ActionKind.Discard => SimActionKind.Discard, ActionKind.Riichi => SimActionKind.Riichi,
            ActionKind.Tsumo => SimActionKind.Tsumo, ActionKind.Ron => SimActionKind.Ron, ActionKind.Pass => SimActionKind.Pass,
            ActionKind.Pon => SimActionKind.Pon, ActionKind.Chi => SimActionKind.Chi, ActionKind.MinKan => SimActionKind.OpenKan,
            ActionKind.AnKan => SimActionKind.ClosedKan, ActionKind.ShouMinKan => SimActionKind.AddedKan, _ => (SimActionKind?)null,
        };
        foreach (var action in legal.Where(a => a.Kind == kind))
        {
            if (choice.Call is not null)
            {
                var formed = action.ConsumedTiles().ToList();
                if (action.Kind is SimActionKind.Pon or SimActionKind.Chi or SimActionKind.OpenKan)
                    formed.Add(Tile.Parse(action.Tile!));
                if (action.Kind == SimActionKind.AddedKan
                    ? choice.Call.Tiles.All(t => TileHelpers.SameKind(t, Tile.Parse(action.Tile!)))
                    : formed.Order().SequenceEqual(choice.Call.Tiles.Order()))
                    return action;
            }
            else if (choice.Tile?.ToString() == action.Tile || action.Kind == SimActionKind.Pass)
                return action;
        }
        return null;
    }
}
