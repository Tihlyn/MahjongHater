using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

public sealed class DecisionPolicy : IPolicy
{
    private const LegalAction Calls = LegalAction.Pon | LegalAction.Chi | LegalAction.MinKan | LegalAction.AnKan | LegalAction.ShouMinKan;
    private readonly IOpponentModel opponents;
    private readonly IDiscardPolicy discards;
    private readonly IPushFoldPolicy pushFold;
    private readonly ICallPolicy calls;
    private readonly IRiichiPolicy riichi;
    private readonly BetaoriPolicy betaori;
    private readonly PolicyWeights weights;
    // Only used for the between-turns hand summary (13 tiles, nothing legal).
    private readonly HandAnalyzer analyzer;

    public DecisionPolicy(IOpponentModel? opponents = null, IDiscardPolicy? discards = null,
        IPushFoldPolicy? pushFold = null, ICallPolicy? calls = null, IRiichiPolicy? riichi = null, PolicyWeights? weights = null,
        HandAnalyzer? analyzer = null)
    {
        this.weights = weights ?? PolicyWeights.Default;
        this.analyzer = analyzer ?? new HandAnalyzer();
        this.opponents = opponents ?? new OpponentModel(this.weights);
        this.discards = discards ?? new HeuristicDiscardPolicy(weights: this.weights);
        this.pushFold = pushFold ?? new PushFoldPolicy(this.weights);
        this.calls = calls ?? new CallPolicy();
        this.riichi = riichi ?? new RiichiPolicy(this.weights);
        this.betaori = new BetaoriPolicy(this.weights);
    }

    public ActionChoice Choose(StateSnapshot state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // IOpponentModel exposes Update + queries, so hold its transaction across all stages.
        // Lock the injected model itself to cover two policies sharing the same model.
        while (!Monitor.TryEnter(this.opponents, 25))
            ct.ThrowIfCancellationRequested();
        try
        {
            ct.ThrowIfCancellationRequested();
            return this.ChooseCore(state, ct);
        }
        finally
        {
            Monitor.Exit(this.opponents);
        }
    }

