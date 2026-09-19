using MahjongHater.Core;
using Xunit;

namespace MahjongHater.Tests;

public class HandTrackingTests
{
    [Theory]
    [InlineData(0, 14)]
    [InlineData(1, 11)]
    [InlineData(2, 8)]
    [InlineData(3, 5)]
    [InlineData(4, 2)]
    public void MaxClosedTiles_shrinks_by_three_per_meld(int melds, int expected)
    {
        Assert.Equal(expected, HandTracking.MaxClosedTiles(melds));
    }

    [Fact]
    public void HasAnyLegalCall_rejects_isolated_offsuit_tile()
    {
        // Live 2026-07-05: a stuck Chi/Pass panel over a hand with ZERO souzu while an
        // 8s was on the table — no chi, no pon, no ron. Not a local window.
        var hand = TestTiles.Parse("333m456m89m567p7p7z");
        Assert.False(HandTracking.HasAnyLegalCall(hand, Tile.Parse("8s"), 0));
    }

    [Fact]
    public void HasAnyLegalCall_pon_needs_a_pair_in_hand()
    {
        var hand = TestTiles.Parse("22z34567m11p345s");
        Assert.True(HandTracking.HasAnyLegalCall(hand, Tile.Parse("2z"), 0));
        Assert.False(HandTracking.HasAnyLegalCall(TestTiles.Parse("2z34567m111p345s"), Tile.Parse("2z"), 0));
    }

    [Theory]
    [InlineData("12m", "3m", true)]   // edge shape
    [InlineData("24m", "3m", true)]   // kanchan
    [InlineData("45m", "3m", true)]   // open shape
    [InlineData("57m", "3m", false)]  // no run through 3m
    public void HasAnyLegalCall_chi_shapes(string partial, string claimed, bool expected)
    {
        var hand = TestTiles.Parse(partial + "111p222p333s7z");
        Assert.Equal(expected, HandTracking.HasAnyLegalCall(hand, Tile.Parse(claimed), 0));
    }

    [Fact]
    public void HasAnyLegalCall_chi_only_when_allowed()
    {
        // Chi is only legal from the seat to our left; a pure chi shape must not count
        // for the other seats.
        var hand = TestTiles.Parse("45m111p222p333s7z");
        Assert.True(HandTracking.HasAnyLegalCall(hand, Tile.Parse("3m"), 0, allowChi: true));
        Assert.False(HandTracking.HasAnyLegalCall(hand, Tile.Parse("3m"), 0, allowChi: false));
    }

    [Fact]
    public void HasAnyLegalCall_chi_never_crosses_suits_or_honors()
    {
        // 9m + 2p in hand must not read as a run around a claimed 1p.
        Assert.False(HandTracking.HasAnyLegalCall(TestTiles.Parse("99m22p111s222s7z77z"), Tile.Parse("1p"), 0));
        Assert.False(HandTracking.HasAnyLegalCall(TestTiles.Parse("123m456m789m123p7z"), Tile.Parse("6z"), 0));
    }

    [Fact]
    public void HasAnyLegalCall_detects_ron_only_completion()
    {
        // Tanki wait on a lone honor: no pon, no chi — only ron makes this callable.
        var hand = TestTiles.Parse("111m222m333p444p7z");
        Assert.True(HandTracking.HasAnyLegalCall(hand, Tile.Parse("7z"), 0));
    }
}
