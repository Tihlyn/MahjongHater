using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

// The danger budget (docs/DEFENSE_PLAN.md §3.4) transcribed from Fukuchi ch. 2 and the
// fold-mode ordering (§3.5).
public class PushFoldBudgetTests
{
    private static readonly PolicyWeights W = PolicyWeights.Default;

    // A mid-game board: we have discarded `turn - 1` tiles; seat `riichiSeat` is in riichi
    // with six discards (riichi tile 9p). Hand tiles decide how many safe tiles we hold.
    private static StateSnapshot Board(string hand, int turn = 8, int riichiSeat = 1, int dealerSeat = 3, string ourDiscards = "")
    {
        var state = Seat(Snap(hand), riichiSeat, "1m9m2m8m1p9p", riichi: true, riichiIndex: 5) with { DealerSeat = dealerSeat, WallRemaining = 70 - 4 * (turn - 1) };
        var seats = state.Seats.ToArray();
        seats[0] = seats[0] with { DiscardCount = turn - 1, Discards = TestTiles.Parse(ourDiscards) };
        return state with { Seats = seats };
    }

    private static PushFoldDecision Decide(StateSnapshot state, DiscardCandidate best)
    {
        var model = Model(state);
        return new PushFoldPolicy().Decide(state, model, [best]);
    }

    private static DiscardCandidate Tenpai(int ukeire, double points) =>
        Candidate(shanten: 0, ukeire: ukeire) with { ValuePoints = points, MinPoints = (int)points };

    [Fact]
    public void No_threat_means_no_budget_limit()
    {
        var state = Snap("123m456m4578p447s1z");
        var decision = new PushFoldPolicy().Decide(state, Model(state), [Tenpai(8, 2000)]);
        Assert.True(decision.NoThreat);
        Assert.Equal(1, decision.MaxDanger);
    }

    [Fact]
    public void Good_wait_tenpai_early_pushes_everything()
    {
        var decision = Decide(Board("123m456m4578p447s1z", turn: 4), Tenpai(8, 1300));
        Assert.Equal(1, decision.MaxDanger);
        Assert.Contains("early", decision.Reason.Display);
    }

    [Fact]
    public void Good_wait_tenpai_mid_game_needs_2000_for_a_ten_percent_tile()
    {
        var cheap = Decide(Board("123m456m4578p447s1z", turn: 8), Tenpai(8, 1300));
        var worth = Decide(Board("123m456m4578p447s1z", turn: 8), Tenpai(8, 2000));
        Assert.Equal(W.BudgetFivePercent, cheap.MaxDanger, 6);
        Assert.Equal(W.BudgetTenPercent, worth.MaxDanger, 6);
    }

    [Fact]
    public void Bad_wait_tenpai_late_game_needs_5200_for_a_ten_percent_tile()
    {
        var board = Board("123m456m4578p447s1z", turn: 13);
        Assert.Equal(W.RankBMax, Decide(board, Tenpai(3, 2000)).MaxDanger, 6);           // below 2600 riichi: safe tiles only
        Assert.Equal(W.BudgetFivePercent, Decide(board, Tenpai(3, 3900)).MaxDanger, 6);
        Assert.Equal(W.BudgetTenPercent, Decide(board, Tenpai(3, 5200)).MaxDanger, 6);
    }

    [Fact]
    public void Dealer_riichi_raises_the_bar()
    {
        var vsNonDealer = Decide(Board("123m456m4578p447s1z", turn: 13, riichiSeat: 1, dealerSeat: 3), Tenpai(8, 2000));
        var vsDealer = Decide(Board("123m456m4578p447s1z", turn: 13, riichiSeat: 1, dealerSeat: 1), Tenpai(8, 2000));
        Assert.True(vsDealer.MaxDanger < vsNonDealer.MaxDanger, $"{vsDealer.MaxDanger} vs {vsNonDealer.MaxDanger}");
        Assert.Contains("dealer riichi", vsDealer.Reason.Display);
    }

    [Fact]
    public void We_as_dealer_push_a_good_wait()
    {
        var decision = Decide(Board("123m456m4578p447s1z", turn: 9, dealerSeat: 0), Tenpai(8, 1300));
        Assert.Equal(1, decision.MaxDanger);
    }

    [Fact]
    public void One_shanten_is_gated_by_tenpai_chance_and_value()
    {
        var board = Board("123m456m4578p447s1z", turn: 8);
        var narrow = Candidate(shanten: 1, ukeire: 8) with { ValuePoints = 3900 };
        var wide = Candidate(shanten: 1, ukeire: 16) with { ValuePoints = 3900 };
        var wideMangan = Candidate(shanten: 1, ukeire: 16) with { ValuePoints = 8000 };
        var veryWide = Candidate(shanten: 1, ukeire: 28) with { ValuePoints = 3900 };
        Assert.Equal(W.RankBMax, Decide(board, narrow).MaxDanger, 6);
        Assert.Equal(W.RankBMax, Decide(board, wide).MaxDanger, 6);
        Assert.Equal(W.BudgetSevenPercent, Decide(board, wideMangan).MaxDanger, 6);
        // ≥ 20 % tenpai chance plays like a bad-wait tenpai: 3 900 mid-game cuts a 10 % tile.
        Assert.Equal(W.BudgetTenPercent, Decide(board, veryWide).MaxDanger, 6);
        Assert.Contains("wide 1-shanten", Decide(board, veryWide).Reason.Display);
    }

