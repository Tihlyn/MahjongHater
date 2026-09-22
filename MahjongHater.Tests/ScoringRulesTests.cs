using MahjongHater.Core;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests;

// The payout table checked against the game itself. Every row below is a win screen the
// client printed on 2026-09-22 ("<fu> Fu <han> Han [limit]", [3]=dealer, [7]=points/100,
// [8]=tsumo), so these are observations, not textbook values
// (docs/research/RULES_CROSSCHECK_2026_09_22.md).
public class ScoringRulesTests
{
    [Theory]
    // fu, han, dealer, tsumo, points the game actually paid
    [InlineData(40, 5, false, false, 8000)]   // 14:40:59 Mangan
    [InlineData(25, 5, true, true, 12000)]    // 14:45:41 Mangan, dealer tsumo
    [InlineData(30, 2, false, false, 2000)]   // 14:50:09
    [InlineData(40, 3, false, false, 5200)]   // 15:07:43
    [InlineData(20, 4, true, true, 7800)]     // 15:17:57 dealer tsumo, below mangan
    [InlineData(20, 5, false, true, 8000)]    // 15:31:34 Mangan, non-dealer tsumo
    [InlineData(40, 2, false, false, 2600)]   // 15:38:08
    [InlineData(40, 1, false, false, 1300)]   // 15:39:49
    [InlineData(40, 2, false, true, 2700)]    // 15:50:00 non-dealer tsumo
    [InlineData(40, 4, false, false, 8000)]   // 15:53:55 Mangan at 4 han 40 fu
    [InlineData(50, 2, true, false, 4800)]    // 16:11:54 dealer ron
    [InlineData(40, 3, true, false, 7700)]    // 16:14:11 dealer ron, rounded up
    [InlineData(30, 1, true, false, 1500)]    // 16:52:36 dealer ron
    [InlineData(30, 1, false, false, 1000)]   // 18:26:24
    [InlineData(30, 5, false, true, 8000)]    // 16:47:58 Mangan, non-dealer tsumo
    public void The_table_pays_what_the_game_paid(int fu, int han, bool dealer, bool tsumo, int points)
        => Assert.Equal(points, ScoringEngine.TotalPaymentFor(fu, han, dealer, tsumo));

    // The limit boundaries the Lodestone does not state, pinned to the standard rules our
    // observations are consistent with. 4 han 30 fu is the one that would move under
    // round-up ("kiriage") mangan - no such hand has been seen, so it stays standard and
    // the live check will flag it the first time the game disagrees.
    [Theory]
    [InlineData(30, 4, 7700)]    // NOT mangan without kiriage
    [InlineData(40, 4, 8000)]    // mangan
    [InlineData(60, 3, 7700)]    // NOT mangan without kiriage
    [InlineData(70, 3, 8000)]    // mangan
    [InlineData(30, 6, 12000)]   // haneman
    [InlineData(30, 8, 16000)]   // baiman
    [InlineData(30, 11, 24000)]  // sanbaiman
    [InlineData(30, 13, 32000)]  // kazoe yakuman
    public void Limit_boundaries_are_the_standard_ones(int fu, int han, int nonDealerRon)
        => Assert.Equal(nonDealerRon, ScoringEngine.TotalPaymentFor(fu, han, isDealer: false, tsumo: false));

    [Fact]
    public void The_win_screen_text_is_parsed_into_fu_han_and_limit()
    {
        var t = new EventTracker();
        t.OnRefresh(AtkFrame.OfInts([32, 0, 0, 1, 0, 0, 0, 120, 1, .. new int[13]])
            .WithString(2, "East 3 East Wind").WithString(6, "25 Fu 5 Han Mangan"));
        var screen = t.LastWinScreen;
        Assert.NotNull(screen);
        Assert.Equal(25, screen!.Fu);
        Assert.Equal(5, screen.Han);
        Assert.Equal("Mangan", screen.Limit);
        Assert.Equal(12000, screen.Points);
        Assert.True(screen.WinnerIsDealer);
        Assert.True(screen.Tsumo);
        Assert.Equal(screen.Points, ScoringEngine.TotalPaymentFor(screen.Fu, screen.Han, screen.WinnerIsDealer, screen.Tsumo));
    }

    [Fact]
    public void A_win_screen_without_a_score_line_parses_to_nothing()
    {
        var t = new EventTracker();
        t.OnRefresh(AtkFrame.OfInts([32, 0, .. new int[20]]).WithString(2, "East 1 East Wind"));
        Assert.Null(t.LastWinScreen);
    }
}
