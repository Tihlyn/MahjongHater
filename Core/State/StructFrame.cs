using System.Buffers.Binary;

namespace MahjongHater.Core.State;

// Raw ints read from the AddonEmj struct in one frame, plus the two AtkValues the reader
// still consumes. No Dalamud types: built from a byte copy of the addon (live) or from a
// hex fixture (tests) through the same FromBytes.
public sealed record StructFrame(
    int[] HandSlots,
    int?[] Scores,
    int?[] DiscardCounts,
    int[]?[] DiscardArrays,
    int? DoraIndicator,
    int? UraDoraIndicator,
    int? Melds,
    int? RiichiFlags,
    int? RoundWind,
    int? SeatWind,
    int? DealerSeat,
    int? Honba,
    int? RiichiSticks,
    int? WallRemaining,
    int StateCode,
    int WallCount,
    int AtkValuesCount)
{
    public static StructFrame FromBytes(ReadOnlySpan<byte> memory, EmjLayout layout, int stateCode, int wallCount, int atkValuesCount)
    {
        var hand = new int[layout.HandSlots];
        for (var i = 0; i < hand.Length; i++)
            hand[i] = ReadInt(memory, layout.HandArray + (i * 4)) ?? 0;

        var arrays = new int[]?[4];
        for (var seat = 0; seat < 4; seat++)
        {
            if (layout.DiscardArrays[seat] is not { } start)
                continue;
            var count = layout.DiscardCounts[seat] is { } c ? Math.Clamp(ReadByte(memory, c) ?? 0, 0, layout.DiscardArrayMaxLen) : layout.DiscardArrayMaxLen;
            var arr = new int[count];
            for (var i = 0; i < count; i++)
                arr[i] = ReadInt(memory, start + (i * 4)) ?? 0;
            arrays[seat] = arr;
        }

        var scores = new int?[4];
        var counts = new int?[4];
        for (var seat = 0; seat < 4; seat++)
        {
            scores[seat] = layout.Scores[seat] is { } so ? ReadInt(memory, so) : null;
            counts[seat] = layout.DiscardCounts[seat] is { } co ? ReadByte(memory, co) : null;
        }

        return new StructFrame(
            hand,
            scores,
            counts,
            arrays,
            layout.DoraIndicator is { } d ? ReadInt(memory, d) : null,
            layout.UraDoraIndicator is { } u ? ReadInt(memory, u) : null,
            layout.Melds is { } m ? ReadInt(memory, m) : null,
            layout.RiichiFlags is { } r ? ReadInt(memory, r) : null,
            layout.RoundWind is { } rw ? ReadInt(memory, rw) : null,
            layout.SeatWind is { } sw ? ReadInt(memory, sw) : null,
            layout.DealerSeat is { } ds ? ReadInt(memory, ds) : null,
            layout.Honba is { } h ? ReadInt(memory, h) : null,
            layout.RiichiSticks is { } rs ? ReadInt(memory, rs) : null,
            layout.WallRemaining is { } w ? ReadInt(memory, w) : null,
            stateCode,
            wallCount,
            atkValuesCount);
    }

    public DecodedStruct Decode(EmjLayout layout)
    {
        var iconBase = layout.ResolveBase(this.HandSlots, out var shifted);
        var healthy = iconBase is not null;
        var effective = iconBase ?? layout.TileIconBase;

        // Slots 0..12 hold the sorted closed hand; the last slot is the draw/claim slot
        // (docs/EMJ_STRUCT.md). Empty slots read 0.
        var closed = new List<Tile>(this.HandSlots.Length);
        Tile? drawn = null;
        var last = this.HandSlots.Length - 1;
        for (var i = 0; i < this.HandSlots.Length; i++)
        {
            var raw = this.HandSlots[i];
            if (raw == 0 || !EmjLayout.TryDecodeTile(raw, effective, out var tile))
                continue;
            if (i == last)
                drawn = tile;
            else
                closed.Add(tile);
        }

        var seatDiscards = new List<Tile>?[4];
        for (var seat = 0; seat < 4; seat++)
        {
            if (this.DiscardArrays[seat] is not { } arr)
                continue;
            var tiles = new List<Tile>(arr.Length);
            foreach (var raw in arr)
            {
                if (raw == 0)
                    break;
                if (EmjLayout.TryDecodeTile(raw, effective, out var t))
                    tiles.Add(t);
            }

            seatDiscards[seat] = tiles;
        }

        return new DecodedStruct(
            closed,
            drawn,
            this.Scores,
            this.DiscardCounts,
            seatDiscards,
            this.DoraIndicator is { } dora && EmjLayout.TryDecodeTile(dora, effective, out var doraTile) ? doraTile : null,
            this.UraDoraIndicator is { } ura && EmjLayout.TryDecodeTile(ura, effective, out var uraTile) ? uraTile : null,
            this.RiichiFlags,
            this.RoundWind,
            this.SeatWind,
            this.DealerSeat,
            this.Honba,
            this.RiichiSticks,
            this.WallRemaining,
            this.StateCode,
            this.WallCount,
            effective,
            shifted,
            healthy);
    }

    private static int? ReadInt(ReadOnlySpan<byte> memory, int offset)
        => offset >= 0 && offset + 4 <= memory.Length ? BinaryPrimitives.ReadInt32LittleEndian(memory.Slice(offset, 4)) : null;

    private static int? ReadByte(ReadOnlySpan<byte> memory, int offset)
        => offset >= 0 && offset < memory.Length ? memory[offset] : null;
}

// Struct frame with icon ids turned into tiles. Optional fields are null when the layout
// has no offset for them yet (Phase 0 fills them in as they are mapped).
public sealed record DecodedStruct(
    IReadOnlyList<Tile> ClosedTiles,      // slots 0..12, visual (sorted) order
    Tile? DrawnTile,                      // slot 13
    int?[] Scores,
    int?[] DiscardCounts,
    List<Tile>?[] SeatDiscards,
    Tile? DoraIndicator,
    Tile? UraDoraIndicator,
    int? RiichiFlags,
    int? RoundWindRaw,
    int? SeatWindRaw,
    int? DealerSeatRaw,
    int? Honba,
    int? RiichiSticks,
    int? WallRemaining,
    int StateCode,
    int WallCount,
    int EffectiveIconBase,
    bool BaseShifted,
    bool Healthy)
{
    public int ClosedCount => this.ClosedTiles.Count + (this.DrawnTile is null ? 0 : 1);

    // Closed hand in visual order: sorted slots then the draw — the same order the
    // hand-slot nodes render in, so an index here is the node index for a click.
    public List<Tile> HandInVisualOrder()
    {
        var list = new List<Tile>(this.ClosedTiles);
        if (this.DrawnTile is { } d)
            list.Add(d);
        return list;
    }

    public int TotalDiscards
    {
        get
        {
            var sum = 0;
            foreach (var c in this.DiscardCounts)
                sum += c ?? 0;
            return sum;
        }
    }
}
