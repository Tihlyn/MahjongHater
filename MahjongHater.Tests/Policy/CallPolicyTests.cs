using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

public class CallPolicyTests
{
    private static CallDecision Evaluate(StateSnapshot state) => new CallPolicy().Evaluate(state, Model(state), default);

    private static StateSnapshot Offered(string hand, string tile, LegalAction legal, int seat = 3) => Snap(hand, legal) with
    {
        DrawnTile = null,
        CallTile = Tile.Parse(tile),
        CallFromSeat = seat,
        Phase = GamePhase.CallPrompt,
    };

    [Fact]
    public void Yakuhai_pon_is_accepted_when_it_lowers_shanten()
    {
        var decision = Evaluate(Offered("123m456p67s1155z9s", "5z", LegalAction.Pon));
        Assert.True(decision.Accept, decision.Reason.Display);
        Assert.Equal(ActionKind.Pon, decision.Kind);
        Assert.True(decision.Meld!.IsOpen);
        Assert.All(decision.Meld.Tiles, t => Assert.Equal(Tile.Parse("5z"), t));
    }

    [Fact]
    public void Open_yakuless_pon_is_declined()
    {
        var state = Offered("123m456p67s1122z9s", "2z", LegalAction.Pon) with { SeatWind = Wind.West };
        Assert.False(Evaluate(state).Accept);
    }

    [Fact]
    public void Pon_needs_two_matching_closed_tiles()
    {
        Assert.False(Evaluate(Offered("123m456p67s115z89s", "5z", LegalAction.Pon)).Accept);
    }

    [Fact]
    public void Pon_that_does_not_improve_shanten_is_declined()
    {
        Assert.False(Evaluate(Offered("123m456p789s1155z", "5z", LegalAction.Pon)).Accept);
    }

    [Fact]
    public void Viable_lists_yaku_safe_claims_regardless_of_shanten()
    {
        // The strict policy declines this yakuhai pon (no shanten gain); it is still yaku-safe.
        var offered = Offered("123m456p789s1155z", "5z", LegalAction.Pon);
        var viable = new CallPolicy().Viable(offered, default);
        Assert.Contains(viable, v => v.Accept && v.Kind == ActionKind.Pon && v.Meld!.Tiles.All(t => t.Equals(Tile.Parse("5z"))));
        // A yakuless open hand has no viable claim at all.
        var yakuless = Offered("123m456p67s1122z9s", "2z", LegalAction.Pon) with { SeatWind = Wind.West };
        Assert.Empty(new CallPolicy().Viable(yakuless, default));
    }

    [Fact]
    public void Pon_is_preferred_over_chi_at_equal_shanten()
    {
        var state = Offered("5534m555z678p22s9s", "5m", LegalAction.Pon | LegalAction.Chi);
        Assert.Equal(ActionKind.Pon, Evaluate(state).Kind);
        Assert.Equal(ActionKind.Chi, Evaluate(state with { Legal = LegalAction.Chi }).Kind);
    }

