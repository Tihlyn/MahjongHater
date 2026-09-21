using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

public class DiscardAndStanceTests
{
    [Fact]
    public void Discard_policy_preserves_analyzer_metrics_and_legacy_score_scale()
    {
        var legacy = new PolicyWeights { DefenseModel = DefenseModel.Legacy };
        var state = Seat(Snap("123m456m789m4467p1z"), riichi: true);
        var hand = new Hand();
        hand.ClosedTiles.AddRange(state.Hand);
        var engine = new HandAnalyzer().Analyze(hand);
        var model = Model(state, legacy);
        var candidates = new HeuristicDiscardPolicy(weights: legacy).Rank(state, model, default);
        foreach (var candidate in candidates)
        {
            var source = Assert.Single(engine.Ranked, o => o.DiscardTile == candidate.Tile);
            Assert.Equal(source.Eval.ShantenAfter, candidate.ShantenAfter);
            Assert.Equal(source.Eval.Ukeire, candidate.Ukeire);
            Assert.Equal(source.Eval.Ukeire2, candidate.Ukeire2);
            Assert.Equal(source.ValueEstimate, candidate.Value);
            Assert.Equal(source.Score - model.ExpectedDealInCost(candidate.Tile), candidate.Score, 8);
            Assert.InRange(candidate.DealInRisk, 0, 1);
        }
        Assert.Equal(0, candidates[0].ShantenAfter);
    }

    [Fact]
    public void V2_scores_candidates_in_points_per_hand()
    {
        var state = Seat(Snap("123m456m789m4467p1z"), riichi: true);
        var model = Model(state);
        var candidates = new HeuristicDiscardPolicy().Rank(state, model, default);
        foreach (var candidate in candidates)
        {
            Assert.InRange(candidate.WinProbability, 0, 1);
            Assert.True(candidate.ValuePoints >= 0);
            var expected = candidate.WinProbability * candidate.ValuePoints
                           - PolicyWeights.Default.PushExposureTurns * model.ExpectedDealInCost(candidate.Tile);
            Assert.Equal(expected, candidate.Score, 6);
            Assert.Equal(candidate.Score, candidate.ExpectedValue, 6);
        }

        // The tenpai keep (discard 1z, wait 5-8p: riichi, pinfu, ittsu as dealer) is priced
        // from the scoring engine plus the ippatsu/ura expectation.
        var tenpai = candidates.Single(c => c.Tile.Equals(Tile.Parse("1z")));
        Assert.Equal(0, tenpai.ShantenAfter);
        Assert.InRange(tenpai.ValuePoints, 5800, 12000);
        Assert.True(tenpai.MinPoints >= 5800);
        Assert.True(tenpai.WinProbability > 0.3);
        var nonDealer = new HeuristicDiscardPolicy().Rank(state with { DealerSeat = 1 }, Model(state with { DealerSeat = 1 }), default)
            .Single(c => c.Tile.Equals(Tile.Parse("1z")));
        Assert.True(nonDealer.ValuePoints < tenpai.ValuePoints);
    }

    [Fact]
    public void Analyzer_receives_visibility_and_dora_indicators()
    {
        var state = Seat(Snap("123m456m789m4467p1z"), discards: "888p") with
        {
            DoraIndicators = [Tile.Parse("5p")],
        };
        var best = new HeuristicDiscardPolicy().Rank(state, Model(state), default)[0];
        Assert.Equal(Tile.Parse("1z"), best.Tile);
        Assert.Equal(4, best.Ukeire);
        Assert.True(best.Value >= 1);
    }

    [Fact]
    public void Riichi_lock_uses_explicit_drawn_tile_even_when_hand_is_sorted()
    {
        var state = Snap("123m456m789m4467p1z") with { OurRiichi = true, DrawnTile = Tile.Parse("4p") };
        var only = Assert.Single(new HeuristicDiscardPolicy().Rank(state, Model(state), default));
        Assert.Equal(state.DrawnTile, only.Tile);
    }

    [Fact]
    public void Riichi_lock_discards_actual_red_draw_instead_of_plain_copy()
    {
        var state = Snap("055m111p222s333s11z") with
        {
            OurRiichi = true,
            DrawnTile = new Tile(TileSuit.Man, 5, true),
        };
        var only = Assert.Single(new HeuristicDiscardPolicy().Rank(state, Model(state), default));
        Assert.True(only.Tile.IsRedFive);
        var plainState = state with { Hand = TestTiles.Parse("555m111p222s333s11z"), DrawnTile = Tile.Parse("5m") };
        var plain = Assert.Single(new HeuristicDiscardPolicy().Rank(plainState, Model(plainState), default));
        Assert.Equal(plain.Value, only.Value);
    }

    [Fact]
    public void Missing_riichi_draw_produces_no_candidate()
    {
        var state = Snap() with { OurRiichi = true, DrawnTile = null };
        Assert.Empty(new HeuristicDiscardPolicy().Rank(state, Model(state), default));
    }

    [Fact]
    public void Far_hand_folds_against_riichi()
    {
        var state = Seat(Snap(), riichi: true);
        Assert.Equal(PushFoldStance.Fold,
            new PushFoldPolicy().Evaluate(state, Model(state), Candidate(shanten: 2), out var reason));
        Assert.NotEmpty(reason.Display);
    }

