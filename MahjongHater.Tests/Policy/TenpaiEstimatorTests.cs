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

    // Reliability of the 2026-09-21 Phoenix fit (learn-fit-tenpai, archive n24 train split):
    // observed tenpai of NON-riichi seats by discard count 0-6: 1.3 %, 7-10: 11.8 %,
    // 11-14: 24.3 %, 15-18: 35.6 %. Closed hands rarely stay dama, hence the low early values.
    [Theory]
    [InlineData(4, 0, 0.005, 0.03)]
    [InlineData(9, 0, 0.05, 0.15)]
    [InlineData(13, 0, 0.10, 0.25)]
    [InlineData(17, 0, 0.20, 0.45)]
    [InlineData(10, 1, 0.20, 0.40)]    // one call at turn 10
    [InlineData(12, 2, 0.55, 0.80)]    // two calls at turn 12
    [InlineData(8, 3, 0.55, 0.85)]     // three calls at turn 8
    public void Default_weights_follow_the_replay_fit(int discards, int melds, double lo, double hi)
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
        // The replay fit found no signal in early outside discards (weight ≈ 0) and a
        // small one in late middle discards.
        Assert.True(P(10, 0, late: 1) > P(10, 0));
        Assert.InRange(P(10, 0, early: 1) / P(10, 0), 0.8, 1.2);
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
