using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

public class DecisionPolicyTests
{
    [Fact]
    public void Game_restricted_discards_override_attack_ranking()
    {
        var state = Snap() with { DiscardableTiles = [Tile.Parse("1z")] };
        var policy = new DecisionPolicy(discards: new FixedDiscards(Candidate("3m"), Candidate("1z")));
        var result = policy.Choose(state, default);
        Assert.Equal(Tile.Parse("1z"), result.Tile);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public void Committed_riichi_cannot_fold_away_from_drawn_tile()
    {
        var state = Seat(Snap(), riichi: true) with { OurRiichi = true, DrawnTile = Tile.Parse("1z") };
        var policy = new DecisionPolicy(discards: new FixedDiscards(Candidate("3m", risk: 0), Candidate("1z", risk: 0.9)));
        var result = policy.Choose(state, default);
        Assert.Equal(Tile.Parse("1z"), result.Tile);
        Assert.DoesNotContain(result.Steps, s => s.Stage == "push/fold");
        Assert.Contains("Riichi locked", result.Summary);
    }

    [Fact]
    public void Fold_selects_safest_candidate_even_if_it_worsens_shanten()
    {
        var state = Seat(Snap(), riichi: true);
        var policy = new DecisionPolicy(discards: new FixedDiscards(
            Candidate("3m", shanten: 2, risk: 0.4), Candidate("1z", shanten: 3, risk: 0, score: -100)));
        var result = policy.Choose(state, default);
        Assert.Equal(ActionKind.Discard, result.Kind);
        Assert.Equal(Tile.Parse("1z"), result.Tile);
        Assert.Equal(result.Tile, result.Candidates[0].Tile);
        Assert.Contains(result.Steps, s => s.Stage == "push/fold" && s.Display.Contains("Budget"));
        Assert.Contains(result.Steps, s => s.Stage == "discard" && (s.Display.StartsWith("Turn") || s.Display.StartsWith("Fold")));
    }

    [Fact]
    public void Legacy_fold_selects_safest_candidate_even_if_it_worsens_shanten()
    {
        var legacy = new PolicyWeights { DefenseModel = DefenseModel.Legacy };
        var state = Seat(Snap(), riichi: true);
        var policy = new DecisionPolicy(weights: legacy, discards: new FixedDiscards(
            Candidate("3m", shanten: 2, risk: 0.4), Candidate("1z", shanten: 3, risk: 0, score: -100)));
        var result = policy.Choose(state, default);
        Assert.Equal(Tile.Parse("1z"), result.Tile);
        Assert.Contains(result.Steps, s => s.Stage == "push/fold" && s.Display.Contains("Fold"));
    }

    [Fact]
    public void Folding_never_declares_riichi()
    {
        var state = Seat(Snap(legal: LegalAction.Discard | LegalAction.Riichi), riichi: true);
        // Cheap tenpai on a 2-tile wait against a declared riichi: fold, hence no riichi.
        var policy = new DecisionPolicy(discards: new FixedDiscards(Candidate(shanten: 0, value: 0, ukeire: 2)));
        Assert.Equal(ActionKind.Discard, policy.Choose(state, default).Kind);
    }

    [Fact]
    public void Typical_one_shanten_hand_has_sensible_discard_and_steps()
    {
        var state = Snap();
        var result = new DecisionPolicy().Choose(state, default);
        Assert.Equal(ActionKind.Discard, result.Kind);
        Assert.Equal(Tile.Parse("1z"), result.Tile);
        Assert.Equal(1, result.Candidates[0].ShantenAfter);
        Assert.True(result.Candidates[0].Ukeire > 0);
        Assert.NotEmpty(result.Steps);
        Assert.All(result.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Display)));
    }

    [Fact]
    public void Open_yakuless_complete_hand_cannot_tsumo_even_with_dora()
    {
        // Open 222p + 123m 789m 55s 678s has no yaku. Closed tsumo itself would be a yaku.
        var state = Snap("123m789m55678s", LegalAction.Tsumo | LegalAction.Discard) with
        {
            OurMelds = [Meld.MakePon(Tile.Parse("2p"), true)],
            DoraIndicators = [Tile.Parse("1p")],
        };
        var result = new DecisionPolicy().Choose(state, default);
        Assert.Equal(ActionKind.Discard, result.Kind);
        Assert.Contains(result.Steps, s => s.Stage == "win" && s.Display.Contains("Decline"));
    }

    [Fact]
    public void Closed_complete_hand_can_win_by_menzen_tsumo()
    {
        var state = Snap("123m789m222p55678s", LegalAction.Tsumo | LegalAction.Discard);
        var result = new DecisionPolicy().Choose(state, default);
        Assert.Equal(ActionKind.Tsumo, result.Kind);
        Assert.Equal(state.DrawnTile, result.Tile);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Incomplete_hand_cannot_tsumo()
    {
        var result = new DecisionPolicy().Choose(Snap(legal: LegalAction.Tsumo | LegalAction.Discard), default);
        Assert.Equal(ActionKind.Discard, result.Kind);
    }

    [Fact]
    public void Ron_adds_the_claimed_tile_and_uses_riichi_flag()
    {
        var state = Snap("123m789m222p5567s", LegalAction.Ron) with
        {
            CallTile = Tile.Parse("8s"),
            OurRiichi = true,
            DrawnTile = null,
        };
        Assert.Equal(ActionKind.Ron, new DecisionPolicy().Choose(state, default).Kind);
        Assert.Equal(ActionKind.Pass, new DecisionPolicy().Choose(state with { OurRiichi = false }, default).Kind);
    }

    [Fact]
    public void Ron_does_not_add_claimed_tile_twice()
    {
        var state = Snap("123m789m222p55678s", LegalAction.Ron) with
        {
            CallTile = Tile.Parse("8s"),
            OurRiichi = true,
        };
        Assert.Equal(ActionKind.Ron, new DecisionPolicy().Choose(state, default).Kind);
    }

    [Fact]
    public void Win_gate_uses_snapshot_winds()
    {
        var state = Snap("123m789m333z5567s", LegalAction.Ron) with { CallTile = Tile.Parse("8s"), SeatWind = Wind.West };
        Assert.Equal(ActionKind.Ron, new DecisionPolicy().Choose(state, default).Kind);
        Assert.Equal(ActionKind.Pass, new DecisionPolicy().Choose(state with { SeatWind = Wind.South }, default).Kind);
    }

    [Fact]
    public void Win_gate_respects_configured_minimum_han()
    {
        var state = Snap("123m789m222p55678s", LegalAction.Tsumo);
        var policy = new DecisionPolicy(weights: new PolicyWeights { MinHanDoman = 2 });
        Assert.Equal(ActionKind.Pass, policy.Choose(state, default).Kind);
    }

    [Fact]
    public void Win_has_priority_over_calls()
    {
        var state = Snap("123m789m222p55678s", LegalAction.Tsumo | LegalAction.Pon);
        Assert.Equal(ActionKind.Tsumo, new DecisionPolicy(calls: new FixedCalls(true)).Choose(state, default).Kind);
    }

    [Fact]
    public void Accepted_call_precedes_discards()
    {
        var state = Snap(legal: LegalAction.Pon | LegalAction.Discard);
        var result = new DecisionPolicy(calls: new FixedCalls(true)).Choose(state, default);
        Assert.Equal(ActionKind.Pon, result.Kind);
        Assert.NotNull(result.Call);
    }

    [Fact]
    public void Declined_call_passes_when_no_discard_is_legal()
    {
        var result = new DecisionPolicy(calls: new FixedCalls(false)).Choose(Snap(legal: LegalAction.Pon), default);
        Assert.Equal(ActionKind.Pass, result.Kind);
        Assert.Contains(result.Steps, s => s.Stage == "call");
    }

    [Fact]
    public void Declined_call_falls_through_to_discard_when_legal()
    {
        var result = new DecisionPolicy(calls: new FixedCalls(false))
            .Choose(Snap(legal: LegalAction.Pon | LegalAction.Discard), default);
        Assert.Equal(ActionKind.Discard, result.Kind);
        Assert.Contains(result.Steps, s => s.Stage == "call");
    }

    [Fact]
    public void No_legal_action_passes()
    {
        Assert.Equal(ActionKind.Pass, new DecisionPolicy().Choose(StateSnapshot.Empty, default).Kind);
    }

    [Theory]
    [InlineData("123m")]
    [InlineData("123m456m789m4467p")]
    [InlineData("123m456m789m4467p12z")]
    public void Discard_guard_rejects_out_of_sync_tile_count(string hand)
    {
        var result = new DecisionPolicy().Choose(Snap(hand), default);
        Assert.Equal(ActionKind.None, result.Kind);
        Assert.Contains("hand out of sync", result.Summary);
        Assert.NotEmpty(result.Steps);
    }

    [Fact]
    public void Discard_guard_counts_meld_tiles()
    {
        var state = Snap("234m456p55s67s2z") with { OurMelds = [Meld.MakePon(Tile.Parse("5z"), true)] };
        Assert.Equal(ActionKind.Discard, new DecisionPolicy().Choose(state, default).Kind);
    }

    [Fact]
    public void Discard_guard_treats_a_kan_as_one_set()
    {
        // 11 closed + one kan (4 physical tiles) is a normal post-kan discard state.
        var state = Snap("234m456p55s67s2z") with { OurMelds = [Meld.MakeKan(Tile.Parse("5z"), MeldType.Daiminkan)] };
        Assert.Equal(ActionKind.Discard, new DecisionPolicy().Choose(state, default).Kind);
    }

    [Fact]
    public void Decision_declares_riichi_on_good_closed_wait()
    {
        var state = Snap("123m456m789m4467p1z", LegalAction.Discard | LegalAction.Riichi);
        var result = new DecisionPolicy().Choose(state, default);
        Assert.Equal(ActionKind.Riichi, result.Kind);
        Assert.Equal(Tile.Parse("1z"), result.Tile);
        Assert.Contains(result.Steps, s => s.Stage == "riichi");
    }

    [Fact]
    public void Decision_declines_riichi_when_wall_is_short()
    {
        var state = Snap("123m456m789m4467p1z", LegalAction.Discard | LegalAction.Riichi) with { WallRemaining = 3 };
        Assert.Equal(ActionKind.Discard, new DecisionPolicy().Choose(state, default).Kind);
    }

    [Fact]
    public void Cancellation_before_and_between_stages_is_observed()
    {
        Assert.Throws<OperationCanceledException>(() => new DecisionPolicy().Choose(Snap(), new CancellationToken(true)));
        using var source = new CancellationTokenSource();
        var policy = new DecisionPolicy(calls: new CancellingCalls(source));
        Assert.Throws<OperationCanceledException>(() => policy.Choose(Snap(legal: LegalAction.Pon), source.Token));
    }

    [Fact]
    public async Task Concurrent_snapshots_do_not_mix_opponent_state()
    {
        var model = new OpponentModel();
        var discards = new FixedDiscards(Candidate("3m", shanten: 2, risk: 0.4), Candidate("1z", shanten: 3, risk: 0));
        var policies = new[] { new DecisionPolicy(model, discards), new DecisionPolicy(model, discards) };
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
        {
            var threatened = i % 2 == 0;
            var state = Seat(Snap(), riichi: threatened) with { Sequence = i + 1 };
            var result = policies[i % 2].Choose(state, default);
            return (Threatened: threatened, Result: result);
        })));
        Assert.All(results, r => Assert.Equal(Tile.Parse(r.Threatened ? "1z" : "3m"), r.Result.Tile));
    }

    private sealed class CancellingCalls(CancellationTokenSource source) : ICallPolicy
    {
        public CallDecision Evaluate(StateSnapshot state, IOpponentModel opponents, CancellationToken ct)
        {
            source.Cancel();
            return CallDecision.Decline("Cancelled during call stage.");
        }
    }
}
