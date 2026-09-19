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
        Assert.All(frame.Scores, s => Assert.Null(s));
        Assert.All(frame.DiscardCounts, c => Assert.Null(c));
        Assert.Null(frame.DoraIndicator);
        Assert.Null(frame.Decode(layout).DoraIndicator);
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
