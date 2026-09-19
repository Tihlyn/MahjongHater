namespace MahjongHater.Core;

// Pure hand-size and call-legality rules used by the tracker.
// Free of Dalamud types so they are unit-testable.
public static class HandTracking
{
    // Maximum closed tiles (including the draw) given declared melds.
    // A kan consumes 4 tiles but grants a replacement draw, so it still frees 3 slots.
    public static int MaxClosedTiles(int calledMeldCount) => 14 - (3 * calledMeldCount);

    // True when the closed hand can legally call the claimed tile at all: pon/kan
    // (two/three copies in hand), chi (suit neighbors forming a run — only from the
    // seat to our left, so callers pass allowChi=false for other seats), or ron (the
    // claim completes the hand). The game only opens a claim window when one of these
    // holds — a "window" failing all of them belongs to another seat (call
    // announcement / mirrored decision) or is a stale panel, and must never activate
    // the local call UI (see docs/EMJ_ADDON_REFERENCE.md, "Call window lifecycle").
    public static bool HasAnyLegalCall(IReadOnlyList<Tile> closedHand, Tile claimed, int calledMeldCount, bool allowChi = true)
    {
        ArgumentNullException.ThrowIfNull(closedHand);

        Span<int> counts = stackalloc int[34];
        foreach (var t in closedHand)
            counts[TileHelpers.ToIndex(t)]++;

        var k = TileHelpers.ToIndex(claimed);
        if (counts[k] >= 2)
            return true; // pon (kan at three)

        if (allowChi && k < 27)
        {
            var n = k % 9; // position within the suit — shapes must not cross suits
            if ((n >= 2 && counts[k - 1] > 0 && counts[k - 2] > 0)
                || (n is >= 1 and <= 7 && counts[k - 1] > 0 && counts[k + 1] > 0)
                || (n <= 6 && counts[k + 1] > 0 && counts[k + 2] > 0))
                return true; // chi
        }

        // Ron: the claimed tile completes the hand.
        counts[k]++;
        return Shanten.Calculate(counts, calledMeldCount) == -1;
    }
}
