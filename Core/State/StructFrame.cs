using System.Buffers.Binary;

namespace MahjongHater.Core.State;

// Raw ints read from the AddonEmj struct in one frame, plus the two AtkValues the reader
// still consumes. No Dalamud types: built from a byte copy of the addon (live) or from a
// hex fixture (tests) through the same FromBytes. Offsets: docs/EMJ_STRUCT.md.
public sealed record StructFrame(
    int[] HandSlots,
    SeatPanelRaw[] Seats,
    int[]?[] DiscardArrays,
    int? DoraIndicator,
    int? DoraIndicatorCount,
    int? UraDoraIndicator,
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

        var seats = new SeatPanelRaw[4];
        var arrays = new int[]?[4];
        for (var seat = 0; seat < 4; seat++)
        {
            int[]? meldIdx = null;
            if (layout.MeldTileIndexArrays[seat] is { } mi)
            {
                meldIdx = new int[layout.MeldTileIndexSlots];
                for (var i = 0; i < meldIdx.Length; i++)
                    meldIdx[i] = ReadInt(memory, mi + (i * 4)) ?? layout.MeldTileIndexEmpty;
            }

            int[]? meldFrom = null;
            if (layout.MeldFromDirectionBytes[seat] is { } mf)
            {
                meldFrom = new int[layout.MeldTileIndexSlots];
                for (var i = 0; i < meldFrom.Length; i++)
                    meldFrom[i] = ReadByte(memory, mf + i) ?? 0;
            }

            seats[seat] = new SeatPanelRaw(
                layout.ClosedTileCounts[seat] is { } cc ? ReadByte(memory, cc) : null,
                layout.MeldCounts[seat] is { } mc ? ReadByte(memory, mc) : null,
                layout.DiscardCounts[seat] is { } dc ? ReadByte(memory, dc) : null,
                layout.RiichiDiscardIndexBytes[seat] is { } ri ? ReadByte(memory, ri) : null,
                layout.Scores[seat] is { } so ? ReadInt(memory, so) : null,
                layout.PointDifferences[seat] is { } pd ? ReadInt(memory, pd) : null,
                meldIdx,
                meldFrom);

            if (layout.DiscardArrays[seat] is { } start)
            {
                var count = seats[seat].DiscardCount is { } c ? Math.Clamp(c, 0, layout.DiscardArrayMaxLen) : layout.DiscardArrayMaxLen;
                var arr = new int[count];
                for (var i = 0; i < count; i++)
                    arr[i] = ReadInt(memory, start + (i * 4)) ?? 0;
                arrays[seat] = arr;
            }
        }

        return new StructFrame(
            hand,
            seats,
            arrays,
            layout.DoraIndicator is { } d ? ReadInt(memory, d) : null,
            layout.DoraIndicatorCount is { } dn ? ReadByte(memory, dn) : null,
            layout.UraDoraIndicator is { } u ? ReadInt(memory, u) : null,
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

        // Slots 0..12 hold the sorted closed hand; the last slot is the draw/claim slot.
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

        var seats = new SeatPanel[4];
        var seatDiscards = new List<Tile>?[4];
        for (var seat = 0; seat < 4; seat++)
        {
            var raw = this.Seats[seat];
            var melds = new List<StructMeld>(4);
            if (raw.MeldTileIndices is { } idx)
            {
                // Entries beyond MeldCount are stale (from-direction is never reset).
                var n = raw.MeldCount is { } mc ? Math.Min(mc, idx.Length) : idx.Length;
                for (var i = 0; i < n; i++)
                {
                    var ti = idx[i];
                    var from = raw.MeldFromDirections is { } fd && i < fd.Length ? fd[i] : 0;
                    var needsComposition = ti == layout.MeldTileIndexUnknown;
                    melds.Add(new StructMeld(ti, needsComposition, EmjLayout.TryTileFromIndex(ti, out var t) ? t : null, from)
                    { IsEmpty = ti == layout.MeldTileIndexEmpty });
                }
            }

            int? riichiIndex = raw.RiichiIndexRaw is { } r && r != layout.RiichiNone ? r : null;
            seats[seat] = new SeatPanel(raw.ClosedTileCount, raw.MeldCount, raw.DiscardCount, riichiIndex, raw.Score, raw.PointDifference, melds);

            if (this.DiscardArrays[seat] is { } arr)
            {
                var tiles = new List<Tile>(arr.Length);
                foreach (var v in arr)
                {
                    if (v == 0)
                        break;
                    if (EmjLayout.TryDecodeTile(v, effective, out var t))
                        tiles.Add(t);
                }

                seatDiscards[seat] = tiles;
            }
        }

        return new DecodedStruct(
            closed,
            drawn,
            seats,
            seatDiscards,
            this.DoraIndicator is { } dora && EmjLayout.TryDecodeTile(dora, effective, out var doraTile) ? doraTile : null,
            this.DoraIndicatorCount,
            this.UraDoraIndicator is { } ura && EmjLayout.TryDecodeTile(ura, effective, out var uraTile) ? uraTile : null,
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

// One seat panel's raw counters (null = unmapped in the layout).
public sealed record SeatPanelRaw(
    int? ClosedTileCount,
    int? MeldCount,
    int? DiscardCount,
    int? RiichiIndexRaw,
    int? Score,
    int? PointDifference,
    int[]? MeldTileIndices,
    int[]? MeldFromDirections);

// A meld as the struct records it: the 34-index of the tile (pon/kan) or an unresolved marker
// (chi/concealed kan tiles not stored — the type-13 event has them), and the seat it was claimed from
// relative to the caller in turn order (1 shimocha, 2 toimen, 3 kamicha).
public sealed record StructMeld(int TileIndex, bool NeedsComposition, Tile? Tile, int FromDirection)
{
    public bool IsEmpty { get; init; }
}

public sealed record SeatPanel(
    int? ClosedTileCount,     // excludes the drawn/claimed tile
    int? MeldCount,
    int? DiscardCount,        // never decrements when a discard is claimed
    int? RiichiDiscardIndex,  // null = not in riichi (or unmapped)
    int? Score,
    int? PointDifference,
    IReadOnlyList<StructMeld> Melds);

// Struct frame with icon ids turned into tiles. Optional fields are null when the layout
// has no offset for them yet.
public sealed record DecodedStruct(
    IReadOnlyList<Tile> ClosedTiles,      // slots 0..12, visual (sorted) order
    Tile? DrawnTile,                      // slot 13
    SeatPanel[] Seats,
    List<Tile>?[] SeatDiscards,
    Tile? DoraIndicator,
    int? DoraIndicatorCount,
    Tile? UraDoraIndicator,
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
    public SeatPanel Us => this.Seats[0];

    public int ClosedCount => this.ClosedTiles.Count + (this.DrawnTile is null ? 0 : 1);

    public int?[] Scores => [.. this.Seats.Select(s => s.Score)];

    public int?[] DiscardCounts => [.. this.Seats.Select(s => s.DiscardCount)];

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
            foreach (var s in this.Seats)
                sum += s.DiscardCount ?? 0;
            return sum;
        }
    }
}
