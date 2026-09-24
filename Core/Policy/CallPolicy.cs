using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

public sealed class CallPolicy : ICallPolicy
{
    private readonly HandAnalyzer analyzer;

    public CallPolicy(HandAnalyzer? analyzer = null)
    {
        this.analyzer = analyzer ?? new HandAnalyzer();
    }

    public CallDecision Evaluate(StateSnapshot state, IOpponentModel opponents, CancellationToken ct)
        => this.Evaluate(state, CallDescriptor.Describe(state, ct), ct);

    internal CallDecision Evaluate(StateSnapshot state, CallDescription description, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var diagnostics = description.Reasons.ToList();
        if (!description.Valid || description.Candidates.Count == 0)
            return CallDecision.Decline(description.Valid
                ? "No offered call has a constructible candidate."
                : "Call construction is invalid; decline the call.") with { Diagnostics = diagnostics };

        var before = Shanten.Calculate(state.Hand.ToList(), state.OurMelds.Count);
        var choices = new List<(CallDecision Decision, int Shanten, int Priority)>();
        AnalysisResult? baseline = null;
        foreach (var option in description.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (option.Meld.IsKan)
            {
                var accept = this.EvaluateKan(state, option, ref baseline, ct, out var after, out var why);
                diagnostics.Add(new Reason("kan", $"{option.Display}: {why}"));
                if (accept) choices.Add((Accept(option, "Kan preserves shanten and live improving tiles."), after, 2));
                continue;
            }

            if (option.Kind == ActionKind.Chi && BreaksOnlyPair(state.Hand, option.Remaining, option.Consumed))
            {
                diagnostics.Add(new Reason("call-evaluation", $"{option.Display}: decline; breaks the only pair."));
                continue;
            }

            var bestAfter = int.MaxValue;
            var bestYakuAfter = int.MaxValue;
            foreach (var tile in option.Remaining.Distinct())
            {
                ct.ThrowIfCancellationRequested();
                var kept = RemoveOne(option.Remaining, tile);
                var shanten = Shanten.Calculate(kept, option.MeldsAfter.Count);
                bestAfter = Math.Min(bestAfter, shanten);
                if (HasOpenYakuRoute(state, kept, option.MeldsAfter, option.Meld.Tiles[0]))
                    bestYakuAfter = Math.Min(bestYakuAfter, shanten);
            }
            var improves = bestYakuAfter < before;
            diagnostics.Add(new Reason("call-evaluation", $"{option.Display}: shanten {before}->{bestAfter}; "
                + (bestYakuAfter == int.MaxValue ? "decline; no open yaku route."
                    : $"best with yaku route {bestYakuAfter}; {(improves ? "accept" : "decline; no shanten improvement")}.")));
            if (improves)
                choices.Add((Accept(option, $"Call improves {before}-shanten to {bestYakuAfter}-shanten and retains an open yaku route."),
                    bestYakuAfter, option.Kind == ActionKind.Pon ? 0 : 1));
        }

        ct.ThrowIfCancellationRequested();
        var decision = choices.OrderBy(c => c.Shanten).ThenBy(c => c.Priority).Select(c => c.Decision).FirstOrDefault()
            ?? CallDecision.Decline("All validated calls were declined by policy; see candidate evaluations.");
        return decision with { Diagnostics = diagnostics };
    }

    // The learned policy can relax the speed preference, but never construction or kan safety.
    public IReadOnlyList<CallDecision> Viable(StateSnapshot state, CancellationToken ct)
        => this.Viable(state, CallDescriptor.Describe(state, ct), ct);

    internal IReadOnlyList<CallDecision> Viable(StateSnapshot state, CallDescription description, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var viable = new List<CallDecision>();
        AnalysisResult? baseline = null;
        foreach (var option in description.Candidates.Where(o => o.Kind is ActionKind.Pon or ActionKind.Chi or ActionKind.MinKan))
        {
            ct.ThrowIfCancellationRequested();
            var keepsYaku = option.Meld.IsKan
                ? this.EvaluateKan(state, option, ref baseline, ct, out _, out _)
                    && HasOpenYakuRoute(state, option.Remaining, option.MeldsAfter, option.Meld.Tiles[0])
                : option.Remaining.Distinct().Any(t => HasOpenYakuRoute(state, RemoveOne(option.Remaining, t), option.MeldsAfter, option.Meld.Tiles[0]));
            if (keepsYaku) viable.Add(Accept(option, "Call keeps an open yaku route."));
        }
        return viable;
    }

