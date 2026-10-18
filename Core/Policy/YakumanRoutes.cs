namespace MahjongHater.Core.Policy;

// A yakuman the kept tiles are already shaped for, and how much of a yakuman's value a win on
// that route is worth on average (four concealed triplets is only a yakuman on tsumo or a
// tanki ron, so it counts at a discount).
public sealed record YakumanRoute(string Name, double ValueShare);

// Hand-building side of the yakuman fix. Before 3.3 every hand that was not yet tenpai was
// valued at its dora plus one han, so a big-three-dragons 1-shanten was cheap enough to fold
// and an open four-winds shape threw an honor pair for tile efficiency. These rules fire only
// when most of the shape is in hand, so ordinary hands are never revalued.
//
// Phoenix frequency (152 690 games): four concealed triplets 487, thirteen orphans 291, big
// three dragons 249, little four winds 85, all honors 21, then the rest in single digits.
public static class YakumanRoutes
{
    // `closed` = tiles still in hand after the discard; `melds` = our called/concealed melds.
    public static YakumanRoute? Of(IReadOnlyList<Tile> closed, IReadOnlyList<Meld> melds)
    {
        var counts = new int[34];
        foreach (var t in closed)
            counts[TileHelpers.ToIndex(t)]++;
        var meldCounts = new int[34];
        foreach (var m in melds)
            meldCounts[TileHelpers.ToIndex(m.Tiles[0])] += m.IsTriplet ? 3 : 0;
        var all = closed.Concat(melds.SelectMany(m => m.Tiles)).ToList();
        var isOpen = melds.Any(m => m.IsOpen);
        int Held(int kind) => counts[kind] + meldCounts[kind];
        var chi = melds.Any(m => m.Type == MeldType.Chi);

        // Big three dragons: two dragon sets and a pair of the third.
        var dragons = new[] { 31, 32, 33 }.Select(k => Math.Min(3, Held(k))).OrderByDescending(n => n).ToArray();
        if (dragons[0] >= 3 && dragons[1] >= 3 && dragons[2] >= 2)
            return new YakumanRoute("Daisangen", 1);

        // Four winds: three wind sets and a pair of the fourth (little), or better.
        var winds = new[] { 27, 28, 29, 30 }.Select(k => Math.Min(3, Held(k))).OrderByDescending(n => n).ToArray();
        if (winds[0] >= 3 && winds[1] >= 3 && winds[2] >= 3 && winds[3] >= 2)
            return new YakumanRoute("Shousuushii", 1);
        if (winds[0] >= 3 && winds[1] >= 3 && winds[2] >= 2 && winds[3] >= 2 && !chi)
            return new YakumanRoute("Shousuushii", 0.6);
        if (winds[0] >= 3 && winds[1] >= 2 && winds[2] >= 2 && winds[3] >= 2 && !chi)
            return new YakumanRoute("Shousuushii", 0.4);

        // All honors: nothing but honors, already in sets or pairs.
        if (all.Count >= 11 && all.All(t => t.IsHonor) && Enumerable.Range(27, 7).Count(k => Held(k) >= 2) >= 5)
            return new YakumanRoute("Tsuuiisou", 1);

        // All terminals / all green: nothing else held.
        if (all.Count >= 11 && all.All(t => t.IsTerminal) && Enumerable.Range(0, 27).Count(k => Held(k) >= 2) >= 4)
            return new YakumanRoute("Chinroutou", 1);
        if (all.Count >= 11 && !chi && all.All(IsGreen) && all.Count(t => t.Suit == TileSuit.Sou) >= 6)
            return new YakumanRoute("Ryuuiisou", 1);

        if (isOpen)
            return null;

        // Thirteen orphans: 11+ distinct terminal/honor kinds held (the rest of the hand junk).
        var orphans = Enumerable.Range(0, 34).Where(k => k >= 27 || k % 9 is 0 or 8).Count(k => counts[k] > 0);
        if (orphans >= 11)
            return new YakumanRoute("Kokushi Musou", 1);

        // Four concealed triplets: three concealed sets and two pairs, nothing else (tsumo
        // or tanki, so about half a yakuman on average).
        var sets = Enumerable.Range(0, 34).Count(k => counts[k] >= 3) + melds.Count(m => m.Type == MeldType.Ankan);
        var pairs = Enumerable.Range(0, 34).Count(k => counts[k] == 2);
        if (sets >= 3 && pairs >= 2 && sets * 3 + pairs * 2 >= closed.Count + 3 * melds.Count - 1)
            return new YakumanRoute("Suuankou", 0.5);

        // Nine gates: closed single suit already holding 1112345678999 minus at most one.
        foreach (var suit in new[] { 0, 9, 18 })
        {
            if (closed.Count < 12 || closed.Any(t => TileHelpers.ToIndex(t) / 9 * 9 != suit || t.IsHonor))
                continue;
            var missing = 0;
            int[] need = [3, 1, 1, 1, 1, 1, 1, 1, 3];
            for (var i = 0; i < 9; i++)
                missing += Math.Max(0, need[i] - counts[suit + i]);
            if (missing <= 1)
                return new YakumanRoute("Chuuren Poutou", 1);
        }

        return null;
    }

    public static int Points(bool isDealer) => isDealer ? 48000 : 32000;

    private static bool IsGreen(Tile t) => t switch
    {
        { Suit: TileSuit.Sou, Number: 2 or 3 or 4 or 6 or 8 } => true,
        { Suit: TileSuit.Dragon, Number: 2 } => true,
        _ => false,
    };
}
