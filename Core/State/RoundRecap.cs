namespace MahjongHater.Core.State;

// One yaku exactly as the game named it, with the han it awarded and the blurb the recap
// shows on hover. Dora appear here as named entries ("Ura Dora" — "Awards bonus han but is
// not a yaku by itself"), which is not how our YakuDetector models them.
public sealed record ScoredYaku(string Name, int Han, string Description)
{
    public bool IsDoraLike => this.Name.Contains("Dora", StringComparison.OrdinalIgnoreCase);
}

// The game's own account of how a hand scored, read off the round recap.
public sealed record RoundRecap(
    string WinMethod,               // "Called Ron" / "Called Tsumo" / "Draw"
    string Score,                   // "40 Fu 2 Han [Mangan]" as printed
    int Fu,
    int Han,
    IReadOnlyList<Tile> Hand,       // the winner's hand, without the winning tile
    Tile? WinningTile,
    IReadOnlyList<ScoredYaku> Yaku,
    IReadOnlyList<int> SeatDeltas,  // per seat, already multiplied to points
    IReadOnlyList<Tile> Dora,
    IReadOnlyList<Tile> UraDora)
{
    // Han the game attributes to named yaku that are not dora.
    public int YakuHan => this.Yaku.Where(y => !y.IsDoraLike).Sum(y => y.Han);

    public int DoraHan => this.Yaku.Where(y => y.IsDoraLike).Sum(y => y.Han);

    public IEnumerable<string> YakuNames => this.Yaku.Where(y => !y.IsDoraLike).Select(y => y.Name);
}

// Decodes the state-29 round recap. The layout was read off the live client on 2026-09-23
// and confirmed by reconstructing a valid winning hand from it, rather than by assuming an
// offset table (docs/research/ADDON_PROTOCOL_2026_09_23.md).
//
// This is the game stating, for every win in every match: the winner's actual hand, the yaku
// it awarded with per-yaku han, the fu/han total, the dora, and what each seat paid. It is
// the only place the game tells us its own reasoning, so it is the check on ours.
public static class RoundRecapReader
{
    // Three parallel 18-slot arrays behind the count at [42]: names, han, descriptions.
    private const int YakuCount = 42;
    private const int YakuNames = 43;
    private const int YakuHanValues = 61;
    private const int YakuDescriptions = 79;
    private const int YakuSlots = 18;

    private const int HandCount = 14;
    private const int HandFirst = 24;
    private const int WinningTile = 41;
    private const int DoraCount = 97;
    private const int DoraFirst = 98;
    private const int UraCount = 103;
    private const int UraFirst = 104;

    public static RoundRecap? Read(AtkFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.EventType != 29)
            return null;

        // A draw carries no hand and no yaku; only a scored win is worth comparing.
        var score = frame.Str(6);
        if (string.IsNullOrWhiteSpace(score) || !TryFuHan(score, out var fu, out var han))
            return null;

        var hand = new List<Tile>(14);
        var declared = frame.IsInt(HandCount) ? Math.Clamp(frame.Int(HandCount), 0, 14) : 0;
        for (var i = 0; i < declared; i++)
        {
            if (frame.IsInt(HandFirst + i) && TileHelpers.TryTileFromIconId(frame.Int(HandFirst + i), out var tile))
                hand.Add(tile);
        }

        var yaku = new List<ScoredYaku>(4);
        var count = frame.IsInt(YakuCount) ? Math.Clamp(frame.Int(YakuCount), 0, YakuSlots) : 0;
        for (var i = 0; i < count; i++)
        {
            var name = frame.Str(YakuNames + i)?.Trim();
            if (string.IsNullOrEmpty(name))
                continue;
            yaku.Add(new ScoredYaku(name, ParseHan(frame.Str(YakuHanValues + i)),
                frame.Str(YakuDescriptions + i)?.Trim() ?? string.Empty));
        }

        return new RoundRecap(
            frame.Str(5)?.Trim() ?? string.Empty,
            score.Trim(), fu, han, hand,
            frame.IsInt(WinningTile) && TileHelpers.TryTileFromIconId(frame.Int(WinningTile), out var won) ? won : null,
            yaku,
            [.. Enumerable.Range(1, 4).Select(i => frame.Int(i) * 100)],
            Indicators(frame, DoraCount, DoraFirst),
            Indicators(frame, UraCount, UraFirst));
    }

    private static List<Tile> Indicators(AtkFrame frame, int countIndex, int first)
    {
        var tiles = new List<Tile>(4);
        var count = frame.IsInt(countIndex) ? Math.Clamp(frame.Int(countIndex), 0, 5) : 0;
        for (var i = 0; i < count; i++)
        {
            if (frame.IsInt(first + i) && TileHelpers.TryTileFromIconId(frame.Int(first + i), out var tile))
                tiles.Add(tile);
        }

        return tiles;
    }

    // "40 Fu 2 Han", "25 Fu 5 Han Mangan" — the same line the scoring oracle already checks.
    private static bool TryFuHan(string text, out int fu, out int han)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text,
            @"(\d+)\s*Fu\s+(\d+)\s*Han", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        fu = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        han = m.Success ? int.Parse(m.Groups[2].Value) : 0;
        return m.Success && fu is >= 20 and <= 140 && han is >= 1 and <= 52;
    }

    // "1 Han", "2 Han"; a yakuman entry may print something else, so an unparsed value is 0
    // rather than a guess.
    private static int ParseHan(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var han) && han is >= 0 and <= 52 ? han : 0;
    }
}
