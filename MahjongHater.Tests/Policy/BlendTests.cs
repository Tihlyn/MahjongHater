using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

// Defense v3 blend: profiles and hand reads as probabilities instead of switches, every seat
// in the budget, and a no-safe-tile retreat priced against the attack.
public class BlendTests
{
    private static readonly PolicyWeights V3 = PolicyWeights.Default with { DefenseModel = DefenseModel.V3 };

    private static Meld Pon(string t) => Meld.MakePon(Tile.Parse(t), true);

    [Fact]
    public void Danger_runs_from_the_steady_to_the_novice_reading_and_leans_cautious_when_undecided()
    {
        // Riichi seat; 4m early makes 2m "outside an early discard" for a steady player only.
        var state = Seat(Snap("123m456m4578p447s1z"), 1, "4m1z9p2z1s9s", riichi: true, riichiIndex: 5);
        var model = new TileDangerModel(weights: V3);
        double At(double q) => model.Estimate(Tile.Parse("2m"),
            TileDangerModel.BuildViews(state, new int[34], null, [0, q, 0, 0], readMixture: true)[1], new int[34], []).Probability;
        var steady = At(0);
        var novice = At(1);
        Assert.True(novice > steady);
        Assert.Equal(novice, At(1 - 1e-12), 6);
        var half = At(0.5);
        var mix = (steady + novice) / 2;
        Assert.Equal(mix + V3.ProfileCaution * (novice - mix), half, 9);   // 4q(1 - q) = 1 at 50 %
        Assert.True(At(0.25) > steady && At(0.25) < half && half < At(0.75) && At(0.75) < novice);
        Assert.Equal(mix, new TileDangerModel(weights: V3 with { ProfileCaution = 0 }).Estimate(Tile.Parse("2m"),
            TileDangerModel.BuildViews(state, new int[34], null, [0, 0.5, 0, 0], readMixture: true)[1], new int[34], []).Probability, 9);
    }

    [Fact]
    public void A_river_flush_read_fades_with_each_in_suit_discard_instead_of_flipping()
    {
        // Dragon pon; number discards avoid pinzu. Each extra pinzu discard lowers the flush
        // weight; the hard read flips at 15 % in one step.
        double Flush(string discards)
        {
            var seat = new SeatState(1, TestTiles.Parse(discards), [Pon("7z")], false, -1, 25000);
            var reads = HandRead.Alternatives(seat, Wind.South, Wind.East);
            Assert.Equal(1, reads.Sum(r => r.Weight), 9);
            return reads.Where(r => r.Read.Kind == ReadKind.Flush).Sum(r => r.Weight);
        }

        var clean = Flush("1m9m2m8m1s9s2s8s3m7m");
        var one = Flush("1m9m2m8m1s9s2s8s3m1p");
        var two = Flush("1m9m2m8m1s9s2s8s1p2p");
        var three = Flush("1m9m2m8m1s9s2s1p2p3p");
        Assert.Equal(1, clean, 9);
        Assert.True(one < clean && two < one && three < two, $"{clean} {one} {two} {three}");
        Assert.True(two > 0 && two < 1);
        Assert.Equal(0, three, 9);
    }

    [Fact]
    public void Two_calls_in_one_suit_stay_a_certain_flush()
    {
        var seat = new SeatState(1, TestTiles.Parse("1m9m2m8m1s9s1p2p3p"), [Pon("5p"), Pon("8p")], false, -1, 25000);
        var reads = HandRead.Alternatives(seat, Wind.South, Wind.East);
        Assert.Single(reads);
        Assert.Equal(ReadKind.Flush, reads[0].Read.Kind);
    }

    // Fixed tenpai/value per seat and a danger per (tile, seat).
    private sealed class Fixed(int primary, Dictionary<int, (double Tenpai, double Value)> seats, Func<Tile, int, double> danger) : IOpponentModel
    {
        public void Update(StateSnapshot state) { }
        public double TenpaiProbability(int seat) => seats.TryGetValue(seat, out var s) ? s.Tenpai : 0;
        public double Danger(Tile tile, int seat) => danger(tile, seat);
        public double ExpectedDealInCost(Tile tile) => seats.Sum(s => s.Value.Tenpai * danger(tile, s.Key) * s.Value.Value);
        public int PrimaryThreat() => primary;
        public double Value(int seat) => seats.TryGetValue(seat, out var s) ? s.Value : 0;
        public double NoviceProbability(int seat) => 0.05;
    }

