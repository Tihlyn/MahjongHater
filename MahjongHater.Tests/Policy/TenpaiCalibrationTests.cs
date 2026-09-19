using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.Policy;

public class TenpaiCalibrationTests
{
    private static readonly DateTime T0 = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    private static StateSnapshot EndOfHand()
    {
        var seats = StateSnapshot.Empty.Seats.ToList();
        seats[1] = seats[1] with { Discards = TestTiles.Parse("19m19p19s5m5p5s") };
        seats[2] = seats[2] with { Discards = TestTiles.Parse("1z2z3z4z5m6m7m8m9m1p2p3p"), Melds = [Meld.MakePon(Tile.Parse("6z"), true)] };
        seats[3] = seats[3] with { Discards = TestTiles.Parse("1z2z"), Riichi = true, RiichiDiscardIndex = 1 };
        return StateSnapshot.Empty with { Phase = GamePhase.OthersTurn, Hand = TestTiles.Parse("123m456p789s1122z"), Seats = seats, RoundWind = Wind.South };
    }

    [Fact]
    public void Draw_records_every_opponent_from_the_banners()
    {
        var samples = TenpaiCalibration.FromRoundEnd(EndOfHand(), ["Noten...", "Noten...", "Tenpai!", "Tenpai!"], -1, PolicyWeights.Default, T0);
        Assert.Equal(3, samples.Count);
        Assert.All(samples, s => Assert.Equal("draw", s.Source));
        Assert.False(samples.Single(s => s.Seat == 1).Tenpai);
        Assert.True(samples.Single(s => s.Seat == 2).Tenpai);
        Assert.True(samples.Single(s => s.Seat == 3).Tenpai);
        Assert.Equal(1, samples.Single(s => s.Seat == 3).Predicted);          // riichi
        Assert.Equal(1, samples.Single(s => s.Seat == 2).Features.OpenMelds);
        Assert.Equal("South", samples[0].Round);
    }

    [Fact]
    public void Win_records_only_the_winner_and_ignores_residue_banners()
    {
        var samples = TenpaiCalibration.FromRoundEnd(EndOfHand(), ["Pon!", "Noten...", "Ron!", "Noten..."], 2, PolicyWeights.Default, T0);
        var s = Assert.Single(samples);
        Assert.Equal(2, s.Seat);
        Assert.True(s.Tenpai);
        Assert.Equal("win", s.Source);
    }

    [Fact]
    public void Our_own_win_yields_no_sample()
    {
        Assert.Empty(TenpaiCalibration.FromRoundEnd(EndOfHand(), ["Tsumo!", "Noten...", "Noten...", "Noten..."], 0, PolicyWeights.Default, T0));
    }

    [Fact]
    public void Draw_skips_seats_whose_banner_is_residue()
    {
        var samples = TenpaiCalibration.FromRoundEnd(EndOfHand(), ["Noten...", "Pon!", "Tenpai!", ""], -1, PolicyWeights.Default, T0);
        Assert.Equal([2], samples.Select(s => s.Seat));
    }

    [Theory]
    [InlineData("Tenpai!", true)]
    [InlineData("Noten...", false)]
    [InlineData("Ron!", true)]
    [InlineData("Tsumo!", true)]
    [InlineData("Pon!", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Banner_parsing(string? banner, bool? expected)
    {
        Assert.Equal(expected, TenpaiCalibration.BannerMeansTenpai(banner));
    }

    [Fact]
    public void Draw_banners_complete_needs_all_three_opponents()
    {
        Assert.False(TenpaiCalibration.DrawBannersComplete(["Noten...", "Tenpai!", "Pon!", "Noten..."]));
        Assert.True(TenpaiCalibration.DrawBannersComplete(["", "Tenpai!", "Noten...", "Noten..."]));
    }

    [Fact]
    public void Csv_row_is_invariant_and_matches_the_header_columns()
    {
        var sample = new TenpaiSample(T0, "East", 2, new TenpaiFeatures(12, 1, false, 0.5, 0.25), 0.4321, true, "draw");
        var row = TenpaiCalibration.ToCsv(sample);
        Assert.Equal(TenpaiCalibration.CsvHeader.Split(',').Length, row.Split(',').Length);
        Assert.Equal("2026-09-19T12:00:00.0000000Z,East,2,12,1,0,0.500,0.250,0.432,1,draw", row);
    }
}
