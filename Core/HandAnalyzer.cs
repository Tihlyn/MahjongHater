namespace MahjongHater.Core;

// Context provided to the analyzer about tiles visible outside the player's own hand.
public sealed class AnalysisContext
{
    // Tiles visible outside the player's hand: discard piles + opponent called melds.
    // Do NOT include dora indicators or the player's own melds — the analyzer adds those.
    public List<Tile> SeenTiles { get; init; } = [];

    // Raw dora indicator tiles (the tile AFTER each indicator is the actual dora).
    public List<Tile> DoraIndicators { get; init; } = [];

    public int WallRemaining { get; init; } = 70;

    public Wind SeatWind { get; init; } = Wind.East;

    public Wind RoundWind { get; init; } = Wind.East;

    public RulesetOptions Ruleset { get; init; } = RulesetOptions.Default;

    // True once riichi has been declared this hand. The player is then locked into
    // discarding whatever they just drew (tsumogiri) every turn until the hand ends —
    // "best discard" no longer applies, since there is no choice to optimize.
    public bool IsRiichi { get; init; }
}

// One ranked discard candidate.
public sealed class DiscardOption
{
    public required DiscardEvaluation Eval { get; init; }

    // The actual copy to throw: plain five preferred over red when both are held.
    public required Tile DiscardTile { get; init; }

    // Open hand whose every wait completes a 0-han hand — it cannot win by itself.
    public bool OpenYakuRisk { get; init; }

    // Rough han-ish value of the kept hand (dora + red fives + best wait yaku).
    public double ValueEstimate { get; init; }

    public double Score { get; init; }

    public List<Tile> Waits { get; init; } = [];
}

public class HandAnalyzer
{
    // All ranking constants in one place for in-game tuning.
    private static class Tuning
    {
        public const double TenpaiValueWeight = 0.35;   // ukeire × (1 + w × value)
        public const double OpenYakulessPenalty = 0.05; // score crush for call-locked hands
        public const int UkeireWeight = 10_000;         // primary key for shanten ≥ 1
        public const double KeptValueWeight = 50;       // additive value term vs ukeire2
        public const int RiichiMinWall = 4;
    }

    public virtual AnalysisResult Analyze(Hand hand, AnalysisContext? context = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hand);
        var ctx = context ?? new AnalysisContext();

        var called = hand.CalledMelds.Count;
        var totalTiles = hand.ClosedTiles.Count + (3 * called);
        if (totalTiles != 13 && totalTiles != 14)
        {
            return new AnalysisResult
            {
                IsValid = false,
                Reasoning = hand.ClosedTiles.Count == 0
                    ? "No closed tiles available to analyze."
                    : $"Waiting for stable hand data ({hand.ClosedTiles.Count} closed tiles, {called} melds).",
            };
        }

        var counts = ToCounts(hand.ClosedTiles);
        var availability = BuildAvailability(counts, hand, ctx);
        var wallEst = Math.Max(1, ctx.WallRemaining > 0 ? ctx.WallRemaining : 70 - totalTiles);

