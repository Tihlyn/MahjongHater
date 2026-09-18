using MahjongHater.Core;
using Xunit;

namespace MahjongHater.Tests;

public class FaceKeysTests
{
    [Theory]
    [InlineData("i76041", "1m")]                 // plain pile key
    [InlineData("i76050", "1p")]
    [InlineData("i76068", "1z")]                 // East wind
    [InlineData("i76074", "7z")]                 // Chun
    [InlineData("i76075", "0m")]                 // red five man
    [InlineData("i76076", "0p")]                 // red five pin
    [InlineData("i76077", "0s")]                 // red five sou
    public void Decodes_plain_icon_keys(string key, string expected)
    {
        Assert.True(FaceKeys.TryDecodeIcon(key, out var tile));
        Assert.Equal(Tile.Parse(expected), tile);
    }

    [Fact]
    public void Decodes_composite_hand_key_with_repeated_icon()
    {
        // Real recorded key shape: backdrop fragment + icon fragment, twice.
        var key = "cp0u0v0w42h55tEmjTile_hr1.tex|i76042|p0u0v0w42h55tEmjTile_hr1.tex|i76042";
        Assert.True(FaceKeys.TryDecodeIcon(key, out var tile));
        Assert.Equal(Tile.Parse("2m"), tile);
    }

    [Fact]
    public void Rejects_conflicting_icons_in_one_key()
    {
        Assert.False(FaceKeys.TryDecodeIcon("ci76042|i76043", out _));
    }

    [Fact]
    public void Rejects_sentinel_and_out_of_range_icons()
    {
        Assert.False(FaceKeys.TryDecodeIcon("i4294967295p0", out _)); // legacy sentinel key
        Assert.False(FaceKeys.TryDecodeIcon("i123", out _));          // not a tile icon
        Assert.False(FaceKeys.TryDecodeIcon("i76078", out _));        // past red fives
    }

    [Fact]
    public void Ignores_letter_i_inside_texture_names()
    {
        // "Tile" contains an 'i' — must not be parsed as an icon fragment.
        Assert.False(FaceKeys.TryDecodeIcon("a21p20u132v0w34h45tEmjTile_hr1.tex", out _));
    }

    [Fact]
    public void Null_and_empty_keys_fail()
    {
        Assert.False(FaceKeys.TryDecodeIcon(null, out _));
        Assert.False(FaceKeys.TryDecodeIcon(string.Empty, out _));
    }
}

public class TileIconMapTests
{
    [Fact]
    public void All_34_kinds_round_trip_through_icon_ids()
    {
        for (var icon = 76041; icon <= 76074; icon++)
        {
            Assert.True(TileHelpers.TryTileFromIconId(icon, out var tile));
            Assert.False(tile.IsRedFive);
        }
    }

    [Fact]
    public void Red_five_icons_decode_with_red_flag()
    {
        Assert.True(TileHelpers.TryTileFromIconId(76075, out var m));
        Assert.True(m is { Suit: TileSuit.Man, Number: 5, IsRedFive: true });
        Assert.True(TileHelpers.TryTileFromIconId(76076, out var p));
        Assert.True(p is { Suit: TileSuit.Pin, Number: 5, IsRedFive: true });
        Assert.True(TileHelpers.TryTileFromIconId(76077, out var s));
        Assert.True(s is { Suit: TileSuit.Sou, Number: 5, IsRedFive: true });
    }

    [Fact]
    public void Out_of_range_icons_fail()
    {
        Assert.False(TileHelpers.TryTileFromIconId(76040, out _));
        Assert.False(TileHelpers.TryTileFromIconId(76078, out _));
        Assert.False(TileHelpers.TryTileFromIconId(0, out _));
    }
}
