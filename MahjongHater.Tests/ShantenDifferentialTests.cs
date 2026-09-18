using System.Diagnostics;
using MahjongHater.Core;
using Xunit;

namespace MahjongHater.Tests;

// Differential testing: the fast per-suit implementation must agree with the frozen
// naive oracle on thousands of randomized hands across biased distributions.
public class ShantenDifferentialTests
{
    private static List<Tile> DrawHand(Random rng, int size, Func<int, int> weight)
    {
        // Weighted sampling without replacement from the 136-tile wall.
        var pool = new List<int>(136);
        for (var kind = 0; kind < 34; kind++)
        {
            for (var copy = 0; copy < 4; copy++)
            {
                var w = weight(kind);
                for (var i = 0; i < w; i++)
                    pool.Add(kind);
            }
        }

        var counts = new int[34];
        var tiles = new List<Tile>(size);
        while (tiles.Count < size)
        {
            var kind = pool[rng.Next(pool.Count)];
            if (counts[kind] >= 4)
                continue;

            counts[kind]++;
            tiles.Add(TileHelpers.FromIndex(kind));
        }

        return tiles;
    }

    private static void RunDifferential(int seed, int iterations, Func<int, int> weight)
    {
        var rng = new Random(seed);
        for (var i = 0; i < iterations; i++)
        {
            var called = rng.Next(4);            // 0-3 called melds
            var size = (13 - (3 * called)) + rng.Next(2); // 13/14-style hand for that meld count
            var tiles = DrawHand(rng, size, weight);

            var expected = NaiveShantenOracle.Calculate(tiles, called);
            var actual = Shanten.Calculate(tiles, called);
            Assert.True(expected == actual,
                $"Mismatch for [{string.Join(" ", tiles.OrderBy(t => t))}] called={called}: oracle={expected}, fast={actual}");
        }
    }

    [Fact]
    public void Matches_oracle_on_uniform_hands()
        => RunDifferential(seed: 1337, iterations: 8000, weight: _ => 1);

    [Fact]
    public void Matches_oracle_on_flush_biased_hands()
        => RunDifferential(seed: 42, iterations: 6000, weight: kind => kind < 9 ? 8 : 1);

    [Fact]
    public void Matches_oracle_on_honor_heavy_hands()
        => RunDifferential(seed: 7, iterations: 6000, weight: kind => kind >= 27 ? 8 : 1);

    [Fact]
    public void Calculate_is_fast_on_pathological_flush_hands()
    {
        // Pure-flush shapes were the exponential blow-up case for the old search.
        var hands = new[]
        {
            TestTiles.Parse("11123455678999m"),
            TestTiles.Parse("12345678999991m"),
            TestTiles.Parse("11112222333344m"),
            TestTiles.Parse("1122334455667m"),
        };

        // Warm-up (JIT + suit cache).
        foreach (var hand in hands)
            Shanten.Calculate(hand);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            foreach (var hand in hands)
                Shanten.Calculate(hand);
        }

        sw.Stop();
        var perCall = sw.Elapsed.TotalMilliseconds / (100.0 * hands.Length);
        Assert.True(perCall < 1.0, $"Calculate took {perCall:F3} ms per call (budget 1 ms).");
    }

    [Fact]
    public void EvaluateDiscards_is_fast()
    {
        var tiles = TestTiles.Parse("1123455678999m1z");
        var counts = new int[34];
        foreach (var t in tiles)
            counts[TileHelpers.ToIndex(t)]++;
        var availability = new int[34];
        for (var k = 0; k < 34; k++)
            availability[k] = 4 - counts[k];

        Shanten.EvaluateDiscards(counts, 0, availability); // warm-up

        var sw = Stopwatch.StartNew();
        var evaluations = Shanten.EvaluateDiscards(counts, 0, availability);
        sw.Stop();

        Assert.NotEmpty(evaluations);
        Assert.True(sw.Elapsed.TotalMilliseconds < 250, $"EvaluateDiscards took {sw.Elapsed.TotalMilliseconds:F1} ms (budget 250 ms).");
    }

    [Fact]
    public void EvaluateDiscards_matches_per_discard_oracle()
    {
        var rng = new Random(99);
        for (var i = 0; i < 300; i++)
        {
            var tiles = DrawHand(rng, 14, _ => 1);
            var counts = new int[34];
            foreach (var t in tiles)
                counts[TileHelpers.ToIndex(t)]++;
            var availability = new int[34];
            for (var k = 0; k < 34; k++)
                availability[k] = 4 - counts[k];

            var evaluations = Shanten.EvaluateDiscards(counts, 0, availability);
            foreach (var eval in evaluations)
            {
                var remaining = new List<Tile>(tiles);
                remaining.Remove(remaining.First(t => TileHelpers.SameKind(t, eval.Discard)));
                Assert.Equal(NaiveShantenOracle.Calculate(remaining), eval.ShantenAfter);
            }
        }
    }
}