    [Fact]
    public void Two_shanten_only_cuts_safe_tiles()
    {
        var decision = Decide(Board("123m456m4578p447s1z", turn: 8), Candidate(shanten: 2, ukeire: 30) with { ValuePoints = 5200 });
        Assert.Equal(W.RankBMax, decision.MaxDanger, 6);
    }

    [Fact]
    public void Without_safe_tiles_we_attack_and_with_one_we_do_not_fold_mid_game()
    {
        // Every tile is a raw middle tile against the riichi seat: nothing at rank B.
        var none = Decide(Board("34m46m34p67p5p46s7s3s", turn: 8), Candidate(shanten: 2, ukeire: 10) with { ValuePoints = 1300 });
        Assert.Equal(0, none.SafeTiles);
        Assert.Equal(1, none.MaxDanger);
        // One guest wind (North: the dealer is seat 3, so seat 1 is West) is the only safe
        // tile; the budget floors at 5 %.
        var one = Decide(Board("34m46m34p67p5p46s7s4z", turn: 8), Candidate(shanten: 2, ukeire: 10) with { ValuePoints = 1300 });
        Assert.Equal(1, one.SafeTiles);
        Assert.Equal(W.BudgetFivePercent, one.MaxDanger, 6);
    }

    [Fact]
    public void Two_riichi_halve_the_budget()
    {
        var one = Board("123m456m4578p447s1z", turn: 8);
        var two = Seat(one, 2, "3z4z5z6z7z1z", riichi: true, riichiIndex: 5);
        var single = Decide(one, Tenpai(8, 2000)).MaxDanger;
        var doubled = Decide(two, Tenpai(8, 2000)).MaxDanger;
        Assert.Equal(single * W.TwoRiichiFactor, doubled, 6);
    }

    [Fact]
    public void Probable_tenpai_without_riichi_scales_the_budget_up()
    {
        // Seat 2 has three open melds and fourteen discards: a strong tenpai estimate, no riichi.
        var state = Seat(Snap("123m456m4578p447s1z"), 2, "1m9m1p9p1s9s2m8m2p8p2s8s3z4z",
            melds: [Meld.MakePon(Tile.Parse("5z"), true), Meld.MakePon(Tile.Parse("6z"), true), Meld.MakePon(Tile.Parse("7z"), true)]);
        var seats = state.Seats.ToArray();
        seats[0] = seats[0] with { DiscardCount = 12 };
        state = state with { Seats = seats, WallRemaining = 20 };
        var model = Model(state);
        var tenpai = model.TenpaiProbability(2);
        Assert.InRange(tenpai, W.ThreatTenpaiFloor, 0.9);
        var decision = new PushFoldPolicy().Decide(state, model, [Tenpai(3, 2000)]);
        Assert.Equal(2, decision.PrimaryThreat);
        Assert.Equal(W.RankBMax / tenpai, decision.MaxDanger, 6);
    }

    [Fact]
    public void All_last_leader_folds_more_and_trailer_pushes_more()
    {
        var board = Board("123m456m4578p447s1z", turn: 13) with { HandNumber = 4, RoundWind = Wind.East, Ruleset = new RulesetOptions(true, 4) };
        static StateSnapshot Scores(StateSnapshot s, int us, int a, int b, int c)
        {
            var seats = s.Seats.ToArray();
            seats[0] = seats[0] with { Score = us };
            seats[1] = seats[1] with { Score = a };
            seats[2] = seats[2] with { Score = b };
            seats[3] = seats[3] with { Score = c };
            return s with { Seats = seats };
        }

        var baseline = Decide(Scores(board, 25000, 27000, 24000, 24000), Tenpai(3, 5200)).MaxDanger;   // 2nd: no factor
        var narrowLead = Decide(Scores(board, 28800, 27000, 25400, 18800), Tenpai(3, 5200)).MaxDanger;
        var bigLead = Decide(Scores(board, 45000, 20000, 18000, 17000), Tenpai(3, 5200)).MaxDanger;
        var trailing = Decide(Scores(board, 12000, 30000, 30000, 28000), Tenpai(3, 5200)).MaxDanger;
        Assert.Equal(baseline * W.LeadingFactor, narrowLead, 6);
        Assert.Equal(baseline * W.LeadingFactor * W.LeadingFactor, bigLead, 6);
        Assert.Equal(baseline * W.TrailingFactor, trailing, 6);
    }

