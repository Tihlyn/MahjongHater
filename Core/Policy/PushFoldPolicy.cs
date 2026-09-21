using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

// What the push/fold layer hands the decision: the highest deal-in probability we accept
// cutting this turn against every threatening seat, and who those seats are.
public sealed record PushFoldDecision(double MaxDanger, IReadOnlyList<int> ThreatSeats, int PrimaryThreat, int SafeTiles, Reason Reason)
{
    public bool NoThreat => this.ThreatSeats.Count == 0;
}

public sealed class PushFoldPolicy : IPushFoldPolicy
{
    private readonly PolicyWeights weights;

    public PushFoldPolicy(PolicyWeights? weights = null)
    {
        this.weights = weights ?? PolicyWeights.Default;
    }

    // Legacy stance (v1.3): a declared riichi is a hard threat; the model's tenpai estimate
    // is soft (it climbs with every discard) and only decides for hands far from tenpai.
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

    // Danger budget (docs/DEFENSE_PLAN.md §3.4): Fukuchi's push thresholds by our shape,
    // hand value in points, turn, and whether the threat is the dealer, turned into the
    // highest conditional deal-in probability a discard may carry. `candidates` are ranked
    // best-attack-first; the first one is the hand we would be pushing with.
    public PushFoldDecision Decide(StateSnapshot state, IOpponentModel opponents, IReadOnlyList<DiscardCandidate> candidates)
    {
        var w = this.weights;
        var threats = new List<int>();
        for (var seat = 1; seat <= 3; seat++)
        {
            var s = state.Seats[seat];
            if (s.Riichi || opponents.TenpaiProbability(seat) >= w.ThreatTenpaiFloor)
                threats.Add(seat);
        }

        var primary = opponents.PrimaryThreat();
        if (primary < 1 || threats.Count == 0 || candidates.Count == 0)
            return new PushFoldDecision(1, [], -1, 0, new Reason("push/fold", "Push: nobody is threatening yet."));

        var best = candidates[0];
        var threatSeat = state.Seats[primary];
        var riichi = threatSeat.Riichi;
        var tenpaiT = riichi ? 1 : opponents.TenpaiProbability(primary);
        var dealerThreat = primary == state.DealerSeat;
        var weAreDealer = state.DealerSeat == 0;
        var open = state.IsOpen;
        var turn = state.Turn;
        var phase = turn <= w.EarlyTurnMax ? "early" : turn <= w.MidTurnMax ? "mid" : "late";
        var value = best.ShantenAfter == 0 ? Math.Max(best.MinPoints, best.ValuePoints * 0.8) : best.ValuePoints;
        var goodWait = best.ShantenAfter == 0 && best.Ukeire >= w.GoodWaitTiles;
        var tenpaiChance = best.ShantenAfter == 1 ? best.Ukeire * 5 / 6.0 / 100 : 0;
        var safeTiles = this.CountSafeTiles(state, opponents, primary);

        string shape;
        double budget;
        if (best.ShantenAfter == 0 || (best.ShantenAfter == 1 && tenpaiChance >= w.WideOneShantenChance))
        {
            var bad = best.ShantenAfter == 1 || !goodWait;
            shape = best.ShantenAfter == 1 ? "wide 1-shanten (as bad-wait tenpai)" : goodWait ? "tenpai, good wait" : "tenpai, bad wait";
            var (req10, req5) = Requirements(bad, phase, dealerThreat, weAreDealer, open, w);
            budget = value >= req10 ? w.BudgetTenPercent : value >= req5 ? w.BudgetFivePercent : w.RankBMax;
            if (!bad && phase == "early")
                budget = 1;
            if (weAreDealer && !bad && phase != "late")
                budget = 1;
        }
        else if (best.ShantenAfter == 1)
        {
            shape = tenpaiChance >= w.NarrowOneShantenChance ? "1-shanten" : "narrow 1-shanten";
            budget = tenpaiChance >= w.NarrowOneShantenChance
                ? (value >= 8000 ? w.BudgetSevenPercent : value >= 5200 ? w.BudgetFivePercent * 0.75 : w.RankBMax)
                : (value >= 12000 ? w.BudgetSevenPercent : w.RankBMax);
        }
        else
        {
            shape = $"{best.ShantenAfter}-shanten";
            budget = value >= 12000 && best.Ukeire >= 20 ? w.BudgetFivePercent : w.RankBMax;
        }

        var notes = new List<string> { shape, $"{value:0} pts", phase, riichi ? (dealerThreat ? "dealer riichi" : "riichi") : $"tenpai {tenpaiT:P0}" };

        // Two riichi: a tile has to pass both, and the second one halves what we accept.
        if (threats.Count(t => state.Seats[t].Riichi) >= 2)
        {
            budget *= w.TwoRiichiFactor;
            notes.Add("two riichi");
        }

        // Placement stakes when a model can say what winning or dealing in does to our final
        // placement: scale the point-based budget by (placement gain / placement loss) relative
        // to (hand value / threat value). Early in a match both differences are small and
        // proportional to points, so the factor stays near 1; in the last hands it becomes
        // "leading: fold", "4th: push" with the actual scores, for any hand of the match.
        var scored = state.Seats.Any(s => s.Score != 0);
        var stakesApplied = false;
        if (scored && w.PlacementStakesWeight > 0 && opponents is IPlacementModel placement)
        {
            var threatPoints = Math.Max(1000, opponents.Value(primary));
            var winDeltas = new int[4]; winDeltas[0] = (int)value; winDeltas[primary] = -(int)value;
            var lossDeltas = new int[4]; lossDeltas[0] = -(int)threatPoints; lossDeltas[primary] = (int)threatPoints;
            var pWin = placement.Placement(state, winDeltas);
            var pDraw = placement.Placement(state, new int[4]);
            var pLoss = placement.Placement(state, lossDeltas);
            if (pWin is not null && pDraw is not null && pLoss is not null)
            {
                double Utility(double[] p) => p.Zip(w.PlacementUtility, (a, b) => a * b).Sum();
                var gain = Utility(pWin) - Utility(pDraw);
                var loss = Utility(pDraw) - Utility(pLoss);
                var pointsRatio = value / threatPoints;
                var factor = loss <= 1e-6 ? w.PlacementStakesMax
                    : gain <= 1e-6 ? w.PlacementStakesMin
                    : Math.Clamp(gain / loss / pointsRatio, w.PlacementStakesMin, w.PlacementStakesMax);
                factor = Math.Pow(factor, w.PlacementStakesWeight);
                budget *= factor;
                stakesApplied = true;
                notes.Add($"placement stakes ×{factor:0.00} (win {gain:+0.00;-0.00}, deal-in {-loss:+0.00;-0.00})");
            }
        }

        // Placement in the last hand: first place folds more (the regulars override even a
        // bot's "push tenpai" here, docs/research/wwyd_sweep.md), a comfortable lead more still;
        // 3rd/4th pushes more. The fixed factors stand in when no placement model answered.
        if (scored && state.IsAllLast && !stakesApplied)
        {
            var us = state.Us.Score;
            var lead = us - state.Seats.Where(s => s.Seat != 0).Max(s => s.Score);
            var rank = 1 + state.Seats.Count(s => s.Seat != 0 && s.Score > us);
            if (rank == 1)
            {
                budget *= lead >= w.SafeLeadPoints ? w.LeadingFactor * w.LeadingFactor : w.LeadingFactor;
                notes.Add(lead >= w.SafeLeadPoints ? "leading all-last comfortably" : "leading all-last");
            }
            else if (rank >= 3)
            {
                budget *= w.TrailingFactor;
                notes.Add("trailing all-last");
            }
        }

        // Just before the draw only the tile matters: noten payment vs a mangan deal-in.
        if (state.WallRemaining <= 4 && best.ShantenAfter == 0)
        {
            budget = Math.Max(budget, dealerThreat ? w.PreDrawBudgetVsDealer : w.PreDrawBudget);
            notes.Add("last draws, keep tenpai");
        }

        // Nothing safe to fold with: attacking is the only option; one safe tile mid-game
        // is not a fold either.
        if (safeTiles == 0)
        {
            budget = 1;
            notes.Add("no safe tiles");
        }
        else if (safeTiles == 1 && phase != "late")
        {
            budget = Math.Max(budget, w.BudgetFivePercent);
            notes.Add("one safe tile");
        }

        // A seat that is only probably tenpai discounts the danger proportionally
        // (10 % against a 50 % tenpai is 5 % against a riichi).
        var maxDanger = Math.Clamp(budget / Math.Max(tenpaiT, w.ThreatTenpaiFloor), 0, 1);
        var reason = new Reason("push/fold", $"Budget {maxDanger:P0}: {string.Join(", ", notes)}; {safeTiles} safe tile(s).");
        return new PushFoldDecision(maxDanger, threats, primary, safeTiles, reason);
    }

