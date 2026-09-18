using MahjongHater.Core;

namespace MahjongHater.Tests;

// Parses compact hand notation: "123m406p789s1122z" → list of tiles.
// Digits accumulate until a suit letter flushes them; '0' = red five in m/p/s.
internal static class TestTiles
{
    public static List<Tile> Parse(string notation)
    {
        var tiles = new List<Tile>();
        var pending = new List<int>();
        foreach (var c in notation)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (char.IsDigit(c))
            {
                pending.Add(c - '0');
                continue;
            }

            foreach (var digit in pending)
                tiles.Add(Tile.Parse($"{digit}{c}"));
            pending.Clear();
        }

        if (pending.Count > 0)
            throw new FormatException($"Trailing digits without a suit in '{notation}'.");
        return tiles;
    }
}