    [Fact]
    public void Last_draws_keep_tenpai_for_the_noten_payment()
    {
        var board = Board("123m456m4578p447s1z", turn: 17) with { WallRemaining = 3 };
        var decision = Decide(board, Tenpai(3, 1300));
        Assert.Equal(W.PreDrawBudget, decision.MaxDanger, 6);
        Assert.Contains("last draws", decision.Reason.Display);
    }

    [Fact]
    public void Betaori_orders_by_danger_then_copies_then_hand_merit()
    {
        var state = Board("1z1z5s6m4z9p7p2p", turn: 9);
        var model = Model(state);
        var policy = new PushFoldPolicy();
        var candidates = new List<DiscardCandidate>
        {
            Candidate("5s", shanten: 1, score: 100),
            Candidate("1z", shanten: 2, score: 50),
            Candidate("6m", shanten: 2, score: 60),
            Candidate("4z", shanten: 2, score: 55),
            Candidate("9p", shanten: 2, score: 40),   // genbutsu (riichi tile)
        };
        var decision = policy.Decide(state, model, candidates);
        var ordered = new BetaoriPolicy().Order(state, model, candidates, decision);
        Assert.Equal(Tile.Parse("9p"), ordered[0].Tile);                       // 0 % first
        var honors = ordered.Skip(1).Take(2).Select(c => c.Tile.ToString()).ToList();
        Assert.Contains("1z", honors);                                         // the pair banks a second turn
        Assert.Equal(Tile.Parse("5s"), ordered[^1].Tile);                      // raw middle tile last
    }

    // A placement model that answers with a fixed distribution per score delta sign:
    // "leader": winning changes nothing, dealing in costs first place; "chaser": the reverse.
    private sealed class StakesModel(OpponentModel inner, bool leader) : IOpponentModel, IPlacementModel
    {
        public void Update(StateSnapshot state) => inner.Update(state);
        public double TenpaiProbability(int seat) => inner.TenpaiProbability(seat);
        public double Danger(Tile tile, int seat) => inner.Danger(tile, seat);
        public double ExpectedDealInCost(Tile tile) => inner.ExpectedDealInCost(tile);
        public DangerEstimate Explain(Tile tile, int seat) => inner.Explain(tile, seat);
        public int PrimaryThreat() => inner.PrimaryThreat();
        public double Value(int seat) => inner.Value(seat);
        public int LiveSuji(int seat) => inner.LiveSuji(seat);
        public double[]? Placement(StateSnapshot state, int[] scoreDeltas) => (Math.Sign(scoreDeltas[0]), leader) switch
        {
            (0, true) or (1, true) => [0.9, 0.1, 0, 0],          // leader: a win keeps first place, nothing gained
            (-1, true) => [0.3, 0.6, 0.1, 0],                    // a deal-in likely loses it
            (0, false) or (-1, false) => [0, 0, 0.2, 0.8],       // chaser: already last, nothing to lose
            _ => [0, 0.3, 0.6, 0.1],                              // a win climbs
        };
    }

    [Fact]
    public void Placement_stakes_scale_the_budget_and_replace_the_fixed_all_last_factors()
    {
        var board = Board("123m456m4578p447s1z", turn: 8);
        var seats = board.Seats.Select(s => s with { Score = s.Seat == 0 ? 40000 : 20000 }).ToArray();
        var state = board with { Seats = seats };
        var plain = new PushFoldPolicy().Decide(state, Model(state), [Tenpai(8, 2000)]);
        var leader = new PushFoldPolicy().Decide(state, new StakesModel(Model(state), leader: true), [Tenpai(8, 2000)]);
        var chaser = new PushFoldPolicy().Decide(state, new StakesModel(Model(state), leader: false), [Tenpai(8, 2000)]);
        Assert.Equal(W.BudgetTenPercent, plain.MaxDanger, 6);
        Assert.Equal(plain.MaxDanger * W.PlacementStakesMin, leader.MaxDanger, 6);   // nothing to gain: fold
        Assert.Equal(Math.Min(1, plain.MaxDanger * W.PlacementStakesMax), chaser.MaxDanger, 6);   // nothing to lose: push
        Assert.Contains("placement stakes", leader.Reason.Display);
        // With stakes answered, the transcribed all-last rank factors are not applied on top.
        var allLast = state with { RoundWind = Wind.South, HandNumber = 4, DealerSeat = 3 };
        Assert.True(allLast.IsAllLast);
        var leaderAllLast = new PushFoldPolicy().Decide(allLast, new StakesModel(Model(allLast), leader: true), [Tenpai(8, 2000)]);
        Assert.DoesNotContain("leading all-last", leaderAllLast.Reason.Display);
        // Weight 0 disables the stakes entirely.
        var off = new PushFoldPolicy(W with { PlacementStakesWeight = 0 }).Decide(state, new StakesModel(Model(state), leader: true), [Tenpai(8, 2000)]);
        Assert.Equal(plain.MaxDanger, off.MaxDanger, 6);
    }
}
