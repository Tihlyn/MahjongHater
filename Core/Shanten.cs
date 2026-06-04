namespace MahjongHater.Core;

public static class Shanten
{
    public static int Calculate(List<Tile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var normalized = tiles.Select(TileHelpers.Normalize).ToList();
        var counts = ToCounts(normalized);
        var standard = CalculateStandard(counts);
        var chiitoitsu = CalculateChiitoitsu(counts);
        var kokushi = CalculateKokushi(counts);
        return Math.Min(standard, Math.Min(chiitoitsu, kokushi));
    }

    public static List<Tile> GetUsefulTiles(List<Tile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var current = Calculate(tiles);
        var useful = new List<Tile>();
        foreach (var tile in TileHelpers.AllTileTypes)
        {
            if (TileHelpers.CountKind(tiles, tile) >= 4)
            {
                continue;
            }

            var next = new List<Tile>(tiles) { tile };
            if (Calculate(next) < current)
            {
                useful.Add(tile);
            }
        }

        return useful.OrderBy(t => t).ToList();
    }

    public static int CalculateAfterDiscard(List<Tile> tiles, Tile discard)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var next = new List<Tile>(tiles.Select(TileHelpers.Normalize));
        var discardIndex = next.FindIndex(tile => tile.Equals(discard));
        if (discardIndex < 0)
        {
            discardIndex = next.FindIndex(tile => TileHelpers.SameKind(tile, discard));
        }

        if (discardIndex < 0)
        {
            throw new ArgumentException($"Tile {discard} does not exist in the hand.", nameof(discard));
        }

        next.RemoveAt(discardIndex);
        return Calculate(next);
    }

    private static int CalculateStandard(int[] counts)
    {
        var working = (int[])counts.Clone();
        var best = 8;
        Search(working, 0, 0, 0, false, ref best);
        for (var i = 0; i < working.Length; i++)
        {
            if (working[i] < 2)
            {
                continue;
            }

            working[i] -= 2;
            Search(working, 0, 0, 0, true, ref best);
            working[i] += 2;
        }

        return best;
    }

    private static void Search(int[] counts, int index, int melds, int partials, bool hasPair, ref int best)
    {
        while (index < counts.Length && counts[index] == 0)
        {
            index++;
        }

        if (index >= counts.Length)
        {
            if (partials > 4 - melds)
            {
                partials = 4 - melds;
            }

            var shanten = 8 - (melds * 2) - partials - (hasPair ? 1 : 0);
            if (shanten < best)
            {
                best = shanten;
            }

            return;
        }

        var remainingSum = 0;
        for (var i = index; i < counts.Length; i++)
        {
            remainingSum += counts[i];
        }

        var theoreticalBest = 8 - (melds * 2) - Math.Min(4 - melds, partials + (remainingSum / 2)) - (hasPair ? 1 : 0);
        if (theoreticalBest >= best)
        {
            return;
        }

        if (counts[index] >= 3)
        {
            counts[index] -= 3;
            Search(counts, index, melds + 1, partials, hasPair, ref best);
            counts[index] += 3;
        }

        if (index < 27 && index % 9 <= 6 && counts[index + 1] > 0 && counts[index + 2] > 0)
        {
            counts[index]--;
            counts[index + 1]--;
            counts[index + 2]--;
            Search(counts, index, melds + 1, partials, hasPair, ref best);
            counts[index]++;
            counts[index + 1]++;
            counts[index + 2]++;
        }

        if (counts[index] >= 2)
        {
            counts[index] -= 2;
            Search(counts, index, melds, partials + 1, hasPair, ref best);
            counts[index] += 2;
        }

        if (index < 27 && index % 9 <= 7 && counts[index + 1] > 0)
        {
            counts[index]--;
            counts[index + 1]--;
            Search(counts, index, melds, partials + 1, hasPair, ref best);
            counts[index]++;
            counts[index + 1]++;
        }

        if (index < 27 && index % 9 <= 6 && counts[index + 2] > 0)
        {
            counts[index]--;
            counts[index + 2]--;
            Search(counts, index, melds, partials + 1, hasPair, ref best);
            counts[index]++;
            counts[index + 2]++;
        }

        counts[index]--;
        Search(counts, index, melds, partials, hasPair, ref best);
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
        {
            counts[TileHelpers.ToIndex(tile)]++;
        }

        return counts;
    }
}
