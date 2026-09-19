using MahjongHater.Core;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.State;

public class StructFrameTests
{
    [Fact]
    public void Live_dump_decodes_to_the_hand_seen_on_screen()
    {
        var layout = StructFixture.Layout;
        var frame = StructFrame.FromBytes(StructFixture.Bytes(), layout, stateCode: 6, wallCount: 0, atkValuesCount: 50);
        var d = frame.Decode(layout);

        Assert.True(d.Healthy);
        Assert.False(d.BaseShifted);
        Assert.Equal(76041, d.EffectiveIconBase);
        Assert.Equal(TestTiles.Parse(StructFixture.LiveHand.Replace(" ", string.Empty)), d.ClosedTiles);
        Assert.Equal(Tile.Parse(StructFixture.LiveDrawn), d.DrawnTile);
        Assert.True(d.ClosedTiles[7].IsRedFive);
        Assert.Equal(14, d.ClosedCount);
        Assert.Equal([25000, 25000, 25000, 25000], d.Scores);
        Assert.Equal(Tile.Parse("5p"), d.DoraIndicator);
        Assert.Equal(6, d.StateCode);
        Assert.Null(d.WallRemaining);
        Assert.All(d.SeatDiscards, l => Assert.Null(l));
        Assert.Equal(13, d.Us.ClosedTileCount);
        Assert.Equal(0, d.Us.MeldCount);
        Assert.Null(d.Us.RiichiDiscardIndex);
    }

    [Fact]
    public void Seat_panel_meld_records_decode_and_stale_entries_beyond_the_count_are_ignored()
    {
        // Seat 2: pon 5s claimed from toimen, pon 8m from shimocha; a stale third record.
        var records = new StructFixture.MeldRecord[]?[]
        {
            null, null,
            [new StructFixture.MeldRecord(22, 2), new StructFixture.MeldRecord(7, 1), new StructFixture.MeldRecord(255, 3)],
            null,
        };
        var buf = StructFixture.BytesFor("124589m1589p69s6z", null, melds: records, riichiIndices: [255, 7, 255, 255]);
        buf[StructFixture.Layout.MeldCounts[2]!.Value] = 2;
        var d = StructFrame.FromBytes(buf, StructFixture.Layout, 15, 0, 50).Decode(StructFixture.Layout);

        var seat2 = d.Seats[2];
        Assert.Equal(2, seat2.MeldCount);
        Assert.Equal(2, seat2.Melds.Count);
        Assert.Equal(Tile.Parse("5s"), seat2.Melds[0].Tile);
        Assert.Equal(2, seat2.Melds[0].FromDirection);
        Assert.Equal(Tile.Parse("8m"), seat2.Melds[1].Tile);
        Assert.False(seat2.Melds[1].IsChi);
        Assert.Equal(7, d.Seats[1].RiichiDiscardIndex);
        Assert.Null(d.Seats[0].RiichiDiscardIndex);
    }

    [Fact]
    public void Chi_records_carry_no_tile()
    {
        var records = new StructFixture.MeldRecord[]?[] { null, null, null, [new StructFixture.MeldRecord(255, 3)] };
        var d = StructFrame.FromBytes(StructFixture.BytesFor("124589m1589p69s6z", null, melds: records), StructFixture.Layout, 15, 0, 50)
            .Decode(StructFixture.Layout);
        var meld = Assert.Single(d.Seats[3].Melds);
        Assert.True(meld.IsChi);
        Assert.Null(meld.Tile);
        Assert.Equal(3, meld.FromDirection);
    }

    [Fact]
    public void Empty_draw_slot_means_no_drawn_tile()
    {
        var d = StructFixture.Decoded("123m456p789s1122z", drawn: null);
        Assert.Null(d.DrawnTile);
        Assert.Equal(13, d.ClosedCount);
        Assert.Equal(13, d.HandInVisualOrder().Count);
    }

    [Fact]
    public void Visual_order_is_sorted_slots_then_draw()
    {
        var d = StructFixture.Decoded("123m456p789s1122z", drawn: "1m");
        var visual = d.HandInVisualOrder();
        Assert.Equal(14, visual.Count);
        Assert.Equal(Tile.Parse("1m"), visual[^1]);
    }

    [Fact]
    public void Unmapped_offsets_read_as_null()
    {
        var layout = EmjLayout.Parse("""{ "tileIconBase": 76041, "offsets": { "handArray": "0x0DB8" } }""");
        var frame = StructFrame.FromBytes(StructFixture.Bytes(layout), layout, 6, 0, 50);
        Assert.All(frame.Seats, s => Assert.Null(s.Score));
        Assert.All(frame.Seats, s => Assert.Null(s.DiscardCount));
        Assert.All(frame.Seats, s => Assert.Null(s.MeldTileIndices));
        Assert.Null(frame.DoraIndicator);
        var d = frame.Decode(layout);
        Assert.Null(d.DoraIndicator);
        Assert.All(d.Seats, s => Assert.Empty(s.Melds));
        Assert.All(d.Seats, s => Assert.Null(s.RiichiDiscardIndex));
    }

    [Fact]
    public void Discard_arrays_are_read_up_to_the_seat_count()
    {
        var layout = EmjLayout.Parse("""
            { "tileIconBase": 76041, "offsets": { "handArray": "0x0DB8", "discardCounts": ["0x04FE", "0x07DE", "0x0ABE", "0x0D9E"], "discardArrays": ["0x0200", null, null, null], "discardArrayMaxLen": 24 } }
            """);
        var buf = StructFixture.Bytes(layout, discardCounts: [2, 0, 0, 0]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0x200, 4), 76041);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0x204, 4), 76068);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0x208, 4), 76050); // beyond the count: ignored

        var d = StructFrame.FromBytes(buf, layout, 6, 0, 50).Decode(layout);
        Assert.Equal(TestTiles.Parse("1m1z"), d.SeatDiscards[0]);
        Assert.Null(d.SeatDiscards[1]);
        Assert.Equal(2, d.TotalDiscards);
    }

    [Fact]
    public void Undecodable_slots_mark_the_frame_unhealthy_but_keep_what_decodes()
    {
        var layout = StructFixture.Layout;
        var buf = StructFixture.BytesFor("123m456p789s1122z", "1m");
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(layout.HandArray + 4, 4), 12345);
        var d = StructFrame.FromBytes(buf, layout, 6, 0, 50).Decode(layout);
        Assert.False(d.Healthy);
        Assert.Equal(12, d.ClosedTiles.Count);
    }
}
