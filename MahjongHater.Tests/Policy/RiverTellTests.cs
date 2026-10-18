using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

// Defense v3 against the standard defensive curriculum: keeping a safe tile, the "just got
// tenpai" cut, the riichi tile's neighbourhood, ippatsu/houtei value and a kokushi river.
public class RiverTellTests
{
    private static readonly PolicyWeights V2 = PolicyWeights.Default with { DefenseModel = DefenseModel.V2 };
    private static readonly PolicyWeights V3 = PolicyWeights.Default with { DefenseModel = DefenseModel.V3 };

    private static SeatState Seat1(string discards, bool lastTsumogiri) => SeatState.Empty(1) with
    {
        Discards = TestTiles.Parse(discards),
        DiscardTsumogiri = [.. Enumerable.Repeat(false, TestTiles.Parse(discards).Count - 1), lastTsumogiri],
    };

    [Fact]
    public void A_hand_cut_dora_yakuhai_or_late_middle_is_a_tenpai_cut()
    {
        var state = Snap() with { DoraIndicators = TestTiles.Parse("4p"), DealerSeat = 0, RoundWind = Wind.East };
        Assert.Equal(RiverCut.Dora, TenpaiEstimator.LastCut(Seat1("1m9m5p", false), state));
        Assert.Equal(RiverCut.None, TenpaiEstimator.LastCut(Seat1("1m9m5p", true), state));     // tsumogiri says nothing
        Assert.Equal(RiverCut.Yakuhai, TenpaiEstimator.LastCut(Seat1("1m9m7z", false), state));  // live dragon
        Assert.Equal(RiverCut.Middle, TenpaiEstimator.LastCut(Seat1("1m9m1z2z9s1p4m", false), state));
        Assert.Equal(RiverCut.None, TenpaiEstimator.LastCut(Seat1("1m9m4m", false), state));     // early middle is normal
    }

    [Fact]
    public void A_tenpai_cut_raises_the_estimate()
    {
        var state = Snap() with { DoraIndicators = TestTiles.Parse("4p") };
        var plain = Seat1("1m9m1z2z9s1p3s8s1s", false) with { Melds = [Meld.MakePon(Tile.Parse("6z"), true)] };
        var dora = Seat1("1m9m1z2z9s1p3s8s5p", false) with { Melds = [Meld.MakePon(Tile.Parse("6z"), true)] };
        var p = 0.4;
        Assert.True(TenpaiEstimator.WithRiverTells(p, dora, state, V3) > TenpaiEstimator.WithRiverTells(p, plain, state, V3));
    }

    [Fact]
    public void A_riichi_tile_changes_the_danger_of_its_neighbours()
    {
        // Riichi on a hand-cut 5m (seventh discard). 4m/6m straddle it; 9p is unrelated.
        var discards = "1z9p2z1s9s3z5m";
        var tedashi = Seat(Snap("123m456m4578p447s1z"), 1, discards, riichi: true, riichiIndex: 6);
        var seats = tedashi.Seats.ToArray();
        seats[1] = seats[1] with { DiscardTsumogiri = [false, false, false, false, false, false, false] };
        var hand = tedashi with { Seats = seats };
        var explained = Model(hand, V3).Explain(Tile.Parse("4m"), 1);
        Assert.NotEqual(Model(hand, V2).Danger(Tile.Parse("4m"), 1), explained.Probability);
        Assert.Contains("riichi tile", explained.Why);
        Assert.Equal(Model(hand, V2).Danger(Tile.Parse("8p"), 1), Model(hand, V3).Danger(Tile.Parse("8p"), 1), 9);
    }

    [Fact]
    public void Ippatsu_and_the_last_discard_add_value()
    {
        var state = Seat(Snap(), 1, "1z9p2z1s", riichi: true, riichiIndex: 3) with { WallRemaining = 20 };
        Assert.Equal(V3.IppatsuValueFactor, ThreatValue.SituationFactor(state.Seats[1], state, V3), 6);
        var later = Seat(Snap(), 1, "1z9p2z1s5m", riichi: true, riichiIndex: 3) with { WallRemaining = 0 };
        Assert.Equal(V3.HouteiValueFactor, ThreatValue.SituationFactor(later.Seats[1], later, V3), 6);
        Assert.Equal(1, ThreatValue.SituationFactor(later.Seats[1], later, V2));
    }

    [Fact]
    public void A_kokushi_shaped_river_makes_orphans_dangerous()
    {
        var state = Seat(Snap("123m456m4578p447s1z"), 2, "5m4p6s3m7p2s5s6m");
        Assert.True(OpponentModel.KokushiRead(state.Seats[2]));
        var v3 = Model(state, V3).Explain(Tile.Parse("1z"), 2);
        Assert.Contains("Kokushi", v3.Class);
        Assert.True(v3.Probability > Model(state, V2).Danger(Tile.Parse("1z"), 2));
        Assert.False(OpponentModel.KokushiRead(state.Seats[2] with { Melds = [Meld.MakePon(Tile.Parse("5p"), true)] }));
    }

    [Fact]
    public void The_last_safe_tile_is_kept_mid_hand()
    {
        // 1-shanten; 7z (two copies out) and 1s are both dead weight. V3 throws 1s.
        var state = Seat(Seat(Snap("123m456m78p44s67s7z1s"), 1, "7z9m1p2z8s"), 2, "7z1m9s3z9p") with { WallRemaining = 40 };
        var seats = state.Seats.ToArray();
        seats[0] = seats[0] with { Discards = TestTiles.Parse("2z3z4z9m8p1p9s"), DiscardCount = 7 };
        state = state with { Seats = seats };
        var v3 = new HeuristicDiscardPolicy(weights: V3).Rank(state, Model(state, V3), default);
        Assert.NotEqual(Tile.Parse("7z"), v3[0].Tile);
        Assert.Contains(v3, c => c.Tile == Tile.Parse("7z") && c.Note.Contains("last safe tile"));
    }
}
