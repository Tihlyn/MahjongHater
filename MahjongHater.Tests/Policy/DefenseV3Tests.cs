using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

// Defense v3: open/dama hand reads, the tsumogiri tenpai tell and the value-aware push cap,
// against the Phoenix-replay counts in resources/policy/open_danger.json.
public class DefenseV3Tests
{
    private static readonly PolicyWeights V2 = PolicyWeights.Default with { DefenseModel = DefenseModel.V2 };
    private static readonly PolicyWeights V3 = PolicyWeights.Default with { DefenseModel = DefenseModel.V3 };

    private static Meld Pon(string t) => Meld.MakePon(Tile.Parse(t), true);

    private static Meld Chi(string tiles) => new(MeldType.Chi, TestTiles.Parse(tiles).ToArray(), true);

    private static double Danger(StateSnapshot state, string tile, PolicyWeights w, int seat = 1) => Model(state, w).Danger(Tile.Parse(tile), seat);

    private static StateSnapshot OpenSeat(string discards, params Meld[] melds) => Seat(Snap("123m456m4578p447s1z"), 1, discards, melds: melds);

    [Theory]
    [InlineData("pin flush by two calls", ReadKind.Flush)]
    [InlineData("honor calls, no pinzu discarded", ReadKind.Flush)]
    [InlineData("two pons", ReadKind.Toitoi)]
    [InlineData("dragon pon", ReadKind.Yakuhai)]
    [InlineData("simple chi and pon", ReadKind.Tanyao)]
    [InlineData("terminal chi", ReadKind.Other)]
    public void Reads_follow_the_counter_rules(string name, ReadKind expected)
    {
        var seat = name switch
        {
            "pin flush by two calls" => SeatState.Empty(1) with { Melds = [Chi("123p"), Pon("7p")], Discards = TestTiles.Parse("1m9s") },
            "honor calls, no pinzu discarded" => SeatState.Empty(1) with { Melds = [Pon("5z")], Discards = TestTiles.Parse("1m9m2s8s4m") },
            "two pons" => SeatState.Empty(1) with { Melds = [Pon("3m"), Pon("6s")], Discards = TestTiles.Parse("1p9p") },
            "dragon pon" => SeatState.Empty(1) with { Melds = [Pon("7z")], Discards = TestTiles.Parse("1p9m5s") },
            "simple chi and pon" => SeatState.Empty(1) with { Melds = [Chi("234m"), Pon("6s")], Discards = TestTiles.Parse("1p9p") },
            _ => SeatState.Empty(1) with { Melds = [Chi("789m"), Chi("123s")], Discards = TestTiles.Parse("5p") },
        };
        var read = HandRead.Of(seat, Wind.South, Wind.East);
        Assert.Equal(expected, read.Kind);
        if (name == "honor calls, no pinzu discarded")
            Assert.Equal(TileSuit.Pin, read.FlushSuit);
    }

    [Fact]
    public void Terminals_and_honors_are_nearly_safe_against_an_open_tanyao()
    {
        // Replays: non-suji 1/9 into a tenpai open tanyao 0.6 % vs 6.5 % into a riichi.
        var state = OpenSeat("1m9m2z3z", Chi("234p"), Pon("6s"));
        Assert.True(Danger(state, "1s", V3) < Danger(state, "1s", V2) * 0.2, "terminal");
        Assert.True(Danger(state, "5s", V3) < Danger(state, "5s", V2) * 1.2, "middle stays close to the riichi rate");
    }

    [Fact]
    public void A_live_honor_is_more_dangerous_against_toitoi()
    {
        // Replays: an honor with only the thrown copy visible hits a toitoi 7.2 % vs 3.2 % for a riichi.
        var state = OpenSeat("1m9m2p8p", Pon("3m"), Pon("6s"));
        Assert.True(Danger(state, "1z", V3) > Danger(state, "1z", V2) * 1.8);
    }

    [Fact]
    public void In_suit_tiles_are_the_danger_against_a_flush_read()
    {
        var state = OpenSeat("1m9m3s8s2m", Chi("123p"), Pon("7p"));
        var inSuit = Danger(state, "5p", V3);
        var offSuit = Danger(state, "5s", V3);
        Assert.True(inSuit > offSuit, $"in-suit {inSuit} vs off-suit {offSuit}");
        Assert.Contains("pin flush read", Model(state, V3).Explain(Tile.Parse("5p"), 1).Why);
    }

    [Fact]
    public void A_riichi_seat_is_read_exactly_as_in_v2()
    {
        var state = Seat(Snap("123m456m4578p447s1z"), 1, "1m9m2m8m1p9p", riichi: true, riichiIndex: 5);
        foreach (var tile in new[] { "1z", "1s", "5s", "3p", "7m" })
            Assert.Equal(Danger(state, tile, V2), Danger(state, tile, V3), 9);
    }