    // Fukuchi ch. 2 "When in tenpai": minimum hand value (points) to cut a 10 % and a 5 %
    // danger tile, by wait shape, phase, dealer on either side, open/closed.
    private static (double Req10, double Req5) Requirements(bool badWait, string phase, bool dealerThreat, bool weAreDealer, bool open, PolicyWeights w)
    {
        if (weAreDealer)
        {
            if (!badWait)
                return (0, 0);
            return phase switch
            {
                "late" => (3900, 2600),
                _ => (3900, 2000),
            };
        }

        if (!dealerThreat)
        {
            if (!badWait)
                return phase switch
                {
                    "late" => (open ? 2600 : 2000, open ? 2000 : 0),
                    _ => (2000, 0),
                };
            return phase switch
            {
                "late" => (5200, open ? 3900 : 2600),
                _ => (3900, 2600),
            };
        }

        if (!badWait)
            return phase switch
            {
                "late" => (open ? 3900 : 2600, open ? 2600 : 2000),
                _ => (2000, open ? 2000 : 1300),
            };
        return phase switch
        {
            "late" => (8000, 5200),
            _ => (5200, 3900),
        };
    }

    // Tiles in our hand (with multiplicity) at rank B or safer against the seat.
    public int CountSafeTiles(StateSnapshot state, IOpponentModel opponents, int seat) =>
        state.Hand.Count(t => opponents.Danger(t, seat) < this.weights.RankBMax);
}

