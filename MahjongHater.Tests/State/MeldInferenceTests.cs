using MahjongHater.Core;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.State;

public class MeldInferenceTests
{
    [Fact]
    public void Two_copies_leaving_with_the_called_tile_is_a_pon()
    {
        var before = TestTiles.Parse("22z34567m11p3459s");
        var after = TestTiles.Parse("34567m11p3459s");
        var meld = MeldInference.Infer(before, after, Tile.Parse("2z"));
        Assert.NotNull(meld);
        Assert.Equal(MeldType.Pon, meld!.Type);
        Assert.True(meld.IsOpen);
        Assert.Equal(3, meld.Tiles.Length);
    }

    [Fact]
    public void Two_neighbours_leaving_is_a_chi_sorted()
    {
        var before = TestTiles.Parse("46m11p222p333s7z77z");
        var after = TestTiles.Parse("11p222p333s7z77z");
        var meld = MeldInference.Infer(before, after, Tile.Parse("5m"));
        Assert.NotNull(meld);
        Assert.Equal(MeldType.Chi, meld!.Type);
        Assert.Equal(TestTiles.Parse("456m"), meld.Tiles);
    }

    [Fact]
    public void Three_copies_leaving_is_a_daiminkan()
    {
        var before = TestTiles.Parse("222z34567m11p345s");
        var after = TestTiles.Parse("34567m11p3459s");
        var meld = MeldInference.Infer(before, after, Tile.Parse("2z"));
        Assert.Equal(MeldType.Daiminkan, meld!.Type);
        Assert.Equal(4, meld.Tiles.Length);
    }

    [Fact]
    public void Four_copies_leaving_without_a_call_is_an_ankan()
    {
        var before = TestTiles.Parse("2222z34567m11p345s");
        var after = TestTiles.Parse("34567m11p3459s");
        var meld = MeldInference.Infer(before, after, null);
        Assert.Equal(MeldType.Ankan, meld!.Type);
        Assert.False(meld.IsOpen);
    }

    [Fact]
    public void Non_meld_deltas_are_rejected()
    {
        var before = TestTiles.Parse("19m11p222p333s7z77z");
        var after = TestTiles.Parse("11p222p333s7z77z");
        Assert.Null(MeldInference.Infer(before, after, Tile.Parse("5m")));   // 1m 9m 5m is no run
        Assert.Null(MeldInference.Infer(before, before, Tile.Parse("5m")));  // nothing left the hand
    }

    [Fact]
    public void Removed_keeps_red_fives_distinct_but_matches_by_kind_as_fallback()
    {
        var before = TestTiles.Parse("05m");
        var after = TestTiles.Parse("5m");
        var removed = MeldInference.Removed(before, after);
        Assert.Single(removed);
        Assert.True(removed[0].IsRedFive);
    }

    [Fact]
    public void Signature_is_order_independent()
    {
        var a = new Meld(MeldType.Chi, TestTiles.Parse("456m").ToArray(), true);
        var b = new Meld(MeldType.Chi, TestTiles.Parse("645m").ToArray(), true);
        Assert.Equal(MeldInference.Signature(a), MeldInference.Signature(b));
    }
}
