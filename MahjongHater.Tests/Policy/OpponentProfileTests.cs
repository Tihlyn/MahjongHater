using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

// Defense v3, phase 3: novice-like vs steady opponents from their rivers, and the all-last
// value of a trailing riichi.
public class OpponentProfileTests
{
    private static readonly PolicyWeights V3 = PolicyWeights.Default with { DefenseModel = DefenseModel.V3 };

    private static StateSnapshot Hand(string seat1Discards, string seat2Discards, int handNumber) =>
        Seat(Seat(Snap(), 1, seat1Discards), 2, seat2Discards) with { HandNumber = handNumber, RoundWind = Wind.East };

    [Fact]
    public void Random_looking_rivers_read_as_novice_and_tidy_ones_do_not()
    {
        var p = new OpponentProfiles();
        // Seat 1 opens with middles hand after hand (Novice queue, 2026-10-18: 50-83 %);
        // seat 2 throws honors and terminals first (Phoenix: 16 %).
        foreach (var (a, b, hand) in new[] { ("5m4p6s3m7p2z", "1z9m2z1p8s5m", 1), ("6p5s4m7m2z3s", "9p1s3z4z9s6p", 2), ("4s5p3p6m7s1z", "1m2z9s8p1p7z", 3) })
        {
            p.Observe(Hand(a, b, hand));
            p.Observe(Hand("", "", hand + 1));   // next deal: rivers reset, previous hand banked
        }

        Assert.True(p.NoviceLike(1, V3), $"seat 1 at {p.EarlyMiddleRate(1):P0}");
        Assert.False(p.NoviceLike(2, V3), $"seat 2 at {p.EarlyMiddleRate(2):P0}");
        Assert.StartsWith("novice-like", p.Describe(1, V3));
    }

    [Fact]
    public void One_hand_of_middles_is_not_enough_to_call_a_seat_a_novice()
    {
        var p = new OpponentProfiles();
        p.Observe(Hand("5m4p6z1z9s2m", "", 1));   // two middles out of six
        Assert.False(p.NoviceLike(1, V3));
    }

    [Fact]
    public void A_new_match_forgets_the_profiles()
    {
        var p = new OpponentProfiles();
        p.Observe(Hand("5m4p6s3m7p6m", "", 3));
        Assert.True(p.Observed(1) > 0);
        var fresh = Snap() with { HandNumber = 1, RoundWind = Wind.East, Seats = [.. Enumerable.Range(0, 4).Select(i => SeatState.Empty(i) with { Score = 25000 })] };
        p.Observe(fresh);
        Assert.Equal(0, p.Observed(1));
    }

    [Fact]
    public void Early_outside_is_ignored_against_a_novice_like_seat()
    {
        // Seat 1 riichi; 4m among its first discards makes 2m/3m "outside an early discard".
        var tidy = Seat(Snap("123m456m4578p447s1z"), 1, "4m1z9p2z1s9s", riichi: true, riichiIndex: 5);
        var view = TileDangerModel.BuildViews(tidy, new int[34])[1];
        var novice = TileDangerModel.BuildViews(tidy, new int[34], [false, true, false, false])[1];
        var model = new TileDangerModel(weights: V3);
        var a = model.Estimate(Tile.Parse("2m"), view, new int[34], []);
        var b = model.Estimate(Tile.Parse("2m"), novice, new int[34], []);
        Assert.Contains("outside an early discard", a.Why);
        Assert.DoesNotContain("outside an early discard", b.Why);
        Assert.True(b.Probability > a.Probability);
    }

    [Fact]
    public void A_trailing_all_last_riichi_is_worth_what_it_needs()
    {
        var seats = Enumerable.Range(0, 4).Select(i => SeatState.Empty(i) with { Score = new[] { 36000, 21000, 24000, 19000 }[i] }).ToArray();
        seats[3] = seats[3] with { Riichi = true, RiichiDiscardIndex = 0, Discards = TestTiles.Parse("1z") };
        var state = Snap() with { Seats = seats, HandNumber = 4, RoundWind = Wind.South, DealerSeat = 1 };
        Assert.True(state.IsAllLast);
        // 4th on 19 000 needs 2 000 to pass seat 1, ron from anyone.
        Assert.Equal(2000, ThreatValue.PlacementFloor(state.Seats[3], state, V3));
        // 2nd on 24 000 chasing us on 36 000: a ron on us swings twice, so 6 000.
        var second = state.Seats[2] with { Riichi = true, RiichiDiscardIndex = 0, Discards = TestTiles.Parse("1z") };
        Assert.Equal(6000, ThreatValue.PlacementFloor(second, state, V3));
        Assert.Equal(0, ThreatValue.PlacementFloor(second, state, PolicyWeights.Default));
    }
}