    private bool EvaluateKan(StateSnapshot state, CallCandidate option, ref AnalysisResult? baseline,
        CancellationToken ct, out int shanten, out string why)
    {
        shanten = int.MaxValue;
        if (option.Kind == ActionKind.MinKan && !state.IsOpen)
        {
            why = "decline by strategy: do not open a closed hand with daiminkan (shape is valid).";
            return false;
        }
        baseline ??= this.analyzer.Analyze(PolicyInput.MakeHand(state), PolicyInput.Context(state), ct);
        var afterState = state with { Hand = option.Remaining, OurMelds = option.MeldsAfter, DrawnTile = null };
        var context = PolicyInput.Context(afterState);
        if (option.Kind == ActionKind.MinKan && state.CallTile is { } claimed)
        {
            var index = context.SeenTiles.FindIndex(t => TileHelpers.SameKind(t, claimed));
            if (index >= 0) context.SeenTiles.RemoveAt(index);
        }
        var after = this.analyzer.Analyze(PolicyInput.MakeHand(afterState), context, ct);
        if (!baseline.IsValid || !after.IsValid)
        {
            why = $"decline: analysis invalid (before={baseline.IsValid}, after={after.IsValid}).";
            return false;
        }
        shanten = after.ShantenAfterDiscard;
        var metrics = $"shanten {baseline.ShantenAfterDiscard}->{shanten}, live improving tiles {baseline.Ukeire}->{after.Ukeire}";
        var rejection = shanten > baseline.ShantenAfterDiscard ? "shanten worsens"
            : after.Ukeire < baseline.Ukeire ? "fewer live improving tiles"
            : state.OurRiichi && !baseline.TenpaiWaits.Select(TileHelpers.ToIndex).Order()
                .SequenceEqual(after.TenpaiWaits.Select(TileHelpers.ToIndex).Order()) ? "riichi waits change"
            : null;
        why = $"{metrics}; " + (rejection is null ? "accept." : $"decline: {rejection}.");
        return rejection is null;
    }

    private static bool BreaksOnlyPair(IReadOnlyList<Tile> before, IReadOnlyList<Tile> after, IReadOnlyList<Tile> consumed)
    {
        var pairs = before.GroupBy(TileHelpers.ToIndex).Where(g => g.Count() == 2).ToArray();
        return pairs.Length == 1 && consumed.Any(t => TileHelpers.ToIndex(t) == pairs[0].Key)
            && !after.GroupBy(TileHelpers.ToIndex).Any(g => g.Count() == 2);
    }

    private static bool HasOpenYakuRoute(StateSnapshot state, IReadOnlyList<Tile> closed, IReadOnlyList<Meld> melds, Tile called)
    {
        bool Yakuhai(Tile t) => t.Suit == TileSuit.Dragon || t.Suit == TileSuit.Wind
            && (t.Number == (int)state.RoundWind || t.Number == (int)state.SeatWind);
        if (Yakuhai(called) || melds.Any(m => m.IsTriplet && Yakuhai(m.Tiles[0]))
            || closed.GroupBy(TileHelpers.ToIndex).Any(g => g.Count() >= 3 && Yakuhai(g.First())))
            return true;
        var all = closed.Concat(melds.SelectMany(m => m.Tiles)).ToArray();
        if (state.Ruleset.Kuitan && all.All(t => t.IsSimple))
            return true;
        return new[] { TileSuit.Man, TileSuit.Pin, TileSuit.Sou }.Any(suit =>
            all.Count(t => t.Suit == suit || t.IsHonor) >= 9
            && melds.SelectMany(m => m.Tiles).All(t => t.Suit == suit || t.IsHonor));
    }

    private static List<Tile> RemoveOne(IReadOnlyList<Tile> tiles, Tile tile)
    {
        var remaining = tiles.ToList();
        remaining.Remove(tile);
        return remaining;
    }

    private static CallDecision Accept(CallCandidate option, string why) =>
        new(true, option.Kind, option.Meld, new Reason("call", why));

}
