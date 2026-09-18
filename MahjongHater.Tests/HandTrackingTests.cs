using MahjongHater.Core;
using Xunit;

namespace MahjongHater.Tests;

public class HandTrackingTests
{
    [Fact]
    public void RemoveOneTile_prefers_exact_match_keeping_red_five()
    {
        var hand = TestTiles.Parse("05m"); // red 5m + normal 5m
        Assert.True(HandTracking.RemoveOneTile(hand, Tile.Parse("5m")));
        Assert.Single(hand);
        Assert.True(hand[0].IsRedFive);
    }

    [Fact]
    public void RemoveOneTile_removes_red_copy_when_discard_is_red()
    {
        var hand = TestTiles.Parse("05m");
        Assert.True(HandTracking.RemoveOneTile(hand, Tile.Parse("0m")));
        Assert.Single(hand);
        Assert.False(hand[0].IsRedFive);
    }

    [Fact]
    public void RemoveOneTile_falls_back_to_same_kind()
    {
        var hand = TestTiles.Parse("5m"); // only the normal copy
        Assert.True(HandTracking.RemoveOneTile(hand, Tile.Parse("0m")));
        Assert.Empty(hand);
    }

    [Fact]
    public void RemoveOneTile_returns_false_when_absent()
    {
        var hand = TestTiles.Parse("123m");
        Assert.False(HandTracking.RemoveOneTile(hand, Tile.Parse("9s")));
        Assert.Equal(3, hand.Count);
    }

    [Fact]
    public void RemoveOneTile_removes_exactly_one_copy()
    {
        var hand = TestTiles.Parse("555m");
        Assert.True(HandTracking.RemoveOneTile(hand, Tile.Parse("5m")));
        Assert.Equal(2, hand.Count);
    }

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
    public void PrePromptAnnouncement_with_matching_ghost_allows_wide_window()
    {
        var seven = Tile.Parse("7m");
        Assert.True(HandTracking.IsPrePromptAnnouncement(seven, 1200, seven));
        Assert.False(HandTracking.IsPrePromptAnnouncement(seven, 1600, seven));
    }

    [Fact]
    public void PrePromptAnnouncement_with_mismatched_ghost_never_reattributes()
    {
        // A genuine local discard followed quickly by a claim window on a DIFFERENT
        // tile must stay booked as a local discard.
        Assert.False(HandTracking.IsPrePromptAnnouncement(Tile.Parse("7m"), 100, Tile.Parse("3s")));
    }

    [Fact]
    public void PrePromptAnnouncement_without_ghost_uses_tight_window()
    {
        Assert.True(HandTracking.IsPrePromptAnnouncement(Tile.Parse("7m"), 400, null));
        Assert.False(HandTracking.IsPrePromptAnnouncement(Tile.Parse("7m"), 900, null));
    }

    [Fact]
    public void PrePromptAnnouncement_ghost_matches_by_kind_not_redness()
    {
        Assert.True(HandTracking.IsPrePromptAnnouncement(Tile.Parse("0m"), 500, Tile.Parse("5m")));
    }

    [Fact]
    public void ClaimWindowSlotJump_fires_on_discard_with_no_draw_after()
    {
        Assert.True(HandTracking.IsClaimWindowSlotJump(800, 15000));
    }

    [Fact]
    public void ClaimWindowSlotJump_suppressed_when_local_draw_is_fresher()
    {
        // Own turn: the 14th tile was drawn AFTER the opponent's discard — the visible
        // Chi/Pass panel is a stale leftover, not a claim window (live 2026-07-05).
        Assert.False(HandTracking.IsClaimWindowSlotJump(3000, 1200));
    }

    [Fact]
    public void ClaimWindowSlotJump_suppressed_when_discard_is_stale()
    {
        Assert.False(HandTracking.IsClaimWindowSlotJump(6000, 99999));
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
