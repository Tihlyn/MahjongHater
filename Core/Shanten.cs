namespace MahjongHater.Core;

// Per-discard evaluation produced by Shanten.EvaluateDiscards.
public sealed class DiscardEvaluation
{
    // Representative tile for the discarded kind (never the red copy — the analyzer
    // decides red-vs-plain when both are in hand).
    public required Tile Discard { get; init; }

    public int ShantenAfter { get; init; }

    // Bit k set ⇔ drawing kind k (34-kind index) lowers shanten after this discard.
    public ulong UsefulKindsMask { get; init; }

    // Σ availability over useful kinds.
    public int Ukeire { get; init; }

    // Weighted 2-step ukeire; 0 until the analyzer fills it for finalists.
    public long Ukeire2 { get; set; }
}

// Shanten calculation over 34-kind count arrays using per-group feasibility tables
// (see SuitSplitter). Pure static with no shared mutable state — safe to call from
// any thread concurrently.
public static class Shanten
{
    public static int Calculate(List<Tile> tiles, int calledMeldCount = 0)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        Span<int> counts = stackalloc int[34];
        foreach (var tile in tiles)
            counts[TileHelpers.ToIndex(tile)]++;
        return Calculate(counts, calledMeldCount);
    }

    public static int Calculate(ReadOnlySpan<int> counts, int calledMeldCount = 0)
    {
        var standard = CalculateStandard(counts, calledMeldCount);
        if (calledMeldCount > 0)
            return standard;
        var chiitoitsu = CalculateChiitoitsu(counts);
        var kokushi = CalculateKokushi(counts);
        return Math.Min(standard, Math.Min(chiitoitsu, kokushi));
    }

    public static List<Tile> GetUsefulTiles(List<Tile> tiles, int calledMeldCount = 0)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        Span<int> counts = stackalloc int[34];
        foreach (var tile in tiles)
            counts[TileHelpers.ToIndex(tile)]++;

        var current = Calculate(counts, calledMeldCount);
        var useful = new List<Tile>();
        for (var kind = 0; kind < 34; kind++)
        {
            if (counts[kind] >= 4)
                continue;

            counts[kind]++;
            if (Calculate(counts, calledMeldCount) < current)
                useful.Add(TileHelpers.FromIndex(kind));
            counts[kind]--;
        }

        return useful;
    }

    public static int CalculateAfterDiscard(List<Tile> tiles, Tile discard, int calledMeldCount = 0)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var next = new List<Tile>(tiles.Select(TileHelpers.Normalize));
        var discardIndex = next.FindIndex(tile => tile.Equals(discard));
        if (discardIndex < 0)
            discardIndex = next.FindIndex(tile => TileHelpers.SameKind(tile, discard));

        if (discardIndex < 0)
            throw new ArgumentException($"Tile {discard} does not exist in the hand.", nameof(discard));

        next.RemoveAt(discardIndex);
        return Calculate(next, calledMeldCount);
    }

    // Evaluates every distinct discard kind of a full (13+1 style) hand in one pass.
    // availability34[k] = copies of kind k still drawable (4 - in hand - seen outside).
    // Shared memo dedupes the heavy (discard a, draw b) / (discard b, draw a) overlap.
    public static List<DiscardEvaluation> EvaluateDiscards(
        int[] counts, int calledMeldCount, int[] availability34, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(availability34);

        var memo = new Dictionary<HandKey, int>();
        var results = new List<DiscardEvaluation>();
        for (var kind = 0; kind < 34; kind++)
        {
            if (counts[kind] == 0)
                continue;

            ct.ThrowIfCancellationRequested();
            counts[kind]--;
            var shanten = MemoCalculate(counts, calledMeldCount, memo);

            ulong mask = 0;
            var ukeire = 0;
            for (var draw = 0; draw < 34; draw++)
            {
                if (availability34[draw] <= 0 || counts[draw] >= 4)
                    continue;

                counts[draw]++;
                if (MemoCalculate(counts, calledMeldCount, memo) < shanten)
                {
                    mask |= 1UL << draw;
                    ukeire += availability34[draw];
                }

                counts[draw]--;
            }

            counts[kind]++;
            results.Add(new DiscardEvaluation
            {
                Discard = TileHelpers.FromIndex(kind),
                ShantenAfter = shanten,
                UsefulKindsMask = mask,
                Ukeire = ukeire,
            });
        }

        return results;
    }

    // Weighted 2-step ukeire for a 13-style hand at shanten ≥ 1: for each accepted draw,
    // the best next-level ukeire over all responses that keep the improved shanten,
    // weighted by the draw's availability. Higher = wider follow-up shapes.
    public static long ComputeUkeire2(
        int[] counts, int calledMeldCount, int[] availability34, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(availability34);

        var memo = new Dictionary<HandKey, int>();
        var baseShanten = MemoCalculate(counts, calledMeldCount, memo);
        if (baseShanten < 1)
            return 0;

        long total = 0;
        for (var draw = 0; draw < 34; draw++)
        {
            if (availability34[draw] <= 0 || counts[draw] >= 4)
                continue;

            counts[draw]++;
            if (MemoCalculate(counts, calledMeldCount, memo) < baseShanten)
            {
                ct.ThrowIfCancellationRequested();
                availability34[draw]--;
                var bestInner = 0;
                for (var response = 0; response < 34; response++)
                {
                    if (counts[response] == 0)
                        continue;

                    counts[response]--;
                    if (MemoCalculate(counts, calledMeldCount, memo) == baseShanten - 1)
                    {
                        var inner = 0;
                        for (var next = 0; next < 34; next++)
                        {
                            if (availability34[next] <= 0 || counts[next] >= 4)
                                continue;

                            counts[next]++;
                            if (MemoCalculate(counts, calledMeldCount, memo) == baseShanten - 2)
                                inner += availability34[next];
                            counts[next]--;
                        }

                        if (inner > bestInner)
                            bestInner = inner;
                    }

                    counts[response]++;
                }

                availability34[draw]++;
                total += (long)availability34[draw] * bestInner;
            }

            counts[draw]--;
        }

        return total;
    }

    private static int CalculateStandard(ReadOnlySpan<int> counts, int calledMeldCount)
    {
        var blockBudget = 4 - calledMeldCount;
        var baseShanten = 8 - (2 * calledMeldCount);
        if (blockBudget <= 0)
        {
            // All melds already called: only the pair matters.
            var hasPair = false;
            for (var kind = 0; kind < 34 && !hasPair; kind++)
                hasPair = counts[kind] >= 2;
            return baseShanten - (hasPair ? 1 : 0);
        }

        // Per-group feasibility tables (man / pin / sou / honors).
        Span<ushort> feasM = stackalloc ushort[5];
        Span<ushort> feasP = stackalloc ushort[5];
        Span<ushort> feasS = stackalloc ushort[5];
        Span<ushort> feasZ = stackalloc ushort[5];
        SuitSplitter.Decompose(counts.Slice(0, 9), true, feasM);
        SuitSplitter.Decompose(counts.Slice(9, 9), true, feasP);
        SuitSplitter.Decompose(counts.Slice(18, 9), true, feasS);
        SuitSplitter.Decompose(counts.Slice(27, 7), false, feasZ);

        // Pairwise merges reused by the head enumeration below.
        Span<ushort> mergeMP = stackalloc ushort[5];
        Span<ushort> mergeSZ = stackalloc ushort[5];
        Span<ushort> total = stackalloc ushort[5];
        Merge(feasM, feasP, mergeMP);
        Merge(feasS, feasZ, mergeSZ);
        Merge(mergeMP, mergeSZ, total);

        var bestValue = BestValue(total, blockBudget, hasHead: false);

        // Head (pair) enumeration: extract each candidate pair explicitly, re-decompose
        // only its group, and recombine. This replaces implicit pair/partial accounting
        // and makes the block-count constraint exact.
        Span<ushort> exGroup = stackalloc ushort[5];
        Span<ushort> headFeas = stackalloc ushort[5];
        Span<ushort> headTotal = stackalloc ushort[5];
        Span<int> group = stackalloc int[9];
        for (var kind = 0; kind < 34; kind++)
        {
            if (counts[kind] < 2)
                continue;

            int offset, length;
            bool runs;
            if (kind < 9) { offset = 0; length = 9; runs = true; Merge(feasP, mergeSZ, exGroup); }
            else if (kind < 18) { offset = 9; length = 9; runs = true; Merge(feasM, mergeSZ, exGroup); }
            else if (kind < 27) { offset = 18; length = 9; runs = true; Merge(mergeMP, feasZ, exGroup); }
            else { offset = 27; length = 7; runs = false; Merge(mergeMP, feasS, exGroup); }

            counts.Slice(offset, length).CopyTo(group);
            group[kind - offset] -= 2;
            SuitSplitter.Decompose(group[..length], runs, headFeas);
            Merge(exGroup, headFeas, headTotal);

            var value = BestValue(headTotal, blockBudget, hasHead: true);
            if (value > bestValue)
                bestValue = value;
        }

        return baseShanten - bestValue;
    }

    // Combines two feasibility tables: melds add (capped at 4 — both axes are downward
    // closed, so higher combinations are never better); partial counts sum as bitset
    // shifts (sumset).
    private static void Merge(ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b, Span<ushort> result)
    {
        result.Clear();
        for (var ma = 0; ma <= 4; ma++)
        {
            if (a[ma] == 0)
                continue;

            for (var mb = 0; ma + mb <= 4; mb++)
            {
                if (b[mb] == 0)
                    continue;

                result[ma + mb] |= SumsetBits(a[ma], b[mb]);
            }
        }
    }

    private static ushort SumsetBits(ushort a, ushort b)
    {
        var acc = 0;
        for (var p = 0; p <= 4; p++)
        {
            if ((a & (1 << p)) != 0)
                acc |= b << p;
        }

        return (ushort)(acc & 0x1FF);
    }

    // Max of 2m + min(p, budget - m) (+1 for the head) over the feasibility table.
    private static int BestValue(ReadOnlySpan<ushort> feas, int blockBudget, bool hasHead)
    {
        var best = 0;
        var maxMelds = Math.Min(4, blockBudget);
        for (var m = 0; m <= maxMelds; m++)
        {
            var bits = feas[m];
            if (bits == 0)
                continue;

            var maxP = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)bits);
            var value = (2 * m) + Math.Min(maxP, blockBudget - m) + (hasHead ? 1 : 0);
            if (value > best)
                best = value;
        }

        return best;
    }

    private static int CalculateChiitoitsu(ReadOnlySpan<int> counts)
    {
        var pairs = 0;
        var distinct = 0;
        for (var kind = 0; kind < 34; kind++)
        {
            if (counts[kind] >= 2) pairs++;
            if (counts[kind] > 0) distinct++;
        }

        return 6 - pairs + Math.Max(0, 7 - distinct);
    }

    private static int CalculateKokushi(ReadOnlySpan<int> counts)
    {
        ReadOnlySpan<int> orphanIndices = [0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33];
        var distinct = 0;
        var hasPair = false;
        foreach (var index in orphanIndices)
        {
            if (counts[index] > 0) distinct++;
            if (counts[index] > 1) hasPair = true;
        }

        return 13 - distinct - (hasPair ? 1 : 0);
    }

    private static int MemoCalculate(int[] counts, int calledMeldCount, Dictionary<HandKey, int> memo)
    {
        ulong lo = 0, hi = 0;
        for (var i = 0; i < 17; i++)
            lo |= (ulong)(uint)counts[i] << (i * 3);
        for (var i = 17; i < 34; i++)
            hi |= (ulong)(uint)counts[i] << ((i - 17) * 3);

        var key = new HandKey(lo, hi);
        if (memo.TryGetValue(key, out var cached))
            return cached;

        var result = Calculate(counts, calledMeldCount);
        memo[key] = result;
        return result;
    }

    private readonly record struct HandKey(ulong Lo, ulong Hi);
}