    [Fact]
    public void A_late_tsumogiri_run_raises_the_tenpai_estimate()
    {
        var discards = "1m9m2p8p1z2z3s7s4m6m5p9s";
        var seat = SeatState.Empty(1) with { Melds = [Chi("234m"), Pon("6s")], Discards = TestTiles.Parse(discards) };
        var tedashi = seat with { DiscardTsumogiri = [.. Enumerable.Repeat(false, 12)] };
        var run = seat with { DiscardTsumogiri = [.. Enumerable.Repeat(false, 9), true, true, true] };
        var p = TenpaiEstimator.Estimate(seat, V3);
        Assert.True(TenpaiEstimator.WithTsumogiri(p, run, V3) > p + 0.1);
        Assert.True(TenpaiEstimator.WithTsumogiri(p, tedashi, V3) < p);
        Assert.Equal(p, TenpaiEstimator.WithTsumogiri(p, seat, V3));   // flags unknown: no change
        Assert.Equal(p, TenpaiEstimator.WithTsumogiri(p, run, V2));
    }

    [Fact]
    public void Visible_dora_in_open_melds_raise_the_threat_value()
    {
        // Dora indicator 2p: 3p is dora. A pon of 3p shows three dora.
        var plain = OpenSeat("1m9m2z3z", Pon("6s"), Chi("456m")) with { DoraIndicators = TestTiles.Parse("2p") };
        var dora = OpenSeat("1m9m2z3z", Pon("3p"), Chi("456m")) with { DoraIndicators = TestTiles.Parse("2p") };
        var v3Dora = ThreatValue.Points(dora.Seats[1], dora, V3);
        Assert.True(v3Dora > ThreatValue.Points(plain.Seats[1], plain, V3) * 1.8);
        Assert.True(v3Dora >= ThreatValue.Points(dora.Seats[1], dora, V2));
    }

    // 2026-10-18 08:04:23: 2-shanten worth 1 000 against the dealer's two-call ~10 000 at 66 %
    // tenpai ran a 7 % budget and pushed.
    [Fact]
    public void A_slow_cheap_hand_does_not_push_into_an_expensive_open_dealer()
    {
        var state = Seat(Snap("123m456m4578p447s1z"), 2, "1m9m1z2z9p1p3z4z8s7z6z5z", melds: [Pon("7z"), Pon("3p")])
            with { DealerSeat = 2, DoraIndicators = TestTiles.Parse("2p"), WallRemaining = 30 };
        var slow = Candidate(shanten: 2, ukeire: 17) with { ValuePoints = 1000, WinProbability = 0.05 };
        var v2 = new PushFoldPolicy(V2).Decide(state, Model(state, V2), [slow]);
        var v3 = new PushFoldPolicy(V3).Decide(state, Model(state, V3), [slow]);
        Assert.True(v3.MaxDanger < 0.01, v3.Reason.Display);
        Assert.True(v3.MaxDanger < v2.MaxDanger);
        Assert.Contains("EV cap", v3.Reason.Display);

        // A tenpai hand keeps the table's budget.
        var tenpai = Candidate(shanten: 0, ukeire: 8) with { ValuePoints = 3900, MinPoints = 3900, WinProbability = 0.4 };
        Assert.DoesNotContain("EV cap", new PushFoldPolicy(V3).Decide(state, Model(state, V3), [tenpai]).Reason.Display);
    }

    // Against a novice-like dealer the same slow hand keeps the table's budget: their calls are
    // cheap and often noten, and the arena placed better without the cap in that field.
    [Fact]
    public void The_EV_cap_does_not_apply_against_a_novice_like_threat()
    {
        var state = Seat(Snap("123m456m4578p447s1z"), 2, "4m5p6s3m7p5s1z2z9p1p3z4z", melds: [Pon("7z"), Pon("3p")])
            with { DealerSeat = 2, DoraIndicators = TestTiles.Parse("2p"), WallRemaining = 30 };
        var model = Model(state, V3);
        Assert.True(model.NoviceLike(2), model.Profile(2));
        var slow = Candidate(shanten: 2, ukeire: 17) with { ValuePoints = 1000, WinProbability = 0.05 };
        Assert.DoesNotContain("EV cap", new PushFoldPolicy(V3).Decide(state, model, [slow]).Reason.Display);
        var capped = V3 with { PushEvCapNoviceWeight = 1 };
        Assert.Contains("EV cap", new PushFoldPolicy(capped).Decide(state, Model(state, capped), [slow]).Reason.Display);
    }

    [Fact]
    public void A_steady_threat_behind_a_novice_keeps_the_EV_cap()
    {
        var state = Seat(Seat(Snap("123m456m4578p447s1z"), 2, "4m5p6s3m7p5s1z2z9p1p3z4z", riichi: true, riichiIndex: 11), 1,
                "1m9m1z2z9p1p3z4z8s7z5z", melds: [Pon("7z"), Pon("3p"), Pon("6z")])
            with { DealerSeat = 2, DoraIndicators = TestTiles.Parse("2p"), WallRemaining = 30 };
        var model = Model(state, V3);
        Assert.True(model.NoviceLike(2) && !model.NoviceLike(1), $"{model.Profile(1)} / {model.Profile(2)}");
        Assert.True(model.TenpaiProbability(1) >= V3.ThreatTenpaiFloor);
        var slow = Candidate(shanten: 2, ukeire: 17) with { ValuePoints = 1000, WinProbability = 0.05 };
        Assert.Contains("EV cap", new PushFoldPolicy(V3).Decide(state, model, [slow]).Reason.Display);
    }
}
