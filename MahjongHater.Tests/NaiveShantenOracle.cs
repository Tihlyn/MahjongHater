using MahjongHater.Core;

namespace MahjongHater.Tests;

// Frozen copy of the pre-refactor recursive-backtracking Shanten implementation.
// Used as a differential-testing oracle for the fast per-suit rewrite. Slow but
// exhaustive; validated against the curated reference table before being trusted.
internal static class NaiveShantenOracle
{
    public static int Calculate(List<Tile> tiles, int calledMeldCount = 0)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var normalized = tiles.Select(TileHelpers.Normalize).ToList();
        var counts = ToCounts(normalized);
        var standard = CalculateStandard(counts, calledMeldCount);
        if (calledMeldCount > 0)
            return standard;
        var chiitoitsu = CalculateChiitoitsu(counts);
        var kokushi = CalculateKokushi(counts);
        return Math.Min(standard, Math.Min(chiitoitsu, kokushi));
    }

    private static int CalculateStandard(int[] counts, int calledMeldCount)
    {
        var baseShanten = 8 - 2 * calledMeldCount;
        var working = (int[])counts.Clone();
        var best = baseShanten;
        Search(working, 0, 0, 0, false, ref best, calledMeldCount);
        for (var i = 0; i < working.Length; i++)
        {
            if (working[i] < 2)
                continue;

            working[i] -= 2;
            Search(working, 0, 0, 0, true, ref best, calledMeldCount);
            working[i] += 2;
        }

        return best;
    }

    private static void Search(int[] counts, int index, int melds, int partials, bool hasPair, ref int best, int calledMeldCount)
    {
        while (index < counts.Length && counts[index] == 0)
            index++;

        var remainingMelds = Math.Max(0, (4 - calledMeldCount) - melds);
        var baseShanten = 8 - 2 * calledMeldCount;

        if (index >= counts.Length)
        {
            if (partials > remainingMelds)
                partials = remainingMelds;

            var shanten = baseShanten - (melds * 2) - partials - (hasPair ? 1 : 0);
            if (shanten < best)
                best = shanten;

            return;
        }

        var remainingSum = 0;
        for (var i = index; i < counts.Length; i++)
            remainingSum += counts[i];

        // Admissible prune (fixes the original code's bound, which ignored that remaining
        // tiles can still form melds worth -2 each and so cut off valid decompositions):
        // maximize 2f + partial credit over every feasible future-meld count f.
        var bestReduction = 0;
        var maxFutureMelds = Math.Min(remainingMelds, remainingSum / 3);
        for (var f = 0; f <= maxFutureMelds; f++)
        {
            var partialCredit = Math.Min(partials + ((remainingSum - (3 * f)) / 2), remainingMelds - f);
            var reduction = (2 * f) + Math.Max(0, partialCredit);
            if (reduction > bestReduction)
                bestReduction = reduction;
        }

        var theoreticalBest = baseShanten - (melds * 2) - bestReduction - 1;
        if (theoreticalBest >= best)
            return;

        if (counts[index] >= 3)
        {
            counts[index] -= 3;
            Search(counts, index, melds + 1, partials, hasPair, ref best, calledMeldCount);
            counts[index] += 3;
        }

        if (index < 27 && index % 9 <= 6 && counts[index + 1] > 0 && counts[index + 2] > 0)
        {
            counts[index]--;
            counts[index + 1]--;
            counts[index + 2]--;
            Search(counts, index, melds + 1, partials, hasPair, ref best, calledMeldCount);
            counts[index]++;
            counts[index + 1]++;
            counts[index + 2]++;
        }

        if (counts[index] >= 2)
        {
            counts[index] -= 2;
            Search(counts, index, melds, partials + 1, hasPair, ref best, calledMeldCount);
            counts[index] += 2;
        }

        if (index < 27 && index % 9 <= 7 && counts[index + 1] > 0)
        {
            counts[index]--;
            counts[index + 1]--;
            Search(counts, index, melds, partials + 1, hasPair, ref best, calledMeldCount);
            counts[index]++;
            counts[index + 1]++;
        }

        if (index < 27 && index % 9 <= 6 && counts[index + 2] > 0)
        {
            counts[index]--;
            counts[index + 2]--;
            Search(counts, index, melds, partials + 1, hasPair, ref best, calledMeldCount);
            counts[index]++;
            counts[index + 2]++;
        }

        counts[index]--;
        Search(counts, index, melds, partials, hasPair, ref best, calledMeldCount);
        counts[index]++;
    }

    private static int CalculateChiitoitsu(int[] counts)
    {
        var pairs = counts.Count(count => count >= 2);
        var distinct = counts.Count(count => count > 0);
        return 6 - pairs + Math.Max(0, 7 - distinct);
    }

    private static int CalculateKokushi(int[] counts)
    {
        int[] orphanIndices = [0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33];
        var distinct = orphanIndices.Count(index => counts[index] > 0);
        var pair = orphanIndices.Any(index => counts[index] > 1) ? 1 : 0;
        return 13 - distinct - pair;
    }

    private static int[] ToCounts(IEnumerable<Tile> tiles)
    {
        var counts = new int[34];
        foreach (var tile in tiles)
            counts[TileHelpers.ToIndex(tile)]++;

        return counts;
    }
}
