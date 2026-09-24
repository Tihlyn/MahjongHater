namespace MahjongHater.Core.State;

// Reconstructs a missed meld from a closed-hand delta. Struct records retain counts
// and some tile identities, but not every meld's exact composition.
public static class MeldInference
{
    // Tiles in before that are not in after (multiset difference). Exact matches (red
    // five vs plain) are consumed first so a plain copy never steals a red one's match.
    public static List<Tile> Removed(IReadOnlyList<Tile> before, IReadOnlyList<Tile> after)
    {
        var remaining = new List<Tile>(after);
        var unmatched = new List<Tile>(before.Count);
        foreach (var t in before)
        {
            var idx = remaining.FindIndex(x => x.Equals(t));
            if (idx >= 0)
                remaining.RemoveAt(idx);
            else
                unmatched.Add(t);
        }

        var removed = new List<Tile>(4);
        foreach (var t in unmatched)
        {
            var idx = remaining.FindIndex(x => TileHelpers.SameKind(x, t));
            if (idx >= 0)
                remaining.RemoveAt(idx);
            else
                removed.Add(t);
        }

        return removed;
    }

    // before/after are closed hands (draw excluded). calledTile is the discard that was
    // claimed, null for a self-declared kan. Returns null when the delta is not a meld shape.
    public static Meld? Infer(IReadOnlyList<Tile> before, IReadOnlyList<Tile> after, Tile? calledTile)
    {
        var removed = Removed(before, after);
        if (removed.Count == 0 || removed.Count > 4)
            return null;

        if (calledTile is { } called)
        {
            var called0 = TileHelpers.Normalize(called);
            if (removed.Count == 2)
            {
                if (removed.All(t => TileHelpers.SameKind(t, called0)))
                    return new Meld(MeldType.Pon, [removed[0], removed[1], called], true);
                if (IsRun(removed[0], removed[1], called0))
                    return new Meld(MeldType.Chi, [.. new[] { removed[0], removed[1], called }.OrderBy(TileHelpers.Normalize)], true);
                return null;
            }

            if (removed.Count == 3 && removed.All(t => TileHelpers.SameKind(t, called0)))
                return new Meld(MeldType.Daiminkan, [removed[0], removed[1], removed[2], called], true);

            return null;
        }

        // Self-declared: four copies leave the closed hand at once.
        if (removed.Count == 4 && removed.All(t => TileHelpers.SameKind(t, removed[0])))
            return new Meld(MeldType.Ankan, [.. removed], false);

        return null;
    }

    private static bool IsRun(Tile a, Tile b, Tile c)
    {
        if (a.IsHonor || b.IsHonor || c.IsHonor)
            return false;
        if (a.Suit != b.Suit || b.Suit != c.Suit)
            return false;
        var n = new[] { a.Number, b.Number, c.Number };
        Array.Sort(n);
        return n[1] == n[0] + 1 && n[2] == n[1] + 1;
    }

    // Stable identity used to dedupe the repeated atkType=74 payloads and hand-delta
    // inference of the same meld.
    public static string Signature(Meld meld)
        => $"{meld.Type}:{string.Join(",", meld.Tiles.OrderBy(t => t).Select(t => t.ToString()))}";
}
