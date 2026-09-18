using MahjongHater.Core;
using Xunit;

namespace MahjongHater.Tests;

public class TileFaceMapTests
{
    [Fact]
    public void Resolves_after_two_consistent_observations()
    {
        var map = new TileFaceMap();
        map.Observe("a21p7", Tile.Parse("5m"));
        Assert.False(map.Resolved.ContainsKey("a21p7")); // one observation is not enough

        map.Observe("a21p7", Tile.Parse("5m"));
        Assert.True(map.Resolved.TryGetValue("a21p7", out var tile));
        Assert.Equal(Tile.Parse("5m"), tile);
    }

    [Fact]
    public void Conflicting_observations_prevent_resolution()
    {
        var map = new TileFaceMap();
        map.Observe("a21p7", Tile.Parse("5m"));
        map.Observe("a21p7", Tile.Parse("6m"));
        map.Observe("a21p7", Tile.Parse("5m"));
        // 5m has 2 of 3 observations — below the 80% dominance bar.
        Assert.False(map.Resolved.ContainsKey("a21p7"));
    }

    [Fact]
    public void Dominant_tile_wins_over_scattered_noise()
    {
        var map = new TileFaceMap();
        for (var i = 0; i < 9; i++)
            map.Observe("a21p7", Tile.Parse("5m"));
        map.Observe("a21p7", Tile.Parse("6m")); // one mis-attribution

        Assert.True(map.Resolved.TryGetValue("a21p7", out var tile));
        Assert.Equal(Tile.Parse("5m"), tile);
    }

    [Fact]
    public void Red_five_observations_are_normalized_to_the_kind()
    {
        var map = new TileFaceMap();
        map.Observe("a21p9", Tile.Parse("0m")); // hover says red
        map.Observe("a21p9", Tile.Parse("5m")); // AtkValues says plain

        // Both count toward the same kind — no conflict, resolves to plain 5m.
        Assert.True(map.Resolved.TryGetValue("a21p9", out var tile));
        Assert.Equal(Tile.Parse("5m"), tile);
    }

    [Fact]
    public void Persistence_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"facemap_test_{Guid.NewGuid():N}.json");
        try
        {
            var map = new TileFaceMap();
            map.Observe("a21p7", Tile.Parse("5m"));
            map.Observe("a21p7", Tile.Parse("5m"));
            map.Observe("i76043p0", Tile.Parse("3m"));
            map.Save(path);

            var loaded = new TileFaceMap();
            loaded.Load(path);
            Assert.True(loaded.Resolved.TryGetValue("a21p7", out var tile));
            Assert.Equal(Tile.Parse("5m"), tile);
            Assert.Equal(2, loaded.KeyCount);
            Assert.Equal(1, loaded.ResolvedCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_of_missing_or_corrupt_file_is_harmless()
    {
        var map = new TileFaceMap();
        map.Load(Path.Combine(Path.GetTempPath(), "does_not_exist_facemap.json"));
        Assert.Equal(0, map.KeyCount);

        var corrupt = Path.Combine(Path.GetTempPath(), $"facemap_corrupt_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(corrupt, "{not json");
            map.Load(corrupt);
            Assert.Equal(0, map.KeyCount);
        }
        finally
        {
            File.Delete(corrupt);
        }
    }
}