    private static StateSnapshot Board(string hand, int turn)
    {
        var state = Snap(hand) with { DealerSeat = 3, WallRemaining = 70 - 4 * (turn - 1) };
        var seats = state.Seats.ToArray();
        seats[0] = seats[0] with { DiscardCount = turn - 1 };
        seats[1] = new SeatState(1, TestTiles.Parse("1z9m2z1p8s"), [], true, 4, 25000);
        return state with { Seats = seats };
    }

    [Fact]
    public void A_seat_below_the_tenpai_floor_still_counts_at_its_share()
    {
        var state = Board("123m456m4578p447s1z", turn: 10);
        var opponents = new Fixed(1, new() { [1] = (1, 6000), [2] = (0.15, 8000) }, (t, s) => t == Tile.Parse("1z") ? 0 : 0.05);
        var cheap = Candidate(shanten: 0, ukeire: 2) with { ValuePoints = 1300, MinPoints = 1000, WinProbability = 0.2 };
        var continuous = new PushFoldPolicy(V3 with { ContinuousThreats = true }).Decide(state, opponents, [cheap]);
        var floored = new PushFoldPolicy(V3).Decide(state, opponents, [cheap]);
        Assert.Equal(0.15 * 8000 / 6000, continuous.SeatWeights![2], 6);
        Assert.False(floored.SeatWeights!.ContainsKey(2));
        Assert.DoesNotContain(2, continuous.ThreatSeats);   // the floor still decides who is a threat
    }

    [Fact]
    public void With_no_safe_tile_the_least_costly_retreat_is_weighed_against_the_attack()
    {
        // Every tile is past rank B against the riichi: the old rule pushed anything.
        var state = Board("123m456m4578p447s1z", turn: 10);
        var opponents = new Fixed(1, new() { [1] = (1, 7000) }, (t, _) => t == Tile.Parse("9p") ? 0.04 : t == Tile.Parse("5s") ? 0.06 : 0.14);
        var attack = Candidate("5m", shanten: 2, ukeire: 20) with { ValuePoints = 1000, WinProbability = 0.04 };
        var middle = Candidate("5s", shanten: 2, ukeire: 18) with { ValuePoints = 1000, WinProbability = 0.04 };
        var retreat = Candidate("9p", shanten: 3, ukeire: 30) with { ValuePoints = 1000, WinProbability = 0.01 };
        var v3 = new PushFoldPolicy(V3).Decide(state, opponents, [attack, middle, retreat]);
        var old = new PushFoldPolicy(V3 with { RetreatEv = false }).Decide(state, opponents, [attack, middle, retreat]);
        Assert.Equal(0, v3.SafeTiles);
        Assert.Equal(1, old.MaxDanger);
        Assert.Contains("retreat 9p", v3.Reason.Display);
        Assert.True(v3.Allows(Tile.Parse("9p"), opponents));
        Assert.False(v3.Allows(Tile.Parse("5m"), opponents), v3.Reason.Display);   // 1 000 at 4 % buys ~10 points per turn, not 7 % more danger

        // A hand worth a lot buys the push.
        var big = attack with { ValuePoints = 18000, WinProbability = 0.35 };
        Assert.True(new PushFoldPolicy(V3).Decide(state, opponents, [big, middle, retreat]).Allows(Tile.Parse("5m"), opponents));
    }

    [Fact]
    public void Dama_follows_the_chance_that_the_whole_table_folds()
    {
        var state = Snap("123m456m789m4467p1z", LegalAction.Discard | LegalAction.Riichi) with { DealerSeat = 2, WallRemaining = 50 };
        var mangan = Candidate("1z", shanten: 0, ukeire: 8) with { ValuePoints = 12000, MinPoints = 12000, DamaMinPoints = 8000 };
        var policy = new RiichiPolicy(V3);
        Assert.True(policy.ShouldDeclare(state, new Profiled(0.25), mangan, out _));    // unknown table: 42 % all steady
        Assert.False(policy.ShouldDeclare(state, new Profiled(0.08), mangan, out var dama));
        Assert.Contains("all steady", dama.Display);
    }

    private sealed class Profiled(double q) : IOpponentModel
    {
        public void Update(StateSnapshot state) { }
        public double TenpaiProbability(int seat) => 0;
        public double Danger(Tile tile, int seat) => 0;
        public double ExpectedDealInCost(Tile tile) => 0;
        public double NoviceProbability(int seat) => q;
    }
}
