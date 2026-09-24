using MahjongHater.Core;
using MahjongHater.Core.Learning;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

public class CallDescriptorTests
{
    private static StateSnapshot Offer(string tiles = "3455567m123p55z9s", LegalAction legal = LegalAction.Chi | LegalAction.Pon | LegalAction.MinKan)
        => Snap(tiles, legal) with { CallTile = Tile.Parse("5m"), CallFromSeat = 3, DrawnTile = null,
            Phase = GamePhase.CallPrompt, CallWindowConfirmed = true };

    [Fact]
    public void Combined_chi_pon_kan_describes_every_shape_and_matches_both_learned_views()
    {
        var state = Offer();
        var description = CallDescriptor.Describe(state);
        Assert.True(description.Valid);
        Assert.Equal(5, description.Candidates.Count);
        Assert.Equal(3, description.Candidates.Count(c => c.Kind == ActionKind.Chi));
        Assert.Single(description.Candidates, c => c.Kind == ActionKind.Pon);
        Assert.Single(description.Candidates, c => c.Kind == ActionKind.MinKan);
        foreach (var candidate in description.Candidates)
        {
            Assert.Equal(candidate.Meld.IsKan ? 13 : 14, candidate.Remaining.Count + 3 * candidate.MeldsAfter.Count);
            // Every physical tile is accounted for exactly once after the claim.
            Assert.Equal(state.Hand.Append(state.CallTile!.Value).Order(),
                candidate.Remaining.Concat(candidate.MeldsAfter.SelectMany(m => m.Tiles)).Order());
        }
        var comparison = LearnedCallPolicy.CompareActions(state, description);
        Assert.True(comparison.Matches, comparison.Reason.Display);
        Assert.Equal(6, comparison.Legal.Count); // five calls plus Pass
    }

