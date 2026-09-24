using MahjongHater.Core;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.State;

public class EmjLayoutTests
{
    [Fact]
    public void Embedded_layout_parses_with_the_verified_offsets()
    {
        var layout = EmjLayout.Parse(EmjLayout.ReadEmbedded());
        Assert.Equal("Emj", layout.AddonName);
        Assert.Equal(76041, layout.TileIconBase);
        Assert.Equal(0x0DB8, layout.HandArray);
        Assert.Equal(14, layout.HandSlots);
        Assert.Equal([0x0500, 0x07E0, 0x0AC0, 0x0DA0], layout.Scores);
        Assert.Equal(0x0FD8, layout.DoraIndicator);
        Assert.Equal(0x0FDC, layout.DoraIndicatorCount);
        Assert.Equal(6, layout.StateCodes.OurTurn);
        Assert.Equal(13, layout.StateCodes.Meld);
        Assert.Equal(135, layout.Nodes.HandSlotDraw);
        Assert.Equal("1/46/104/3", layout.Nodes.CallList);
        Assert.Equal("1/36/37/38/7/9", layout.Nodes.SeatWindTexts[0]);
        Assert.True(layout.RequiredBytes >= 0x0FDD);
        Assert.True(layout.RequiredBytes <= EmjLayout.MaxReadBytes);
    }

    [Fact]
    public void Embedded_layout_carries_the_seat_panel_fields()
    {
        var layout = StructFixture.Layout;
        Assert.Equal([0x0478, 0x0758, 0x0A38, 0x0D18], layout.MeldTileIndexArrays);
        Assert.Equal([0x0488, 0x0768, 0x0A48, 0x0D28], layout.MeldFromDirectionBytes);
        Assert.Equal([0x04FD, 0x07DD, 0x0ABD, 0x0D9D], layout.MeldCounts);
        Assert.Equal([0x04FC, 0x07DC, 0x0ABC, 0x0D9C], layout.ClosedTileCounts);
        Assert.Equal([0x04FF, 0x07DF, 0x0ABF, 0x0D9F], layout.RiichiDiscardIndexBytes);
        Assert.Equal([0x0504, 0x07E4, 0x0AC4, 0x0DA4], layout.PointDifferences);
        Assert.Equal(4, layout.MeldTileIndexSlots);
        Assert.Equal(-1, layout.MeldTileIndexEmpty);
        Assert.Equal(255, layout.MeldTileIndexUnknown);
        Assert.Equal(255, layout.RiichiNone);
    }

    [Fact]
    public void Unknown_keys_are_ignored_and_old_scalar_shapes_are_tolerated()
    {
        var layout = EmjLayout.Parse("""
            { "tileIconBase": 76041, "future": { "x": 1 }, "offsets": { "handArray": "0x0DB8", "melds": null, "riichiFlags": null, "somethingNew": "0x10" } }
            """);
        Assert.Null(layout.MeldCounts[0]);
        Assert.Null(layout.RiichiDiscardIndexBytes[0]);
        Assert.Equal(255, layout.RiichiNone);
    }

    [Fact]
    public void Nulls_and_missing_sections_become_null_or_defaults()
    {
        var layout = EmjLayout.Parse("""
            { "name": "X", "tileIconBase": 100, "offsets": { "handArray": "0x10", "scores": [null, "0x20"], "discardArrays": [null, null, null, null], "doraIndicator": null } }
            """);
        Assert.Equal("Emj", layout.AddonName);
        Assert.Equal(14, layout.HandSlots);
        Assert.Null(layout.Scores[0]);
        Assert.Equal(0x20, layout.Scores[1]);
        Assert.Null(layout.Scores[2]);
        Assert.Null(layout.DoraIndicator);
        Assert.Null(layout.MeldCounts[0]);
        Assert.Null(layout.RiichiDiscardIndexBytes[0]);
        Assert.Equal(1, layout.WallCountIndex);
        Assert.Equal("1/46/104/3", layout.Nodes.CallList);
        Assert.Equal(0x10 + (14 * 4), layout.RequiredBytes);
    }

    [Theory]
    [InlineData("0x0DB8", 0x0DB8)]
    [InlineData("0dB8", 0x0DB8)]
    [InlineData("  0x10 ", 0x10)]
    public void Hex_offsets_parse_with_or_without_prefix(string text, int expected)
        => Assert.Equal(expected, EmjLayout.ParseHex(text));

    [Fact]
    public void Missing_required_fields_throw()
    {
        Assert.Throws<InvalidDataException>(() => EmjLayout.Parse("""{ "offsets": { "handArray": "0x10" } }"""));
        Assert.Throws<InvalidDataException>(() => EmjLayout.Parse("""{ "tileIconBase": 1, "offsets": { } }"""));
    }

    [Fact]
    public void Decodes_all_37_faces_from_the_base()
    {
        var layout = StructFixture.Layout;
        Assert.True(layout.TryDecodeTile(76041, out var t0));
        Assert.Equal(Tile.Parse("1m"), t0);
        Assert.True(layout.TryDecodeTile(76074, out var t33));
        Assert.Equal(Tile.Parse("7z"), t33);
        Assert.True(layout.TryDecodeTile(76077, out var red));
        Assert.True(red.IsRedFive);
        Assert.Equal(TileSuit.Sou, red.Suit);
        Assert.False(layout.TryDecodeTile(76078, out _));
        Assert.False(layout.TryDecodeTile(0, out _));
    }

    [Fact]
    public void ResolveBase_keeps_the_configured_base_when_everything_decodes()
    {
        var layout = StructFixture.Layout;
        int[] slots = [76041, 76048, 0, 0, 76077, 0, 0, 0, 0, 0, 0, 0, 0, 76068];
        Assert.Equal(76041, layout.ResolveBase(slots, out var shifted));
        Assert.False(shifted);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    [InlineData(-8)]
    public void ResolveBase_self_heals_a_shifted_client(int shift)
    {
        var layout = StructFixture.Layout;
        var slots = new int[14];
        // 13 populated slots covering low and high ids so the shift is unambiguous.
        int[] idx = [0, 7, 11, 11, 18, 18, 22, 36, 25, 25, 26, 28, 29];
        for (var i = 0; i < idx.Length; i++)
            slots[i] = 76041 + shift + idx[i];
        Assert.Equal(76041 + shift, layout.ResolveBase(slots, out var shifted));
        Assert.True(shifted);
    }

    [Fact]
    public void ResolveBase_reports_unhealthy_when_no_base_fits()
    {
        var layout = StructFixture.Layout;
        int[] slots = [1, 2, 3, 4, 5, 999999, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Null(layout.ResolveBase(slots, out _));
    }

    [Fact]
    public void ResolveBase_needs_evidence_before_retuning()
    {
        var layout = StructFixture.Layout;
        // Three populated slots that only decode at base-2: too little evidence.
        int[] slots = [76039, 76046, 76050, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Null(layout.ResolveBase(slots, out var shifted));
        Assert.False(shifted);
    }
}