// Fold-mode discard choice (docs/DEFENSE_PLAN.md §3.5): safest against the primary threat,
// then against the others; prefer tiles we hold several of (turns banked) and tiles that stay
// safe (genbutsu over suji); among equals keep the hand's least useful tile.
public sealed class BetaoriPolicy
{
    private readonly PolicyWeights weights;

    public BetaoriPolicy(PolicyWeights? weights = null)
    {
        this.weights = weights ?? PolicyWeights.Default;
    }

    public IReadOnlyList<DiscardCandidate> Order(StateSnapshot state, IOpponentModel opponents, IReadOnlyList<DiscardCandidate> candidates, PushFoldDecision decision)
    {
        var seats = decision.ThreatSeats.OrderByDescending(s => s == decision.PrimaryThreat)
            .ThenByDescending(s => state.Seats[s].Riichi)
            .ThenByDescending(s => opponents.TenpaiProbability(s) * opponents.Value(s)).ToList();
        if (seats.Count == 0)
            return candidates;
        var copies = state.Hand.GroupBy(TileHelpers.ToIndex).ToDictionary(g => g.Key, g => g.Count());
        double Weighted(DiscardCandidate c) => seats.Sum(s => opponents.TenpaiProbability(s) * opponents.Danger(c.Tile, s) * opponents.Value(s));
        return candidates
            .OrderBy(c => Bucket(opponents.Danger(c.Tile, seats[0])))
            .ThenBy(c => seats.Count > 1 ? Bucket(opponents.Danger(c.Tile, seats[1])) : 0)
            .ThenBy(Weighted)
            .ThenByDescending(c => copies.GetValueOrDefault(TileHelpers.ToIndex(c.Tile)))
            .ThenBy(c => c.ShantenAfter)
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.Tile)
            .ToList();
    }

    // Small danger differences are noise; compare in half-percent steps so copies-in-hand
    // and hand shape can break ties between equally safe tiles.
    private static int Bucket(double danger) => (int)Math.Round(danger * 200);
}