    [Fact]
    public void Combined_kan_pon_keeps_pon_when_kan_is_declined_by_strategy()
    {
        var state = Offer("555z123m456p67s11z", LegalAction.Pon | LegalAction.MinKan) with { CallTile = Tile.Parse("5z") };
        var description = CallDescriptor.Describe(state);
        Assert.Equal(2, description.Candidates.Count);
        Assert.True(LearnedCallPolicy.CompareActions(state, description).Matches);
        var decision = new CallPolicy().Evaluate(state, Model(state), default);
        Assert.Contains(decision.Diagnostics, r => r.Stage == "kan" && r.Display.Contains("shape is valid"));
        Assert.Contains(decision.Diagnostics, r => r.Stage == "call-evaluation" && r.Display.StartsWith("Pon"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Chi_from_wrong_seat_is_excluded_without_hiding_pon_or_kan(int seat)
    {
        var state = Offer() with { CallFromSeat = seat };
        var description = CallDescriptor.Describe(state);
        Assert.Equal(2, description.Candidates.Count);
        Assert.DoesNotContain(description.Candidates, c => c.Kind == ActionKind.Chi);
        Assert.False(LearnedCallPolicy.CompareActions(state, description).Matches);
    }

    [Fact]
    public void Chooser_compares_complete_shape_not_just_start_number()
    {
        var state = Offer() with { CallShapes = [Meld.MakeChi(Tile.Parse("3p"), Tile.Parse("4p"), Tile.Parse("5p"))] };
        Assert.DoesNotContain(CallDescriptor.Describe(state).Candidates, c => c.Kind == ActionKind.Chi);
        state = state with { CallShapes = [Meld.MakeChi(Tile.Parse("4m"), Tile.Parse("5m"), Tile.Parse("6m"))] };
        var description = CallDescriptor.Describe(state);
        var chi = Assert.Single(description.Candidates, c => c.Kind == ActionKind.Chi);
        Assert.Equal(TestTiles.Parse("456m"), chi.Meld.Tiles);
        Assert.False(LearnedCallPolicy.CompareActions(state, description).Matches); // old feature view has three shapes
    }

    [Fact]
    public void False_kan_offer_does_not_create_a_candidate_or_a_learned_action()
    {
        var state = Offer("345567m123p55z89s");
        var description = CallDescriptor.Describe(state);
        Assert.DoesNotContain(description.Candidates, c => c.Kind == ActionKind.MinKan);
        Assert.Contains(description.Candidates, c => c.Kind == ActionKind.Pon);
        var comparison = LearnedCallPolicy.CompareActions(state, description);
        Assert.False(comparison.Matches);
        Assert.DoesNotContain(LearningFeatures.OpenKanAction, comparison.Legal.Keys);
        Assert.Contains(description.Reasons, r => r.Display.Contains("Offered MinKan has no constructible candidate"));
    }

    [Theory]
    [InlineData("34555567m123p55z", "four physical copies")]
    [InlineData("3455567m123p55z", "expected 13")]
    public void Bad_physical_counts_are_rejected_before_policy_analysis(string hand, string why)
    {
        var state = Offer(hand);
        var description = CallDescriptor.Describe(state);
        Assert.False(description.Valid);
        Assert.Empty(description.Candidates);
        Assert.Contains(description.Reasons, r => r.Display.Contains(why));
        var policy = new CallPolicy(new NeverAnalyze());
        Assert.False(policy.Evaluate(state, Model(state), default).Accept);
    }

    [Fact]
    public void Unverified_or_malformed_melds_are_not_used_to_construct_calls()
    {
        var state = Offer("555m123p2267s", LegalAction.Pon) with
        {
            OurMelds = [new Meld(MeldType.Chi, TestTiles.Parse("124p").ToArray(), true)],
        };
        Assert.False(CallDescriptor.Describe(state).Valid);
        state = state with { OurMelds = [Meld.MakePon(Tile.Parse("5z"), true)] };
        Assert.True(CallDescriptor.Describe(state).Valid);
        var seats = state.Seats.ToArray();
        seats[0] = seats[0] with { MeldsVerified = false };
        Assert.False(CallDescriptor.Describe(state with { Seats = seats }).Valid);
        seats[0] = seats[0] with { MeldsVerified = true, MeldCount = 2 };
        Assert.False(CallDescriptor.Describe(state with { Seats = seats }).Valid);
    }

    [Fact]
    public void Red_copies_are_preserved_and_input_hand_is_not_mutated()
    {
        var state = Offer("3405567m123p55z9s");
        var original = state.Hand.ToArray();
        var description = CallDescriptor.Describe(state);
        var pon = Assert.Single(description.Candidates, c => c.Kind == ActionKind.Pon);
        Assert.DoesNotContain(pon.Consumed, t => t.IsRedFive); // prefer plain copies
        Assert.Contains(pon.Remaining, t => t.IsRedFive);
        var kan = Assert.Single(description.Candidates, c => c.Kind == ActionKind.MinKan);
        Assert.Single(kan.Meld.Tiles, t => t.IsRedFive);
        Assert.Equal(original, state.Hand);
    }

    [Fact]
    public void Added_kan_replaces_its_pon_in_place_and_preserves_red_copy()
    {
        var pon = new Meld(MeldType.Pon, TestTiles.Parse("055m").ToArray(), true);
        var other = Meld.MakePon(Tile.Parse("5z"), true);
        var state = Snap("5m234p678s1z", LegalAction.AnKan | LegalAction.ShouMinKan) with { OurMelds = [pon, other] };
        var candidate = Assert.Single(CallDescriptor.Describe(state).Candidates);
        Assert.Equal(ActionKind.ShouMinKan, candidate.Kind);
        Assert.Equal(MeldType.Shouminkan, candidate.MeldsAfter[0].Type);
        Assert.Same(other, candidate.MeldsAfter[1]);
        Assert.Single(candidate.Meld.Tiles, t => t.IsRedFive);
        Assert.Equal(13, candidate.Remaining.Count + 3 * candidate.MeldsAfter.Count);
        Assert.Equal(MeldType.Pon, pon.Type);
    }

    [Fact]
    public void Concealed_kan_has_a_valid_replacement_draw_hand_and_explicit_metrics()
    {
        var state = Snap("1111m234p567s2267p", LegalAction.AnKan | LegalAction.ShouMinKan);
        var candidate = Assert.Single(CallDescriptor.Describe(state).Candidates);
        Assert.Equal(ActionKind.AnKan, candidate.Kind);
        Assert.Equal(10, candidate.Remaining.Count);
        Assert.False(candidate.Meld.IsOpen);
        var decision = new CallPolicy().Evaluate(state, Model(state), default);
        Assert.True(decision.Accept);
        Assert.Contains(decision.Diagnostics, r => r.Stage == "kan" && r.Display.Contains("shanten") && r.Display.Contains("live improving tiles"));
    }

    [Fact]
    public void Missing_kan_shape_is_distinct_from_strategic_rejection()
    {
        var absent = Snap("123m456m4578p447s1z", LegalAction.AnKan | LegalAction.ShouMinKan);
        var missing = new CallPolicy().Evaluate(absent, Model(absent), default);
        Assert.False(missing.Accept);
        Assert.Contains("No offered call has a constructible candidate", missing.Reason.Display);
        Assert.Contains(missing.Diagnostics, r => r.Display.Contains("no constructible candidate"));
        var shape = Snap("22221345m678p789s", LegalAction.AnKan);
        var rejected = new CallPolicy().Evaluate(shape, Model(shape), default);
        Assert.False(rejected.Accept);
        Assert.Contains(rejected.Diagnostics, r => r.Stage == "call-shape" && r.Display.StartsWith("Validated AnKan"));
        Assert.Contains(rejected.Diagnostics, r => r.Stage == "kan" && r.Display.Contains("decline:"));
    }

    [Fact]
    public void Riichi_cannot_claim_or_kan_a_different_drawn_kind()
    {
        Assert.Empty(CallDescriptor.Describe(Offer() with { OurRiichi = true }).Candidates);
        var state = Snap("1111m234p567s2267p", LegalAction.AnKan) with { OurRiichi = true, DrawnTile = Tile.Parse("7p") };
        Assert.Empty(CallDescriptor.Describe(state).Candidates);
        Assert.Single(CallDescriptor.Describe(state with { DrawnTile = Tile.Parse("1m") }).Candidates);
    }

    [Fact]
    public void Descriptor_honors_cancellation()
        => Assert.Throws<OperationCanceledException>(() => CallDescriptor.Describe(Offer(), new CancellationToken(true)));

    [Fact]
    public void Existing_kan_counts_as_one_meld_but_four_physical_tiles()
    {
        var state = Snap("2222p3458s66z7z", LegalAction.AnKan) with
        {
            OurMelds = [Meld.MakeKan(Tile.Parse("1m"), MeldType.Ankan)], DrawnTile = Tile.Parse("2p"),
        };
        var description = CallDescriptor.Describe(state);
        Assert.True(description.Valid);
        var candidate = Assert.Single(description.Candidates);
        Assert.Equal(7, candidate.Remaining.Count);
        Assert.Equal(2, candidate.MeldsAfter.Count);
        Assert.Equal(15, candidate.Remaining.Count + candidate.MeldsAfter.Sum(m => m.Tiles.Length));
    }

    private sealed class NeverAnalyze : HandAnalyzer
    {
        public override AnalysisResult Analyze(Hand hand, AnalysisContext? context = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Invalid construction reached analysis.");
    }
}
