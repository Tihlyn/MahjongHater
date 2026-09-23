namespace MahjongHater.Core.Operate;

// The Emj addon's input protocol, read off the game's own FireCallback traffic during a full
// manually-played match on 2026-09-23 (docs/research/ADDON_PROTOCOL_2026_09_23.md; raw capture
// in artifacts/capture/). Nothing here is inferred from behaviour: every command below was
// observed being sent BY THE GAME when a human performed the action, and each carries the
// event it was paired with and how many times it fired.
//
// The distinction that matters is COMMAND vs NOTIFICATION. The addon fires callbacks itself on
// state transitions, and replaying one of those as if it were input is how another plugin
// reportedly parked the addon in state 32 (docs/research/ADDON_INTERACTION_PLAN_2026_09_22.md).
// Only the three commands here were seen to follow a human's click; the notifications are
// named so they can never be mistaken for commands.
public static class EmjProtocol
{
    // ─────────────────────────────── commands ───────────────────────────────

    // [7, slot] — discard. Paired with ButtonClick param=slot+15 on node 9, 96 times. The
    // game also fires it with NO event at all (4 times) while a riichi hand auto-discards its
    // draw, which is the clearest proof that the callback is the command and the click is
    // merely one way to reach it.
    public const int Discard = 7;

    // [11, row] — answer the call window by row index. Paired with ListItemClick param=0 on
    // node 3, 22 times. Row indices come from the live item table, never from a label.
    public const int CallRow = 11;

    // [14] — advance the end-of-round recap. Paired with ButtonClick param=7 on node 97, 9
    // times; this is what finally identifies node 97, which the plugin had been pressing by
    // trial and error without ever reading it.
    public const int RecapNext = 14;

    // ──────────────────────────── pointer handshake ─────────────────────────

    // [15, tileIconId] — "the pointer is on this tile". Fired by the addon from its own cursor
    // polling with NO AtkEvent behind it (117 of 118 fires), so it cannot be produced by
    // synthesising a MouseOver. A human discard is [15, icon] then [7, slot]; a synthetic
    // ButtonClick produces only [7, slot], leaving the addon's idea of the pointed-at tile
    // untouched. Sending this is imitating the game rather than simulating a mouse, and it is
    // the outstanding lead on the table going unhoverable.
    public const int Pointer = 15;

    // ───────────────────────── notifications: never replay ──────────────────

    public const int HandStarted = 9;    // <- TimelineActiveLabelChanged param=33 node=128, x10
    public const int HandEnded = 17;     // <- TimelineActiveLabelChanged param=36 node=54, x10
    public const int Dismissed = -1;
    public const int Closed = -2;

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

    public static int[] PointAtTile(int tileIconId)
        => tileIconId > 0
            ? [Pointer, tileIconId]
            : throw new ArgumentOutOfRangeException(nameof(tileIconId), tileIconId, "Tile icon ids are positive.");
}
