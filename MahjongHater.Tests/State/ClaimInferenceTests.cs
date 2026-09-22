using MahjongHater.Core;
using Xunit;

namespace MahjongHater.Tests.State;

// Working the call window backwards: which claims a tile allows is pure arithmetic over the
// closed hand, with no yaku, furiten or rule option in it. That is what makes it usable as a
// check ON the game's offer rather than a replacement for it
// (docs/research/WIN_OFFERS_2026_09_22.md).
public class ClaimInferenceTests
{
    private static ClaimOptions Infer(string hand, string tile, int melds = 0, bool allowChi = true)
        => HandTracking.InferClaims(TestTiles.Parse(hand), Tile.Parse(tile), melds, allowChi);

    [Fact]
    public void Two_copies_allow_pon_and_three_also_allow_kan()
    {
        Assert.Equal(ClaimOptions.Pon, Infer("55m123p456s789s11z", "5m") & (ClaimOptions.Pon | ClaimOptions.Kan));
        Assert.Equal(ClaimOptions.Pon | ClaimOptions.Kan, Infer("555m123p456s789s1z", "5m") & (ClaimOptions.Pon | ClaimOptions.Kan));
        Assert.False(Infer("5m123p456s789s112z", "5m").HasFlag(ClaimOptions.Pon));
    }

    [Theory]
    [InlineData("34m", "2m")]   // 2m completes 234m from below
    [InlineData("34m", "5m")]   // ...and 345m from above
    [InlineData("35m", "4m")]   // closed kanchan
    public void Run_neighbours_allow_chi(string shape, string tile)
        => Assert.True(Infer(shape + "123p456s789s1z", tile).HasFlag(ClaimOptions.Chi));

    [Fact]
    public void Chi_never_crosses_a_suit_boundary()
    {
        // 8m9m + 1p is not a run, and neither is 9m1p2p.
        Assert.False(Infer("89m12p456s789s11z", "1p").HasFlag(ClaimOptions.Chi));
    }

    // The seat restriction the tracker already relies on: a chi only comes from our left.
    [Fact]
    public void Chi_requires_the_seat_to_our_left()
    {
        Assert.True(Infer("34m123p456s789s1z", "5m", allowChi: true).HasFlag(ClaimOptions.Chi));
        Assert.False(Infer("34m123p456s789s1z", "5m", allowChi: false).HasFlag(ClaimOptions.Chi));
    }

    [Fact]
    public void Ron_is_the_winning_shape_only()
    {
        // Complete on 3m: 123m 456p 789s 111z + 99m pair.
        Assert.True(Infer("12m99m456p789s111z", "3m").HasFlag(ClaimOptions.Ron));
        Assert.False(Infer("12m99m456p789s112z", "3m").HasFlag(ClaimOptions.Ron));
    }

    [Fact]
    public void Melds_count_toward_the_winning_shape()
    {
        // 10 closed + one meld: the claim completes the last set.
        Assert.True(Infer("12m99m456p789s", "3m", melds: 1).HasFlag(ClaimOptions.Ron));
    }

    [Fact]
    public void Has_any_legal_call_agrees_with_the_detailed_inference()
    {
        foreach (var (hand, tile, chi) in new[]
                 {
                     ("55m123p456s789s11z", "5m", true),
                     ("34m123p456s789s11z", "5m", true),
                     ("34m123p456s789s11z", "5m", false),
                     ("19m19p19s1234567z", "1z", true),
                 })
        {
            var closed = TestTiles.Parse(hand);
            var t = Tile.Parse(tile);
            Assert.Equal(HandTracking.InferClaims(closed, t, 0, chi) != ClaimOptions.None,
                HandTracking.HasAnyLegalCall(closed, t, 0, chi));
        }
    }
}
