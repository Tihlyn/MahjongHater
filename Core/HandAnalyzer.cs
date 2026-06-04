namespace MahjongHater.Core;

public sealed class HandAnalyzer
{
    public AnalysisResult Analyze(Hand hand)
    {
        ArgumentNullException.ThrowIfNull(hand);
        if (hand.ClosedTiles.Count == 0)
        {
            return new AnalysisResult
            {
                Reasoning = "No closed tiles available to analyze.",
            };
        }

        Tile? bestDiscard = null;
        var bestShanten = int.MaxValue;
        var bestUkeire = -1;
        List<Tile> bestWaits = [];
        var bestProbability = 0d;

        var allTiles = hand.AllTiles;
        foreach (var candidate in hand.ClosedTiles)
        {
            var remaining = RemoveOne(hand.ClosedTiles, candidate);
            var shanten = Shanten.Calculate(remaining);
            var useful = Shanten.GetUsefulTiles(remaining);
            var ukeire = useful.Sum(tile => Math.Max(0, 4 - TileHelpers.CountKind(allTiles, tile)));
            var wallRemaining = Math.Max(1, 70 - allTiles.Count);
            var probability = shanten == 0 ? (double)ukeire / wallRemaining : 0d;

            if (bestDiscard is null
                || shanten < bestShanten
                || (shanten == bestShanten && ukeire > bestUkeire)
                || (shanten == bestShanten && ukeire == bestUkeire && PreferDiscard(candidate, bestDiscard.Value)))
            {
                bestDiscard = candidate;
                bestShanten = shanten;
                bestUkeire = ukeire;
                bestWaits = shanten == 0 ? useful : [];
                bestProbability = probability;
            }
        }

        var chosenDiscard = bestDiscard ?? hand.ClosedTiles[0];
        return new AnalysisResult
        {
            BestDiscard = chosenDiscard,
            ShantenAfterDiscard = bestShanten,
            TenpaiWaits = bestWaits,
            WinProbability = bestProbability,
            Ukeire = Math.Max(0, bestUkeire),
            Reasoning = BuildReasoning(chosenDiscard, bestShanten, bestUkeire, bestWaits, bestProbability),
        };
    }

    private static bool PreferDiscard(Tile candidate, Tile currentBest)
    {
        if (candidate.IsRedFive != currentBest.IsRedFive)
        {
            return !candidate.IsRedFive;
        }

        return candidate.CompareTo(currentBest) < 0;
    }

    private static List<Tile> RemoveOne(IEnumerable<Tile> tiles, Tile target)
    {
        var result = new List<Tile>();
        var removed = false;
        foreach (var tile in tiles)
        {
            if (!removed && tile.Equals(target))
            {
                removed = true;
                continue;
            }

            result.Add(TileHelpers.Normalize(tile));
        }

        return result;
    }

    private static string BuildReasoning(Tile discard, int shanten, int ukeire, IReadOnlyCollection<Tile> waits, double probability)
    {
        if (shanten == 0)
        {
            var waitText = waits.Count == 0 ? "no visible waits" : string.Join(", ", waits.Select(TileHelpers.GetDisplayName));
            return $"Discard {TileHelpers.GetDisplayName(discard)} to stay in tenpai with {ukeire} ukeire ({waitText}), giving roughly {probability:P1} raw draw odds from the live wall.";
        }

        return $"Discard {TileHelpers.GetDisplayName(discard)} to reach {shanten}-shanten with {ukeire} improving tiles.";
    }
}

public sealed class AnalysisResult
{
    public Tile BestDiscard { get; set; }

    public int ShantenAfterDiscard { get; set; }

    public List<Tile> TenpaiWaits { get; set; } = [];

    public double WinProbability { get; set; }

    public int Ukeire { get; set; }

    public string Reasoning { get; set; } = string.Empty;
}