    [Theory]
    [InlineData("34m")]
    [InlineData("46m")]
    [InlineData("67m")]
    public void Every_chi_shape_is_considered(string shape)
    {
        var decision = Evaluate(Offered("555z123p22s89s1z" + shape, "5m", LegalAction.Chi));
        Assert.True(decision.Accept, decision.Reason.Display);
        Assert.Equal(ActionKind.Chi, decision.Kind);
        Assert.True(decision.Meld!.IsOpen);
        Assert.Equal(3, decision.Meld.Tiles.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Chi_is_only_allowed_from_kamicha(int seat)
    {
        Assert.False(Evaluate(Offered("555z123p22s89s1z46m", "5m", LegalAction.Chi, seat)).Accept);
    }

    [Fact]
    public void Chi_does_not_break_the_only_pair()
    {
        Assert.False(Evaluate(Offered("555z123p446m89s1z", "5m", LegalAction.Chi)).Accept);
    }

    [Fact]
    public void Calls_preserve_actual_red_tiles()
    {
        var decision = Evaluate(Offered("234m678p67s2205m9p", "5m", LegalAction.Pon));
        Assert.True(decision.Accept, decision.Reason.Display);
        Assert.Single(decision.Meld!.Tiles, t => t.IsRedFive);
    }

    [Fact]
    public void Tanyao_call_depends_on_kuitan()
    {
        var state = Offered("234m678p67s2255m9p", "5m", LegalAction.Pon);
        Assert.True(Evaluate(state).Accept);
        Assert.False(Evaluate(state with { Ruleset = new RulesetOptions(false) }).Accept);
    }

    [Fact]
    public void Honitsu_route_accepts_non_yakuhai_pon()
    {
        var state = Offered("123456m78m1122z9p", "2z", LegalAction.Pon) with { SeatWind = Wind.West };
        Assert.True(Evaluate(state).Accept);
    }

    [Fact]
    public void Closed_hand_declines_minkan()
    {
        Assert.False(Evaluate(Offered("555z123m456p67s11z", "5z", LegalAction.MinKan)).Accept);
    }

    [Fact]
    public void Already_open_hand_can_minkan_without_losing_ukeire()
    {
        var state = Offered("111m234p2267s", "1m", LegalAction.MinKan) with
        {
            OurMelds = [Meld.MakePon(Tile.Parse("5z"), true)],
        };
        var decision = Evaluate(state);
        Assert.True(decision.Accept, decision.Reason.Display);
        Assert.Equal(ActionKind.MinKan, decision.Kind);
        Assert.Equal(4, decision.Meld!.Tiles.Length);
    }

    [Fact]
    public void Ankan_accepts_unchanged_ukeire()
    {
        var decision = Evaluate(Snap("1111m234p567s2267p", LegalAction.AnKan));
        Assert.True(decision.Accept, decision.Reason.Display);
        Assert.Equal(ActionKind.AnKan, decision.Kind);
        Assert.False(decision.Meld!.IsOpen);
        Assert.Equal(4, decision.Meld.Tiles.Length);
    }

    [Fact]
    public void Ankan_declines_when_quad_tiles_are_needed_for_sequences()
    {
        var decision = Evaluate(Snap("22221345m678p789s", LegalAction.AnKan));
        Assert.False(decision.Accept);
    }

    [Fact]
    public void Riichi_ankan_uses_explicit_draw_and_preserves_waits()
    {
        var state = Snap("1111m234p567s2267p", LegalAction.AnKan) with
        {
            OurRiichi = true,
            DrawnTile = Tile.Parse("1m"),
        };
        var decision = Evaluate(state);
        Assert.True(decision.Accept, decision.Reason.Display);
        Assert.Equal(ActionKind.AnKan, decision.Kind);
    }

    [Fact]
    public void Shouminkan_replaces_existing_open_pon()
    {
        var state = Snap("1m234p567s2267p", LegalAction.ShouMinKan) with
        {
            OurMelds = [Meld.MakePon(Tile.Parse("1m"), true)],
        };
        var decision = Evaluate(state);
        Assert.True(decision.Accept, decision.Reason.Display);
        Assert.Equal(ActionKind.ShouMinKan, decision.Kind);
        Assert.Equal(4, decision.Meld!.Tiles.Length);
    }

    [Fact]
    public void Riichi_hand_cannot_pon()
    {
        Assert.False(Evaluate(Offered("123m456p67s1155z9s", "5z", LegalAction.Pon) with { OurRiichi = true }).Accept);
    }

    [Fact]
    public void Calls_honor_cancellation()
    {
        var state = Offered("123m456p67s1155z9s", "5z", LegalAction.Pon);
        Assert.Throws<OperationCanceledException>(() => new CallPolicy().Evaluate(state, Model(state), new CancellationToken(true)));
    }

    [Fact]
    public void Chooser_restricts_chi_to_the_offered_shapes()
    {
        // 456s is the tenpai shape; with only 234s and 345s on offer the policy must not pick it.
        var state = Snap("233m2345p0p23456s", LegalAction.Chi | LegalAction.Pass) with
        {
            CallTile = Tile.Parse("4s"),
            CallFromSeat = 3,
            CallShapes = [Meld.MakeChi(Tile.Parse("2s"), Tile.Parse("3s"), Tile.Parse("4s")), Meld.MakeChi(Tile.Parse("3s"), Tile.Parse("4s"), Tile.Parse("5s"))],
        };
        var decision = new CallPolicy().Evaluate(state, Model(state), default);
        if (decision.Accept)
            Assert.Contains(decision.Meld!.Tiles.Min(t => t.Number), new[] { 2, 3 });

        var open = state with { CallShapes = [Meld.MakeChi(Tile.Parse("4s"), Tile.Parse("5s"), Tile.Parse("6s"))] };
        var best = new CallPolicy().Evaluate(open, Model(open), default);
        Assert.True(best.Accept);
        Assert.Equal(4, best.Meld!.Tiles.Min(t => t.Number));
    }
}