        return totalTiles == 14
            ? AnalyzeDiscard(hand, ctx, counts, availability, called, wallEst, ct)
            : AnalyzeWaiting(hand, ctx, counts, availability, called, wallEst, ct);
    }

    // 14-tile hand: rank every discard.
    private AnalysisResult AnalyzeDiscard(
        Hand hand, AnalysisContext ctx, int[] counts, int[] availability,
        int called, int wallEst, CancellationToken ct)
    {
        var evals = Shanten.EvaluateDiscards(counts, called, availability, ct);
        var minShanten = evals.Min(e => e.ShantenAfter);
        var doraValues = ctx.DoraIndicators.Select(DoraFromIndicator).ToList();
        var isOpen = hand.IsOpen;

        var options = new List<DiscardOption>(evals.Count);
        foreach (var eval in evals)
        {
            ct.ThrowIfCancellationRequested();
            var isFinalist = eval.ShantenAfter == minShanten;
            var waits = eval.ShantenAfter == 0 ? MaskToTiles(eval.UsefulKindsMask) : [];

            // 2-step ukeire for finalists that are not yet tenpai.
            if (isFinalist && eval.ShantenAfter >= 1)
            {
                var kind = TileHelpers.ToIndex(eval.Discard);
                counts[kind]--;
                eval.Ukeire2 = Shanten.ComputeUkeire2(counts, called, availability, ct);
                counts[kind]++;
            }

            // Yaku presence per wait — only where it can gate/score: tenpai finalists.
            var bestWaitHan = 0;
            var openYakuRisk = false;
            if (isFinalist && eval.ShantenAfter == 0 && waits.Count > 0)
            {
                bestWaitHan = BestHanAcrossWaits(hand, ctx, eval.Discard, waits, ct);
                openYakuRisk = isOpen && bestWaitHan == 0;
            }

            var value = EstimateKeptValue(hand, eval.Discard, doraValues) + bestWaitHan
                        + (!isOpen && eval.ShantenAfter == 0 ? 1 : 0); // riichi is always available closed

            var score = eval.ShantenAfter == 0
                ? eval.Ukeire * (1 + (Tuning.TenpaiValueWeight * value)) * (openYakuRisk ? Tuning.OpenYakulessPenalty : 1)
                : (eval.Ukeire * (double)Tuning.UkeireWeight) + eval.Ukeire2 + (Tuning.KeptValueWeight * value);

            options.Add(new DiscardOption
            {
                Eval = eval,
                DiscardTile = PickDiscardCopy(hand.ClosedTiles, eval.Discard),
                OpenYakuRisk = openYakuRisk,
                ValueEstimate = value,
                Score = score,
                Waits = waits,
            });
        }

        var ranked = options
            .OrderBy(o => o.Eval.ShantenAfter)
            .ThenByDescending(o => o.Score)
            .ThenBy(o => o.Eval.Discard)
            .ToList();

        // Once riichi is declared, the player is locked into discarding whatever they
        // just drew (tsumogiri) every turn until the hand ends — there is no choice
        // left to optimize, so "best discard" no longer applies. Re-anchor on the
        // forced tile instead of the top-ranked one; the ranked list and per-option
        // numbers stay as computed (still informative), but the drawn tile's own
        // shanten/ukeire/waits are what the player is actually about to see.
        DiscardOption best;
        string? lockedReasoning = null;
        if (ctx.IsRiichi && hand.ClosedTiles.Count > 0)
        {
            var drawnKind = TileHelpers.ToIndex(hand.ClosedTiles[^1]);
            var forced = ranked.Find(o => TileHelpers.ToIndex(o.Eval.Discard) == drawnKind);
            best = forced ?? ranked[0];
            lockedReasoning = $"Riichi is locked in — discard {best.DiscardTile} (the tile you just drew); no other tile can be selected.";
        }
        else
        {
            best = ranked[0];
        }

        var tenpai = best.Eval.ShantenAfter == 0;
        var winProbability = tenpai ? (double)best.Eval.Ukeire / wallEst : 0d;
        var riichi = !ctx.IsRiichi && tenpai && !hand.IsOpen && best.Eval.Ukeire > 0 && ctx.WallRemaining >= Tuning.RiichiMinWall;

        return new AnalysisResult
        {
            IsValid = true,
            BestDiscard = best.DiscardTile,
            ShantenAfterDiscard = best.Eval.ShantenAfter,
            TenpaiWaits = best.Waits,
            WinProbability = winProbability,
            Ukeire = best.Eval.Ukeire,
            RiichiRecommended = riichi,
            Ranked = ranked,
            Reasoning = lockedReasoning ?? BuildReasoning(best, riichi, winProbability),
        };
    }

    // 13-tile hand (opponent's turn): report current shanten/waits, no discard advice.
    private static AnalysisResult AnalyzeWaiting(
        Hand hand, AnalysisContext ctx, int[] counts, int[] availability,
        int called, int wallEst, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var shanten = Shanten.Calculate(counts, called);

        var ukeire = 0;
        var waits = new List<Tile>();
        for (var kind = 0; kind < 34; kind++)
        {
            if (availability[kind] <= 0 || counts[kind] >= 4)
                continue;

            counts[kind]++;
            if (Shanten.Calculate(counts, called) < shanten)
            {
                waits.Add(TileHelpers.FromIndex(kind));
                ukeire += availability[kind];
            }

            counts[kind]--;
        }

        var winProbability = shanten == 0 ? (double)ukeire / wallEst : 0d;
        var reasoning = shanten == 0
            ? $"Tenpai — waiting on {FormatTiles(waits)} ({ukeire} live tiles)."
            : $"{shanten}-shanten with {ukeire} live improving tiles.";

        return new AnalysisResult
        {
            IsValid = true,
            BestDiscard = null,
            ShantenAfterDiscard = shanten,
            TenpaiWaits = shanten == 0 ? waits : [],
            WinProbability = winProbability,
            Ukeire = ukeire,
            Reasoning = reasoning,
        };
    }

    // Availability = 4 - copies in own closed hand - copies visible elsewhere
    // (discards + opponent melds from ctx, plus dora indicators and own melds).
    private static int[] BuildAvailability(int[] counts, Hand hand, AnalysisContext ctx)
    {
        var seen = new int[34];
        foreach (var tile in ctx.SeenTiles)
            seen[TileHelpers.ToIndex(tile)]++;
        foreach (var tile in ctx.DoraIndicators)
            seen[TileHelpers.ToIndex(tile)]++;
        foreach (var meld in hand.CalledMelds)
        {
            foreach (var tile in meld.Tiles)
                seen[TileHelpers.ToIndex(tile)]++;
        }

        var availability = new int[34];
        for (var kind = 0; kind < 34; kind++)
            availability[kind] = Math.Max(0, 4 - counts[kind] - seen[kind]);
        return availability;
    }

    // Best detected han over every wait completion; 0 means no wait yields a yaku.
    private static int BestHanAcrossWaits(
        Hand hand, AnalysisContext ctx, Tile discard, List<Tile> waits, CancellationToken ct)
    {
        var detector = new YakuDetector(ctx.Ruleset);
        var keptClosed = RemoveOneKind(hand.ClosedTiles, discard);

        var bestHan = 0;
        foreach (var wait in waits)
        {
            ct.ThrowIfCancellationRequested();
            var winning = new Hand
            {
                WinningTile = wait,
                WinMethod = WinMethod.Ron,
                SeatWind = ctx.SeatWind,
                RoundWind = ctx.RoundWind,
            };
            winning.ClosedTiles.AddRange(keptClosed);
            winning.ClosedTiles.Add(wait);
            winning.CalledMelds.AddRange(hand.CalledMelds);

            var decomposition = HandDecomposer.GetBestDecomposition(winning);
            if (decomposition is null)
                continue;

            var yaku = detector.Detect(winning, decomposition.Melds, decomposition.Pair, decomposition.Wait);
            var han = yaku.Sum(y => y.IsYakuman ? 13 : y.Han);
            if (han > bestHan)
                bestHan = han;
        }

        return bestHan;
    }

    // Dora copies + red fives kept in closed hand and melds after the discard.
    private static double EstimateKeptValue(Hand hand, Tile discard, List<Tile> doraValues)
    {
        var kept = RemoveOneKind(hand.ClosedTiles, discard);
        var all = kept.Concat(hand.CalledMelds.SelectMany(m => m.Tiles)).ToList();
        var dora = all.Count(t => doraValues.Any(d => TileHelpers.SameKind(d, t)));
        var aka = all.Count(t => t.IsRedFive);
        return dora + aka;
    }

    // When throwing a five, keep the red copy in hand if a plain one exists.
    private static Tile PickDiscardCopy(IReadOnlyList<Tile> closedTiles, Tile kind)
    {
        Tile? redCopy = null;
        foreach (var tile in closedTiles)
        {
            if (!TileHelpers.SameKind(tile, kind))
                continue;
            if (!tile.IsRedFive)
                return tile;
            redCopy = tile;
        }

        return redCopy ?? kind;
    }

    private static List<Tile> RemoveOneKind(IReadOnlyList<Tile> tiles, Tile kind)
    {
        var result = new List<Tile>(tiles.Count);
        var removed = false;
        // Remove the plain copy first so value estimates keep the red five.
        var removeIndex = -1;
        for (var i = 0; i < tiles.Count; i++)
        {
            if (TileHelpers.SameKind(tiles[i], kind) && (!tiles[i].IsRedFive || removeIndex < 0))
            {
                removeIndex = i;
                if (!tiles[i].IsRedFive)
                    break;
            }
        }

        for (var i = 0; i < tiles.Count; i++)
        {
            if (i == removeIndex && !removed)
            {
                removed = true;
                continue;
            }

            result.Add(tiles[i]);
        }

        return result;
    }

    // Returns the actual dora tile for a given indicator.
    // Number tiles wrap 9→1; winds wrap N→E; dragons wrap Chun→Haku.
    private static Tile DoraFromIndicator(Tile indicator)
    {
        return indicator.Suit switch
        {
            TileSuit.Man or TileSuit.Pin or TileSuit.Sou =>
                new Tile(indicator.Suit, indicator.Number == 9 ? 1 : indicator.Number + 1),
            TileSuit.Wind =>
                new Tile(TileSuit.Wind, indicator.Number == 4 ? 1 : indicator.Number + 1),
            TileSuit.Dragon =>
                new Tile(TileSuit.Dragon, indicator.Number == 3 ? 1 : indicator.Number + 1),
            _ => indicator,
        };
    }

    private static List<Tile> MaskToTiles(ulong mask)
    {
        var tiles = new List<Tile>();
        for (var kind = 0; kind < 34; kind++)
        {
            if ((mask & (1UL << kind)) != 0)
                tiles.Add(TileHelpers.FromIndex(kind));
        }

        return tiles;
    }

    private static int[] ToCounts(IEnumerable<Tile> tiles)
    {
        var counts = new int[34];
        foreach (var tile in tiles)
            counts[TileHelpers.ToIndex(tile)]++;
        return counts;
    }

    private static string FormatTiles(IReadOnlyCollection<Tile> tiles)
    {
        return tiles.Count == 0 ? "nothing visible" : string.Join(", ", tiles.Select(TileHelpers.GetDisplayName));
    }

    private static string BuildReasoning(DiscardOption best, bool riichi, double winProbability)
    {
        var name = TileHelpers.GetDisplayName(best.Eval.Discard);
        if (best.Eval.ShantenAfter <= 0)
        {
            var text = $"Discard {name} → tenpai. {best.Eval.Ukeire} live winning tiles ({FormatTiles(best.Waits)}). " +
                       $"~{winProbability:P1} draw chance from wall.";
            if (best.OpenYakuRisk)
                text += " Warning: no wait yields a yaku — the hand cannot win as-is.";
            else if (riichi)
                text += " Riichi is available.";
            return text;
        }

        return $"Discard {name} → {best.Eval.ShantenAfter}-shanten, {best.Eval.Ukeire} live improving tiles.";
    }
}

public sealed class AnalysisResult
{
    // False when the hand data is malformed/unstable; only Reasoning is meaningful then.
    public bool IsValid { get; init; }

    // Null for 13-tile hands (nothing to discard) and invalid results.
    public Tile? BestDiscard { get; init; }

    public int ShantenAfterDiscard { get; init; }

    public List<Tile> TenpaiWaits { get; init; } = [];

    public double WinProbability { get; init; }

    public int Ukeire { get; init; }

    public bool RiichiRecommended { get; init; }

    public IReadOnlyList<DiscardOption> Ranked { get; init; } = [];

    public string Reasoning { get; init; } = string.Empty;
}
