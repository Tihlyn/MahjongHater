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
    {
        ct.ThrowIfCancellationRequested();
        var before = Shanten.Calculate(state.Hand.ToList(), state.OurMelds.Count);
        var choices = new List<(CallDecision Decision, int Shanten, int Priority)>();
        AnalysisResult? baseline = null;
        foreach (var option in Options(state))
        {
            ct.ThrowIfCancellationRequested();
            var remaining = state.Hand.ToList();
            foreach (var tile in option.Consumed)
                remaining.Remove(tile);
            var melds = state.OurMelds.ToList();
            if (option.Replaced is not null)
                melds.Remove(option.Replaced);
            melds.Add(option.Meld);

            if (option.Meld.IsKan)
            {
                baseline ??= this.analyzer.Analyze(PolicyInput.MakeHand(state), PolicyInput.Context(state), ct);
                var afterState = state with { Hand = remaining, OurMelds = melds };
                var context = PolicyInput.Context(afterState);
                // The claimed discard now belongs to our meld, so count it only once.
                if (option.Kind == ActionKind.MinKan && state.CallTile is { } claimed)
                {
                    var index = context.SeenTiles.FindIndex(t => TileHelpers.SameKind(t, claimed));
                    if (index >= 0)
                        context.SeenTiles.RemoveAt(index);
                }
                var after = this.analyzer.Analyze(PolicyInput.MakeHand(afterState), context, ct);
                if (!baseline.IsValid || !after.IsValid || after.ShantenAfterDiscard > baseline.ShantenAfterDiscard
                    || after.Ukeire < baseline.Ukeire)
                    continue;
                // Riichi kan must preserve the wait, not just its live tile count.
                if (state.OurRiichi && !baseline.TenpaiWaits.Select(TileHelpers.ToIndex).Order()
                        .SequenceEqual(after.TenpaiWaits.Select(TileHelpers.ToIndex).Order()))
                    continue;
                choices.Add((Accept(option, "Kan preserves shanten and live improving tiles."), after.ShantenAfterDiscard, 2));
                continue;
            }

            if (option.Kind == ActionKind.Chi && BreaksOnlyPair(state.Hand, remaining, option.Consumed))
                continue;

            // Pon/chi require a discard before the new shanten and yaku route are useful.
            var postDiscards = remaining.Distinct().Select(t => RemoveOne(remaining, t)).ToList();
            var bestAfter = int.MaxValue;
            foreach (var kept in postDiscards)
            {
                ct.ThrowIfCancellationRequested();
                var shanten = Shanten.Calculate(kept, state.OurMelds.Count + 1);
                if (shanten < before && HasOpenYakuRoute(state, kept, melds, option.Meld.Tiles[0]))
                    bestAfter = Math.Min(bestAfter, shanten);
            }

            if (bestAfter < before)
                choices.Add((Accept(option, $"Call improves {before}-shanten to {bestAfter}-shanten and retains an open yaku route."),
                    bestAfter, option.Kind == ActionKind.Pon ? 0 : option.Kind == ActionKind.Chi ? 1 : 2));
        }

        ct.ThrowIfCancellationRequested();
        return choices.OrderBy(c => c.Shanten).ThenBy(c => c.Priority).Select(c => c.Decision).FirstOrDefault()
            ?? CallDecision.Decline("No call improves the hand while preserving a yaku route or safe kan shape.");
    }

    private static IEnumerable<CallOption> Options(StateSnapshot state)
    {
        if (!state.OurRiichi && state.CallTile is { } called && state.CallFromSeat is >= 1 and <= 3)
        {
            var copies = state.Hand.Where(t => TileHelpers.SameKind(t, called)).OrderBy(t => t.IsRedFive).ToArray();
            if (state.Can(LegalAction.Pon) && copies.Length >= 2)
                yield return Make(ActionKind.Pon, copies.Take(2).ToArray(), called, MeldType.Pon);
            if (state.Can(LegalAction.MinKan) && state.IsOpen && copies.Length >= 3)
                yield return Make(ActionKind.MinKan, copies.Take(3).ToArray(), called, MeldType.Daiminkan);
            if (state.Can(LegalAction.Chi) && state.CallFromSeat == 3 && !called.IsHonor)
            {
                // With the chooser open only the game's shapes are on offer.
                var starts = state.CallShapes.Count > 0
                    ? state.CallShapes.Select(m => m.Tiles.Min(t => t.Number)).Distinct()
                    : Enumerable.Range(Math.Max(1, called.Number - 2), Math.Min(7, called.Number) - Math.Max(1, called.Number - 2) + 1);
                foreach (var start in starts)
                {
                    var consumed = new List<Tile>();
                    foreach (var number in Enumerable.Range(start, 3).Where(n => n != called.Number))
                    {
                        var matches = state.Hand.Where(t => t.Suit == called.Suit && t.Number == number)
                            .OrderBy(t => t.IsRedFive).ToArray();
                        if (matches.Length > 0)
                            consumed.Add(matches[0]);
                    }

                    if (consumed.Count == 2)
                        yield return Make(ActionKind.Chi, consumed.ToArray(), called, MeldType.Chi);
                }
            }
        }

        if (state.Can(LegalAction.AnKan))
        {
            foreach (var group in state.Hand.GroupBy(TileHelpers.ToIndex).Where(g => g.Count() == 4))
            {
                if (state.OurRiichi && (!state.DrawnTile.HasValue || !TileHelpers.SameKind(group.First(), state.DrawnTile.Value)))
                    continue;
                yield return Make(ActionKind.AnKan, group.ToArray(), null, MeldType.Ankan);
            }
        }

        if (state.Can(LegalAction.ShouMinKan) && state.IsOpen && !state.OurRiichi)
        {
            foreach (var pon in state.OurMelds.Where(m => m.Type == MeldType.Pon && m.IsOpen))
            {
                foreach (var tile in state.Hand.Where(t => TileHelpers.SameKind(t, pon.Tiles[0])).Distinct())
                {
                    var template = Meld.MakeKan(tile, MeldType.Shouminkan);
                    yield return new CallOption(ActionKind.ShouMinKan,
                        WithCopies(template, pon.Tiles.Append(tile).ToArray()), [tile], pon);
                }
            }
        }
    }

    private static CallOption Make(ActionKind kind, Tile[] consumed, Tile? called, MeldType type)
    {
        var tiles = called.HasValue ? consumed.Append(called.Value).ToArray() : consumed;
        var template = type == MeldType.Chi ? Meld.MakeChi(tiles[0], tiles[1], tiles[2])
            : type == MeldType.Pon ? Meld.MakePon(tiles[0], true) : Meld.MakeKan(tiles[0], type);
        // Factories describe the shape; retain the actual red/plain copies and mark chi open.
        return new CallOption(kind, WithCopies(template, tiles), consumed, null);
    }

    private static Meld WithCopies(Meld template, Tile[] tiles)
    {
        var meld = new Meld(template.Type, tiles, template.Type != MeldType.Ankan);
        // Meld's constructor normalizes red fives. Restore physical copies on this new instance.
        tiles.OrderBy(t => t).ToArray().CopyTo(meld.Tiles, 0);
        return meld;
    }

    private static bool BreaksOnlyPair(IReadOnlyList<Tile> before, List<Tile> after, Tile[] consumed)
    {
        var pairs = before.GroupBy(TileHelpers.ToIndex).Where(g => g.Count() == 2).ToArray();
        return pairs.Length == 1 && consumed.Any(t => TileHelpers.ToIndex(t) == pairs[0].Key)
            && !after.GroupBy(TileHelpers.ToIndex).Any(g => g.Count() == 2);
    }

    private static bool HasOpenYakuRoute(StateSnapshot state, List<Tile> closed, List<Meld> melds, Tile called)
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

    private static List<Tile> RemoveOne(List<Tile> tiles, Tile tile)
    {
        var remaining = tiles.ToList();
        remaining.Remove(tile);
        return remaining;
    }

    private static CallDecision Accept(CallOption option, string why) =>
        new(true, option.Kind, option.Meld, new Reason("call", why));

    private sealed record CallOption(ActionKind Kind, Meld Meld, Tile[] Consumed, Meld? Replaced);
}
