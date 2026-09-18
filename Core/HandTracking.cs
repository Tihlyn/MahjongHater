namespace MahjongHater.Core;

// Pure helpers for maintaining the tracked closed-hand list.
// Free of Dalamud types so the lifecycle rules are unit-testable.
public static class HandTracking
{
    // Removes exactly one tile matching the discard: exact match (including red five)
    // first, then any same-kind copy. Returns false when no copy exists.
    public static bool RemoveOneTile(List<Tile> hand, Tile discarded)
    {
        ArgumentNullException.ThrowIfNull(hand);

        var index = hand.FindIndex(t => t.Equals(discarded));
        if (index < 0)
            index = hand.FindIndex(t => TileHelpers.SameKind(t, discarded));
        if (index < 0)
            return false;

        hand.RemoveAt(index);
        return true;
    }

    // Maximum closed tiles (including the draw) given declared melds.
    // A kan consumes 4 tiles but grants a replacement draw, so it still frees 3 slots.
    public static int MaxClosedTiles(int calledMeldCount) => 14 - (3 * calledMeldCount);

    // A seat-0 discard event that lands just before a chi/pon window is the game's
    // announcement of the claimable OPPONENT discard, not a real local discard
    // (2026-07-05: "local discard 7m" booked shortly before a chi window offering that
    // 7m). When the window's ghost slot is readable it must name the same tile; with
    // no ghost read the time window alone decides, so it is kept tighter.
    public static bool IsPrePromptAnnouncement(Tile lastLocalDiscard, double msSinceDiscard, Tile? ghostTile)
        => ghostTile is { } ghost
            ? TileHelpers.SameKind(lastLocalDiscard, ghost) && msSinceDiscard < 1500
            : msSinceDiscard < 700;

    // Distinguishes a claim window's ghost-slot jump from the own-turn 14th tile when
    // the prompt labels are stuck (count=109 mode never clears them, so an identical
    // follow-up window produces no label edge — live 2026-07-05). A claim window
    // follows a discard with NO local draw after it and goes stale within seconds;
    // an own-turn hand jump always has a fresher draw event than the last discard.
    // (Draws do not reliably fire type-6, so this alone is not sufficient — combine
    // with HasAnyLegalCall.)
    public static bool IsClaimWindowSlotJump(double msSinceDiscardEvent, double msSinceLocalDraw)
        => msSinceDiscardEvent < 5000 && msSinceDiscardEvent < msSinceLocalDraw;

    // True when the closed hand can legally call the claimed tile at all: pon/kan
    // (two/three copies in hand), chi (suit neighbors forming a run), or ron (the claim
    // completes the hand). The game only opens a claim window when one of these holds —
    // a "window" failing all of them belongs to another seat (call announcement /
    // mirrored decision) or is a stale panel, and must never activate the local call UI
    // (live 2026-07-05: "Chi" activations while other seats called; a stuck Chi/Pass
    // panel over an own-turn drawn 8s with zero souzu in hand).
    public static bool HasAnyLegalCall(IReadOnlyList<Tile> closedHand, Tile claimed, int calledMeldCount)
    {
        ArgumentNullException.ThrowIfNull(closedHand);

        Span<int> counts = stackalloc int[34];
        foreach (var t in closedHand)
            counts[TileHelpers.ToIndex(t)]++;

        var k = TileHelpers.ToIndex(claimed);
        if (counts[k] >= 2)
            return true; // pon (kan at three)

        if (k < 27)
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
