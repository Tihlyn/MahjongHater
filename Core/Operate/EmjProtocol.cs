namespace MahjongHater.Core.Operate;

// The Emj addon's input protocol, read off the game's own FireCallback traffic during a full
// manually-played match on 2026-09-23 (docs/research/ADDON_PROTOCOL_2026_09_23.md; raw capture
// in artifacts/capture/). Every command below was observed being sent BY THE GAME when a human
// performed the action, and each carries the event it was paired with and how many times it
// fired.
//
// What that does and does not establish: the capture proves the PAYLOAD of each command and
// which event accompanied it. It does not prove that replaying the callback alone reproduces
// the whole action, because every input command in the capture arrived WITH an event. Where we
// know a bare callback is enough, it is because live play showed it, and the comment says so.
// Counts here are paired by the capture's frame number; an earlier revision paired by
// millisecond and manufactured a finding out of the rounding.
//
// The distinction that matters is COMMAND vs NOTIFICATION. The addon fires callbacks itself on
// state transitions, and replaying one of those as if it were input is how another plugin
// reportedly parked the addon in state 32 (docs/research/ADDON_INTERACTION_PLAN_2026_09_22.md).
// Only the three commands here were seen to follow a human's click; the notifications are
// named so they can never be mistaken for commands.
public static class EmjProtocol
{
    // ─────────────────────────────── commands ───────────────────────────────

    // [7, slot] — discard. Paired with ButtonClick param=slot+15 on node 9, 100 times out of
    // 100. There is no unpaired discard anywhere in the capture.
    //
    // The bare command IS sufficient — v2.3.0 has discarded through it for whole live matches
    // — but that is known from play, not from here. A previous revision of this comment claimed
    // four riichi auto-discards fired with no event at all and called it "the clearest proof
    // that the callback is the command"; those four pairs are 23-51 microseconds apart and
    // straddle a millisecond boundary, so pairing by millisecond dropped exactly them. The
    // finding was an artifact of the aggregation. Frame-paired counts now, and the rule is that
    // a capture establishes a payload, while sufficiency needs an observed outcome.
    public const int Discard = 7;

    // [11, row] — answer the call window by row index. Paired with ListItemClick param=0 on
    // node 3, 28 of 28 in the timeline (30 fires in cb_Emj.json once repeats are counted). Row
    // indices come from the live item table, never from a label.
    public const int CallRow = 11;

    // [12, shape] — pick a chi shape while the state-25 chooser is up. ONE observation only
    // (cb_chooser.json, 06:28:26.530, shape index 0); no other index and no cancel was ever
    // captured, so the index mapping below is a single sample, not a measured range.
    // Measured 2026-09-23:
    // accepting Chi from the call list ([11, row]) refreshes the addon into state 25, and the
    // shape button then fires [12, index] paired with ButtonClick param=9+index on node 5+index
    // of the chooser panel (1/46/52). Index is 0-based and matches the order the shapes appear
    // in the state-25 values, which is the order our CallShapes already uses.
    public const int ChiShape = 12;

    // [14] — advance the end-of-round recap. Paired with ButtonClick param=7 on node 97, 10 of
    // 10; this is what finally identifies node 97, which the plugin had been pressing by trial
    // and error without ever reading it.
    public const int RecapNext = 14;

    // ──────────────────────────── pointer handshake ─────────────────────────

    // [15, tileIconId] — "the pointer is on this tile". Fired by the addon from its own cursor
    // polling with NO AtkEvent behind it (115 of 118 fires), so it cannot be produced by
    // synthesising a MouseOver. A human discard is [15, icon] then [7, slot]; ours sends only
    // [7, slot], leaving the addon's idea of the pointed-at tile untouched.
    //
    // We do NOT send it. The honest reason is that discards work without it, so there is no
    // problem to solve. It was previously described here as "the outstanding lead on the table
    // going unhoverable" — that was one incident correlated with one hover-then-discard
    // sequence, which is not evidence. Neither adding nor banning this belongs in the code
    // until the freeze is diagnosed with same-frame pointer/bounds/collision/focus evidence.
    public const int Pointer = 15;

    // ───────────────────────── notifications: never replay ──────────────────

    public const int HandStarted = 9;    // <- TimelineActiveLabelChanged param=33 node=128, x10
    public const int HandEnded = 17;     // <- TimelineActiveLabelChanged param=36 node=54, x10
    public const int Dismissed = -1;
    public const int Closed = -2;

    // UNIDENTIFIED. One bare [10] at 06:11:03.229 in cb_chooser.json, with updateState=true and
    // nothing else in the capture to say what produced it or what it did. It is listed here so
    // it stops being invisible, and it is deliberately NOT in IsNotification: we do not know
    // that it is a notification, only that we have never accounted for it. Never send it.
    public const int Unidentified10 = 10;

    // ──────────────────── ending a finished match ───────────────────────────

    // Not a button in Emj. When the last recap is advanced the table hides itself and
    // EmjTotalResult opens; closing THAT ends the match:
    //   Emj PostHide -> EmjTotalResult PostShow -> ButtonClick param=0 node=26
    //   -> EmjTotalResult [-1],[-2] -> PostClose -> Emj [-2] -> PostClose -> PreFinalize
    // No SelectYesno is involved anywhere in that sequence. The only Yes/No seen all match was
    // the NPC table's *start* prompt, which the old blind-confirm would have answered.
    public const string ResultAddon = "EmjTotalResult";

    // Rank-only; absent from NPC matches, seen after EmjTotalResult in ranked play.
    public const string RankResultAddon = "EmjRankResult";

    public const uint ResultCloseNodeId = 26;

    public const int ResultCloseParam = 0;

    // Hand slots run 0..13 (13 is the draw/claim slot).
    public const int MaxSlot = 13;

    // The chooser panel. Shape i is node 5+i with ButtonClick param 9+i; the cancel button is
    // node 11 with param 8. Both confirmed against the live chooser on 2026-09-23, and they
    // match the paths the layout already carried.
    public const string ChooserPath = "1/46/52";

    public const int ChiShapeFirstParam = 9;

    public const int ChiCancelParam = 8;

    // A callback the addon sends about itself, which must never be replayed as input.
    public static bool IsNotification(int head)
        => head is HandStarted or HandEnded or Dismissed or Closed;

    public static int[] DiscardSlot(int slot)
        => slot is >= 0 and <= MaxSlot
            ? [Discard, slot]
            : throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Hand slots are 0..{MaxSlot}.");

    public static int[] SelectCallRow(int row)
        => row >= 0
            ? [CallRow, row]
            : throw new ArgumentOutOfRangeException(nameof(row), row, "Call rows are indexes into the live item table.");

    public static int[] AdvanceRecap() => [RecapNext];

    // The chooser offers at most four shapes; buttons beyond the offered count are hidden.
    public static int[] PickChiShape(int shapeIndex)
        => shapeIndex is >= 0 and <= 3
            ? [ChiShape, shapeIndex]
            : throw new ArgumentOutOfRangeException(nameof(shapeIndex), shapeIndex, "The chi chooser offers at most four shapes.");

    public static int[] PointAtTile(int tileIconId)
        => tileIconId > 0
            ? [Pointer, tileIconId]
            : throw new ArgumentOutOfRangeException(nameof(tileIconId), tileIconId, "Tile icon ids are positive.");
}
