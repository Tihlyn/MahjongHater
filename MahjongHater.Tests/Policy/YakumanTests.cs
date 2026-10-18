using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

// Defense v3, phase 2: yakuman hands on both sides of the table. Detection and the win gate
// were already right for all twelve Doman yakuman (probe, 2026-10-18); what failed live was
// valuing, steering, pushing and calling with them, and defending against a visible one.
public class YakumanTests
{
    private static readonly PolicyWeights V2 = PolicyWeights.Default with { DefenseModel = DefenseModel.V2 };
    private static readonly PolicyWeights V3 = PolicyWeights.Default with { DefenseModel = DefenseModel.V3 };

    private static Meld Pon(string t) => Meld.MakePon(Tile.Parse(t), true);

    private static Meld Chi(string tiles) => new(MeldType.Chi, TestTiles.Parse(tiles).ToArray(), true);

    [Theory]
    [InlineData("55566677z11m234p", "", "Daisangen")]
    [InlineData("66677z11m234p", "5z", "Daisangen")]
    [InlineData("111222333z44z56m", "", "Shousuushii")]
    [InlineData("11z22z33z44z56m", "1z", "Shousuushii")]
    [InlineData("1122334455667z", "", "Tsuuiisou")]
    [InlineData("19m19p19s12345z5m", "", "Kokushi Musou")]
    [InlineData("111m333p555s77z99s", "", "Suuankou")]
    [InlineData("1112345678999m", "", "Chuuren Poutou")]
    [InlineData("22334466888s66z", "", "Ryuuiisou")]
    public void Routes_are_recognised(string closed, string pon, string expected)
    {
        var melds = pon.Length == 0 ? Array.Empty<Meld>() : [Pon(pon)];
        Assert.Equal(expected, YakumanRoutes.Of(TestTiles.Parse(closed), melds)?.Name);
    }

    [Theory]
    [InlineData("123m456m4578p447s")]   // an ordinary hand
    [InlineData("55566z7z11m234p678s")] // two dragon sets, third dragon single
    [InlineData("111z22z456m789p11s")]  // one wind set and a wind pair
    public void Ordinary_hands_have_no_route(string closed) =>
        Assert.Null(YakumanRoutes.Of(TestTiles.Parse(closed), []));

    // The probe hand: East triplet, three wind pairs, a 6z pon. Tile efficiency threw a wind
    // pair (2z) to keep 5m-6m; with the route valued, a manzu tile goes instead.
    [Fact]
    public void An_open_four_winds_shape_keeps_its_wind_pairs()
    {
        var state = Snap("111z22z33z44z56m") with { OurMelds = [Pon("6z")], DrawnTile = Tile.Parse("6m") };
        var v3 = new HeuristicDiscardPolicy(weights: V3).Rank(state, Model(state, V3), default);
        Assert.False(v3[0].Tile.IsHonor, $"v3 threw {v3[0].Tile}");
        Assert.Equal("Shousuushii", v3[0].Yakuman?.Name);
    }

    [Fact]
    public void A_yakuman_route_is_valued_as_one_before_tenpai()
    {
        var state = Snap("555666z77z23m456p9s");
        var candidates = new HeuristicDiscardPolicy(weights: V3).Rank(state, Model(state, V3), default);
        var keep = candidates.First(c => c.Yakuman is not null);
        Assert.True(keep.ValuePoints >= 32000 || keep.ShantenAfter == 0, $"{keep.Tile}: {keep.ValuePoints}");
    }

    [Fact]
    public void Yakuman_tenpai_pushes_any_tile_against_a_riichi()
    {
        var state = Seat(Snap("123m456m4578p447s1z"), 1, "1m9m2m8m1p9p", riichi: true, riichiIndex: 5) with { WallRemaining = 30 };
        var tenpai = Candidate(shanten: 0, ukeire: 3) with { ValuePoints = 32000, MinPoints = 32000, Yakuman = new YakumanRoute("Daisangen", 1) };
        Assert.Equal(1, new PushFoldPolicy(V3).Decide(state, Model(state, V3), [tenpai]).MaxDanger, 6);
        Assert.True(new PushFoldPolicy(V2).Decide(state, Model(state, V2), [tenpai]).MaxDanger < 0.2);
    }

    [Fact]
    public void A_yakuman_one_shanten_is_lifted_off_the_riichi_tables()
    {
        var state = Seat(Snap("123m456m4578p447s1z"), 1, "1m9m2m8m1p9p", riichi: true, riichiIndex: 5) with { WallRemaining = 30 };
        var slow = Candidate(shanten: 1, ukeire: 6) with { ValuePoints = 32000, WinProbability = 0.15 };
        var v2 = new PushFoldPolicy(V2).Decide(state, Model(state, V2), [slow]);
        var v3 = new PushFoldPolicy(V3).Decide(state, Model(state, V3), [slow]);
        Assert.True(v3.MaxDanger > v2.MaxDanger, $"v3 {v3.MaxDanger} vs v2 {v2.MaxDanger}");
        Assert.Contains("EV raise", v3.Reason.Display);
    }

    [Fact]
    public void Yakuman_on_every_wait_stays_dama()
    {
        var state = Snap("111m333p555s999s7z1m", LegalAction.Discard | LegalAction.Riichi) with { WallRemaining = 30, DealerSeat = 2 };
        var best = Candidate("1m", shanten: 0, ukeire: 3) with { ValuePoints = 32000, MinPoints = 32000 };
        Assert.False(new RiichiPolicy(V3).ShouldDeclare(state, Model(state, V3), best, out var why));
        Assert.Contains("dama", why.Display);
        Assert.True(new RiichiPolicy(V2).ShouldDeclare(state, Model(state, V2), best, out _));
    }

    [Fact]
    public void The_third_dragon_is_a_top_danger_tile_against_two_dragon_sets()
    {
        var state = Seat(Snap("123m456m4578p447s7z"), 2, "1m9m2p8p3s4s5m6m", melds: [Pon("5z"), Pon("6z")]);
        var v3 = Model(state, V3).Explain(Tile.Parse("7z"), 2);
        var v2 = Model(state, V2).Explain(Tile.Parse("7z"), 2);
        Assert.True(v3.Probability > v2.Probability * 3, $"v3 {v3.Probability} vs v2 {v2.Probability}");
        Assert.True(v3.Rank >= DangerRank.E, v3.Rank.ToString());
        Assert.Contains("Daisangen", v3.Why);
        Assert.Contains("pao", v3.Why);
    }

    [Fact]
    public void A_fourth_visible_copy_clears_the_yakuman_tile()
    {
        var state = Seat(Snap("123m456m4578p447s7z"), 2, "7z7z7z1m", melds: [Pon("5z"), Pon("6z")]);
        Assert.Equal(0, Model(state, V3).Danger(Tile.Parse("7z"), 2));
    }
}
