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
        => InferClaims(closedHand, claimed, calledMeldCount, allowChi) != ClaimOptions.None;

    // WHICH claims the closed hand supports on the offered tile. Pon, Kan and Chi are pure
    // tile arithmetic: no furiten, no yaku, no rule options. That makes them an oracle over
    // the game's own offer — if the game offers a Pon we cannot derive, our closed read is
    // missing tiles, and if we derive one it never offered, our read holds tiles that are
    // not there (docs/research/WIN_OFFERS_2026_09_22.md).
    //
    // Ron here is the winning SHAPE only. The game additionally requires a yaku and a
    // furiten-free wait, so "we see a win, the game offered none" is ordinary; the reverse
    // — the game offering a win on a hand we cannot complete — is not, and is the signature
    // of a drifted hand read.
    public static ClaimOptions InferClaims(IReadOnlyList<Tile> closedHand, Tile claimed, int calledMeldCount, bool allowChi = true)
    {
        ArgumentNullException.ThrowIfNull(closedHand);

        Span<int> counts = stackalloc int[34];
        foreach (var t in closedHand)
            counts[TileHelpers.ToIndex(t)]++;

        var k = TileHelpers.ToIndex(claimed);
        var options = ClaimOptions.None;
        if (counts[k] >= 2)
            options |= ClaimOptions.Pon;
        if (counts[k] >= 3)
            options |= ClaimOptions.Kan;

        if (allowChi && k < 27)
        {
            var n = k % 9; // position within the suit — shapes must not cross suits
            if ((n >= 2 && counts[k - 1] > 0 && counts[k - 2] > 0)
                || (n is >= 1 and <= 7 && counts[k - 1] > 0 && counts[k + 1] > 0)
                || (n <= 6 && counts[k + 1] > 0 && counts[k + 2] > 0))
                options |= ClaimOptions.Chi;
        }

        counts[k]++;
        if (Shanten.Calculate(counts, calledMeldCount) == -1)
            options |= ClaimOptions.Ron;
        return options;
    }
}

// What the closed hand alone says about an offered tile. Deliberately not the game's
// option list: it carries no yaku, furiten or riichi restriction.
[Flags]
public enum ClaimOptions
{
    None = 0,
    Chi = 1,
    Pon = 2,
    Kan = 4,
    Ron = 8,
}
