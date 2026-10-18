using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

// Defense v3: the push budget is spent across every threat, each weighted by tenpai × value
// against the primary, instead of every threat being held to the primary's line.
public class SeatBudgetTests
{
    private static readonly PolicyWeights V3 = PolicyWeights.Default with { DefenseModel = DefenseModel.V3 };

    // Fixed numbers: seat → (tenpai, value), and a danger per (tile, seat).
    private sealed class Fixed(int primary, Dictionary<int, (double Tenpai, double Value)> seats, Func<Tile, int, double> danger) : IOpponentModel
    {
        public void Update(StateSnapshot state) { }
        public double TenpaiProbability(int seat) => seats.TryGetValue(seat, out var s) ? s.Tenpai : 0;
        public double Danger(Tile tile, int seat) => danger(tile, seat);
        public double ExpectedDealInCost(Tile tile) => seats.Sum(s => s.Value.Tenpai * danger(tile, s.Key) * s.Value.Value);
        public int PrimaryThreat() => primary;
        public double Value(int seat) => seats.TryGetValue(seat, out var s) ? s.Value : 0;
    }

    private static StateSnapshot Riichi(StateSnapshot state, int seat) => Seat(state, seat, "1z9m2z1p8s", riichi: true, riichiIndex: 4);

    // 1z, 1m and 2m are safe against everyone, so the hand can fold.
    private static bool Safe(Tile t) => t == Tile.Parse("1z") || t == Tile.Parse("1m") || t == Tile.Parse("2m");

    private static Meld Pon(string t) => Meld.MakePon(Tile.Parse(t), true);

    private static StateSnapshot Board(string hand, int turn)
    {
        var state = Snap(hand) with { DealerSeat = 3, WallRemaining = 70 - 4 * (turn - 1) };
        var seats = state.Seats.ToArray();
        seats[0] = seats[0] with { DiscardCount = turn - 1 };
        return state with { Seats = seats };
    }

    // Mid-game, bad wait, 1 000: a budget of a few percent.
    private static readonly DiscardCandidate CheapTenpai = Candidate(shanten: 0, ukeire: 2) with { ValuePoints = 1300, MinPoints = 1000, WinProbability = 0.2 };

    [Fact]
    public void A_cheap_open_hand_behind_a_riichi_counts_at_its_share()
    {
        // Seat 1 riichi ~6 000; seat 2 open, 30 % tenpai, ~2 000. 5s is genbutsu to the riichi
        // and 12 % against the open hand: the old rule held it to the riichi's line.
        var state = Seat(Riichi(Board("123m456m4578p447s1z", turn: 10), 1), 2, "1m9p3z4z6z", melds: [Pon("7z")]);
        var opponents = new Fixed(1, new() { [1] = (1, 6000), [2] = (0.3, 2000) },
            (tile, seat) => Safe(tile) ? 0 : seat == 1 ? (tile == Tile.Parse("5s") ? 0 : 0.1) : 0.12);
        var v3 = new PushFoldPolicy(V3).Decide(state, opponents, [CheapTenpai]);
        var old = new PushFoldPolicy(V3 with { SeatBudget = false }).Decide(state, opponents, [CheapTenpai]);
        Assert.True(v3.MaxDanger < 0.12 && old.MaxDanger < 0.12, v3.Reason.Display);
        Assert.False(old.Allows(Tile.Parse("5s"), opponents));
        Assert.True(v3.Allows(Tile.Parse("5s"), opponents), v3.Reason.Display);   // 0.12 × 0.3 × 2 000 / 6 000 = 1.2 %
        Assert.Equal(0.1, v3.SeatWeights![2], 6);
        Assert.Contains("summed over seat 2", v3.Reason.Display);
    }

    [Fact]
    public void Two_equal_riichi_halve_the_budget_as_before()
    {
        var state = Riichi(Riichi(Board("123m456m4578p447s1z", turn: 10), 1), 2);
        var opponents = new Fixed(1, new() { [1] = (1, 6000), [2] = (1, 6000) }, (t, _) => Safe(t) ? 0 : 0.04);
        var v3 = new PushFoldPolicy(V3).Decide(state, opponents, [CheapTenpai]);
        var old = new PushFoldPolicy(V3 with { SeatBudget = false }).Decide(state, opponents, [CheapTenpai]);
        Assert.Equal(old.MaxDanger * 2, v3.MaxDanger, 6);   // no flat halving...
        foreach (var d in new[] { 0.01, 0.04, 0.08 })
        {
            var o = new Fixed(1, new() { [1] = (1, 6000), [2] = (1, 6000) }, (t, _) => Safe(t) ? 0 : d);
            Assert.Equal(old.Allows(Tile.Parse("5s"), o), v3.Allows(Tile.Parse("5s"), o));   // ...the sum does it
        }
    }

    // NoviceLike from a fixed set of seats on top of Fixed.
    private sealed class Profiled(HashSet<int> novices) : IOpponentModel
    {
        public void Update(StateSnapshot state) { }
        public double TenpaiProbability(int seat) => 0;
        public double Danger(Tile tile, int seat) => 0;
        public double ExpectedDealInCost(Tile tile) => 0;
        public bool NoviceLike(int seat) => novices.Contains(seat);
        public double NoviceProbability(int seat) => novices.Contains(seat) ? 0.9 : 0.05;
    }

    [Fact]
    public void A_mangan_dama_stays_dama_only_when_the_table_folds()
    {
        var state = Snap("123m456m789m4467p1z", LegalAction.Discard | LegalAction.Riichi) with { DealerSeat = 2, WallRemaining = 50 };
        var mangan = Candidate("1z", shanten: 0, ukeire: 8) with { ValuePoints = 12000, MinPoints = 12000, DamaMinPoints = 8000 };
        var policy = new RiichiPolicy(V3);
        Assert.False(policy.ShouldDeclare(state, new Profiled([]), mangan, out var dama));
        Assert.StartsWith("Dama", dama.Display);
        Assert.True(policy.ShouldDeclare(state, new Profiled([2]), mangan, out _));             // a novice-like seat keeps pushing
        Assert.True(policy.ShouldDeclare(state, new Profiled([]), mangan with { DamaMinPoints = 3900 }, out _));
        Assert.True(policy.ShouldDeclare(state, new Profiled([]), mangan with { Ukeire = 3 }, out _));   // bad wait: riichi
        Assert.True(new RiichiPolicy(V3 with { DamaVsFoldersPoints = 0 }).ShouldDeclare(state, new Profiled([]), mangan, out _));
    }
}
