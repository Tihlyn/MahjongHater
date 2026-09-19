using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.Policy;

public class TenpaiEstimatorTests
{
    private static readonly PolicyWeights W = PolicyWeights.Default;

    private static double P(int discards, int melds = 0, double early = 0, double late = 0, bool riichi = false)
        => TenpaiEstimator.Estimate(new TenpaiFeatures(discards, melds, riichi, early, late), W);

    [Theory]
    [InlineData(6, 0, 0.03, 0.09)]     // closed hand, early: rare
    [InlineData(10, 0, 0.12, 0.24)]
    [InlineData(14, 0, 0.28, 0.45)]
    [InlineData(18, 0, 0.50, 0.70)]
    [InlineData(10, 1, 0.28, 0.45)]    // one call at turn 10
    [InlineData(12, 2, 0.60, 0.78)]    // two calls at turn 12
    [InlineData(8, 3, 0.60, 0.80)]     // three calls at turn 8
    public void Default_weights_follow_the_literature_curves(int discards, int melds, double lo, double hi)
    {
        Assert.InRange(P(discards, melds), lo, hi);
    }

    [Fact]
    public void Never_certain_without_riichi_even_very_late_and_open()
    {
        Assert.Equal(W.TenpaiMaxWithoutRiichi, P(24, 4, 1, 1));
        Assert.True(P(24, 4, 1, 1) < 1);
    }

    [Fact]
    public void Riichi_is_certain()
    {
        Assert.Equal(1, P(3, 0, riichi: true));
    }

    [Fact]
    public void Monotone_in_discards_melds_and_shape_features()
    {
        for (var t = 0; t < 20; t++)
            Assert.True(P(t + 1) >= P(t), $"turn {t}");
        Assert.True(P(10, 1) > P(10, 0));
        Assert.True(P(10, 2) > P(10, 1));
        Assert.True(P(10, 0, early: 1) > P(10, 0));
        Assert.True(P(10, 0, late: 1) > P(10, 0));
    }

    [Fact]
    public void Features_come_from_the_seat_state()
    {
        var seat = new SeatState(1, TestTiles.Parse("19m19p19s5m5p5s6m7p2z"), [Meld.MakePon(Tile.Parse("3z"), true)], false, -1, 25000);
        var f = TenpaiFeatures.From(seat);
        Assert.Equal(12, f.Discards);
        Assert.Equal(1, f.OpenMelds);
        Assert.Equal(1.0, f.EarlyOutside);            // first six are all terminals
        Assert.Equal(5 / 12d, f.LateMiddle, 6);       // 5m 5p 5s 6m 7p after turn 6
        Assert.False(f.Riichi);
    }
}