    [Fact]
    public void Valuable_one_shanten_hand_pushes_against_riichi()
    {
        var state = Seat(Snap(), riichi: true);
        Assert.Equal(PushFoldStance.Push,
            new PushFoldPolicy().Evaluate(state, Model(state), Candidate(shanten: 1, value: 3), out _));
    }

    [Fact]
    public void Cheap_one_shanten_hand_folds_against_riichi()
    {
        var state = Seat(Snap(), riichi: true);
        Assert.Equal(PushFoldStance.Fold,
            new PushFoldPolicy().Evaluate(state, Model(state), Candidate(shanten: 1, value: 0), out _));
    }

    [Fact]
    public void Tenpai_pushes_against_a_soft_estimate_and_a_riichi_with_a_real_wait()
    {
        // 24 discards each and no riichi: the estimate is high for everyone.
        var late = Snap();
        foreach (var seat in new[] { 1, 2, 3 })
            late = Seat(late, seat, discards: "1m2m3m4m6m7m8m9m1p2p3p4p6p7p8p9p1s2s3s4s6s7s8s9s");
        Assert.Equal(PushFoldStance.Push,
            new PushFoldPolicy().Evaluate(late, Model(late), Candidate(shanten: 0, value: 1, ukeire: 4), out var soft));
        Assert.Contains("Push", soft.Display);

        var riichi = Seat(Snap(), riichi: true);
        Assert.Equal(PushFoldStance.Push,
            new PushFoldPolicy().Evaluate(riichi, Model(riichi), Candidate(shanten: 0, value: 1, ukeire: 4), out _));
        Assert.Equal(PushFoldStance.Fold,
            new PushFoldPolicy().Evaluate(riichi, Model(riichi), Candidate(shanten: 0, value: 1, ukeire: 2), out _));
    }

    [Fact]
    public void One_shanten_does_not_fold_on_the_soft_estimate_alone()
    {
        var late = Snap();
        foreach (var seat in new[] { 1, 2, 3 })
            late = Seat(late, seat, discards: "1m2m3m4m6m7m8m9m1p2p3p4p6p7p8p9p1s2s3s4s6s7s8s9s");
        Assert.Equal(PushFoldStance.Push,
            new PushFoldPolicy().Evaluate(late, Model(late), Candidate(shanten: 1, value: 0), out _));
        Assert.Equal(PushFoldStance.Fold,
            new PushFoldPolicy().Evaluate(late, Model(late), Candidate(shanten: 2, value: 0), out _));
    }

    [Fact]
    public void No_threat_means_push()
    {
        var state = Snap();
        Assert.Equal(PushFoldStance.Push,
            new PushFoldPolicy().Evaluate(state, Model(state), Candidate(shanten: 3, value: 0), out _));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void Riichi_respects_wall_threshold(int wall, bool expected)
    {
        var state = Snap() with { WallRemaining = wall };
        Assert.Equal(expected, new RiichiPolicy().ShouldDeclare(state, Model(state), Candidate(shanten: 0), out _));
    }

    [Fact]
    public void Riichi_requires_closed_tenpai_with_enough_live_tiles()
    {
        var policy = new RiichiPolicy();
        var state = Snap();
        Assert.False(policy.ShouldDeclare(state, Model(state), Candidate(shanten: 1), out _));
        Assert.False(policy.ShouldDeclare(state, Model(state), Candidate(shanten: 0, ukeire: 1), out _));
        Assert.False(policy.ShouldDeclare(state, Model(state), Candidate(shanten: 0, ukeire: 0), out _));
        Assert.True(policy.ShouldDeclare(state, Model(state), Candidate(shanten: 0, ukeire: 3), out _)); // tanki: 3 live copies
        var open = state with { OurMelds = [Meld.MakePon(Tile.Parse("5z"), true)] };
        Assert.False(policy.ShouldDeclare(open, Model(open), Candidate(shanten: 0), out _));
        Assert.False(policy.ShouldDeclare(state with { OurRiichi = true }, Model(state), Candidate(shanten: 0), out _));
    }

    [Fact]
    public void Opponent_riichi_rejects_bad_wait_even_with_lower_configured_threshold()
    {
        var state = Seat(Snap(), riichi: true);
        Assert.False(new RiichiPolicy(new PolicyWeights { RiichiMinUkeire = 1 })
            .ShouldDeclare(state, Model(state), Candidate(shanten: 0, ukeire: 3), out var reason));
        Assert.Contains("riichi", reason.Display);
        // A good wait chases regardless of value; a bad wait chases with 5 200 or more.
        Assert.True(new RiichiPolicy().ShouldDeclare(state, Model(state), Candidate(shanten: 0, ukeire: 8), out _));
        Assert.True(new RiichiPolicy().ShouldDeclare(state, Model(state), Candidate(shanten: 0, ukeire: 3) with { ValuePoints = 5200 }, out _));
        Assert.False(new RiichiPolicy().ShouldDeclare(state with { DealerSeat = 1 }, Model(state), Candidate(shanten: 0, ukeire: 3) with { ValuePoints = 5200 }, out _));
    }

    [Fact]
    public void Discard_analysis_honors_cancellation()
    {
        var state = Snap();
        Assert.Throws<OperationCanceledException>(() =>
            new HeuristicDiscardPolicy().Rank(state, Model(state), new CancellationToken(true)));
    }
}