    private ActionChoice ChooseCore(StateSnapshot state, CancellationToken ct)
    {
        var steps = new List<Reason>();
        foreach (var (legal, kind, method) in new[]
                 {
                     (LegalAction.Tsumo, ActionKind.Tsumo, WinMethod.Tsumo),
                     (LegalAction.Ron, ActionKind.Ron, WinMethod.Ron),
                 })
        {
            ct.ThrowIfCancellationRequested();
            if (!state.Can(legal))
                continue;
            var han = WinningHan(state, method, ct);
            if (han >= this.weights.MinHanDoman && han > 0)
            {
                steps.Add(new Reason("win", $"Declare {kind}: {han} yaku han meets the {this.weights.MinHanDoman}-han minimum."));
                return new ActionChoice(kind, method == WinMethod.Tsumo ? state.DrawnTile : state.CallTile,
                    null, $"Declare {kind}.", steps.ToArray(), []);
            }

            steps.Add(new Reason("win", $"Decline {kind}: winning hand has {han} yaku han; need {this.weights.MinHanDoman}."));
        }

        ct.ThrowIfCancellationRequested();
        this.opponents.Update(state);
        ct.ThrowIfCancellationRequested();
        steps.Add(new Reason("opponents", string.Join("; ", Enumerable.Range(1, 3).Select(s =>
        {
            var seat = state.Seats[s];
            var who = s == state.DealerSeat ? $"seat {s} (dealer)" : $"seat {s}";
            var live = this.opponents.LiveSuji(s) is var ls and >= 0 ? $", {ls} live suji" : string.Empty;
            if (seat.Riichi)
            {
                var tile = seat.RiichiDiscardIndex >= 0 && seat.RiichiDiscardIndex < seat.Discards.Count ? $" on {seat.Discards[seat.RiichiDiscardIndex]}" : string.Empty;
                return $"{who} riichi{tile}, ~{this.opponents.Value(s):0} pts{live}";
            }

            var melds = seat.Melds.Count(m => m.IsOpen);
            return $"{who} tenpai {this.opponents.TenpaiProbability(s):P0}{(melds > 0 ? $", {melds} call(s), ~{this.opponents.Value(s):0} pts" : string.Empty)}{live}";
        }))));

        if ((state.Legal & Calls) != 0)
        {
            var call = this.calls.Evaluate(state, this.opponents, ct);
            ct.ThrowIfCancellationRequested();
            steps.Add(call.Reason);
            if (call.Accept)
                return new ActionChoice(call.Kind, state.CallTile ?? call.Meld?.Tiles[0], call.Meld,
                    call.Reason.Display, steps.ToArray(), []);
            if (!state.Can(LegalAction.Discard))
                return Pass("Pass the offered call.", steps);
        }

        if (!state.Can(LegalAction.Discard))
            return Pass("No discard is currently legal.", steps) with { Hand = this.WaitingSummary(state, ct) };

        // Every meld counts as 3 for hand arithmetic (a kan is 4 physical tiles but still one set),
        // matching HandAnalyzer — otherwise a post-kan hand is rejected forever.
        var total = state.Hand.Count + (3 * state.OurMelds.Count);
        if (total != 14)
        {
            var why = $"hand out of sync ({state.Hand.Count} closed tiles + {state.OurMelds.Count} melds = {total}; expected 14).";
            steps.Add(new Reason("state", why));
            return ActionChoice.NoneYet(why) with { Steps = steps.ToArray() };
        }

        ct.ThrowIfCancellationRequested();
        var candidates = this.discards.Rank(state, this.opponents, ct);
        ct.ThrowIfCancellationRequested();
        if (candidates.Count == 0)
        {
            const string why = "No valid discard candidate; waiting for stable hand and draw data.";
            steps.Add(new Reason("discard", why));
            return ActionChoice.NoneYet(why) with { Steps = steps.ToArray() };
        }

        var best = candidates[0];
        steps.Add(new Reason("discard", $"Best attack discard {best.Tile}: {best.ShantenAfter}-shanten, {best.Ukeire} live improving tiles."));
        PushFoldStance stance;
        if (this.weights.DefenseModel == DefenseModel.V2)
        {
            // Danger budget: keep attacking with any tile every threat accepts; otherwise
            // give up a shanten for a safe tile (mawashi) or fold outright (betaori).
            var decision = this.pushFold.Decide(state, this.opponents, candidates);
            ct.ThrowIfCancellationRequested();
            steps.Add(decision.Reason);
            var eligible = decision.NoThreat
                ? candidates
                : candidates.Where(c => decision.ThreatSeats.All(s => this.opponents.Danger(c.Tile, s) <= decision.MaxDanger)).ToList();
            if (eligible.Count > 0)
            {
                stance = PushFoldStance.Push;
                best = eligible[0];
                if (best.ShantenAfter > candidates[0].ShantenAfter)
                    steps.Add(new Reason("discard", $"Turn: {best.Tile} stays within budget ({best.Danger:P1} vs seat {best.DangerSeat}) at the cost of a shanten."));
                else if (!decision.NoThreat)
                    steps.Add(new Reason("discard", $"Push: {best.Tile} is within budget ({best.Danger:P1} vs seat {best.DangerSeat})."));
                // Keep the display order: chosen tile first, then the rest by attack merit.
                candidates = [best, .. candidates.Where(c => !ReferenceEquals(c, best))];
            }
            else
            {
                stance = PushFoldStance.Fold;
                candidates = this.betaori.Order(state, this.opponents, candidates, decision);
                best = candidates[0];
                steps.Add(new Reason("discard", $"Fold: nothing within budget; safest discard {best.Tile} ({best.Danger:P1} vs seat {best.DangerSeat}, {decision.SafeTiles} safe tile(s) in hand)."));
            }
        }
        else
        {
            stance = this.pushFold.Evaluate(state, this.opponents, best, out var foldReason);
            ct.ThrowIfCancellationRequested();
            steps.Add(foldReason);
            if (stance == PushFoldStance.Fold)
            {
                candidates = candidates.OrderBy(c => c.DealInRisk).ThenBy(c => c.ShantenAfter)
                    .ThenByDescending(c => c.Score).ThenBy(c => c.Tile).ToArray();
                best = candidates[0];
                steps.Add(new Reason("discard", $"Safest discard {best.Tile}: deal-in risk {best.DealInRisk:P1}."));
            }
        }

        var action = ActionKind.Discard;
        if (stance == PushFoldStance.Push && state.Can(LegalAction.Riichi) && best.ShantenAfter == 0)
        {
            var declare = this.riichi.ShouldDeclare(state, this.opponents, best, out var riichiReason);
            ct.ThrowIfCancellationRequested();
            steps.Add(riichiReason);
            if (declare)
                action = ActionKind.Riichi;
        }
        else
        {
            steps.Add(new Reason("riichi", stance == PushFoldStance.Fold
                ? "Keep defensive flexibility; do not declare riichi while folding."
                : "Riichi is unavailable or the hand is not tenpai."));
        }

        ct.ThrowIfCancellationRequested();
        return new ActionChoice(action, best.Tile, null, $"{action} {best.Tile}: {best.Note}.", steps.ToArray(), candidates)
        {
            Hand = new HandSummary(best.ShantenAfter, best.Ukeire, best.Waits),
        };
    }

    // Shanten / ukeire / waits of the 13-tile hand while we wait for a draw or a claim.
    private HandSummary? WaitingSummary(StateSnapshot state, CancellationToken ct)
    {
        if (state.Hand.Count + (3 * state.OurMelds.Count) != 13)
            return null;
        var result = this.analyzer.Analyze(PolicyInput.MakeHand(state), PolicyInput.Context(state), ct);
        return result.IsValid ? new HandSummary(result.ShantenAfterDiscard, result.Ukeire, result.TenpaiWaits) : null;
    }

    private static int WinningHan(StateSnapshot state, WinMethod method, CancellationToken ct)
    {
        var tile = method == WinMethod.Tsumo ? state.DrawnTile : state.CallTile;
        if (!tile.HasValue)
            return 0;
        var hand = PolicyInput.MakeHand(state);
        hand.WinningTile = tile;
        hand.WinMethod = method;
        if (method == WinMethod.Ron && hand.ClosedTiles.Count + (3 * hand.CalledMelds.Count) == 13)
            hand.ClosedTiles.Add(tile.Value);
        else if (!hand.ClosedTiles.Contains(tile.Value))
            return 0;

        var detector = new YakuDetector(state.Ruleset);
        var best = 0;
        foreach (var decomposition in HandDecomposer.GetWinningDecompositions(hand))
        {
            ct.ThrowIfCancellationRequested();
            var han = detector.Detect(hand, decomposition.Melds, decomposition.Pair, decomposition.Wait)
                .Sum(y => y.IsYakuman ? 13 : y.Han);
            best = Math.Max(best, han);
        }

        ct.ThrowIfCancellationRequested();
        return best;
    }

    private static ActionChoice Pass(string why, List<Reason> steps)
    {
        steps.Add(new Reason("pass", why));
        return ActionChoice.Pass(why) with { Steps = steps.ToArray() };
    }
}
