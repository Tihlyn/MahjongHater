using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

public class OpponentModelTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Own_discards_are_genbutsu(int seat)
    {
        var model = Model(Seat(State(""), seat, "5s"));
        Assert.Equal(0, model.Danger(Tile.Parse("5s"), seat));
        Assert.Equal(0, model.Danger(new Tile(TileSuit.Sou, 5, true), seat));
    }

    [Fact]
    public void Later_discard_rounds_are_genbutsu_after_riichi()
    {
        var state = Seat(Seat(State(""), 1, "1m", true, 0), 2, "2p5s");
        Assert.Equal(0, Model(state).Danger(Tile.Parse("5s"), 1));
    }

    [Fact]
    public void Earlier_opponent_discard_is_not_genbutsu()
    {
        var state = Seat(Seat(State(""), 1, "123m", true, 2), 2, "5s");
        Assert.True(Model(state).Danger(Tile.Parse("5s"), 1) > 0);
    }

    [Fact]
    public void Later_seat_in_same_round_is_safe_after_riichi()
    {
        var state = Seat(Seat(State(""), 1, "1m", true, 0), 2, "5s");
        Assert.Equal(0, Model(state).Danger(Tile.Parse("5s"), 1));
    }

    [Fact]
    public void Earlier_seat_in_same_round_is_not_safe_after_riichi()
    {
        var state = Seat(Seat(State(""), 2, "1m", true, 0), 1, "5s");
        Assert.True(Model(state).Danger(Tile.Parse("5s"), 2) > 0);
    }

    [Fact]
    public void Calls_prevent_unsafe_inferences_from_discard_indices()
    {
        var state = Seat(Seat(State(""), 1, "1m", true, 0), 2, "2p5s", melds: [Meld.MakePon(Tile.Parse("5z"), true)]);
        Assert.True(Model(state).Danger(Tile.Parse("5s"), 1) > 0);
        var offered = state with { CallTile = Tile.Parse("5s"), CallFromSeat = 2 };
        Assert.Equal(0, Model(offered).Danger(Tile.Parse("5s"), 1));
    }

    [Fact]
    public void Unknown_riichi_index_does_not_mark_other_discards_safe()
    {
        var state = Seat(Seat(State(""), 1, riichi: true), 2, "5s");
        Assert.True(Model(state).Danger(Tile.Parse("5s"), 1) > 0);
    }

    [Fact]
    public void Genbutsu_uses_configured_floor()
    {
        Assert.Equal(0.01, Model(Seat(State(""), discards: "5s"), new PolicyWeights { GenbutsuDanger = 0.01 })
            .Danger(Tile.Parse("5s"), 1));
    }

    [Fact]
    public void Suji_discounts_terminal_danger()
    {
        var before = Model(State(""));
        var after = Model(Seat(State(""), discards: "4s"));
        Assert.True(after.Danger(Tile.Parse("1s"), 1) < before.Danger(Tile.Parse("1s"), 1));
    }

    [Fact]
    public void Middle_suji_requires_both_sides()
    {
        var none = Model(State(""));
        var one = Model(Seat(State(""), discards: "2s"));
        var both = Model(Seat(State(""), discards: "28s"));
        Assert.Equal(none.Danger(Tile.Parse("5s"), 1), one.Danger(Tile.Parse("5s"), 1));
        Assert.True(both.Danger(Tile.Parse("5s"), 1) < one.Danger(Tile.Parse("5s"), 1));
    }

    [Fact]
    public void Visible_honors_reduce_danger_to_zero_at_four()
    {
        var zero = Model(State(""));
        var three = Model(State("555z"));
        var four = Model(State("5555z"));
        Assert.True(three.Danger(Tile.Parse("5z"), 1) < zero.Danger(Tile.Parse("5z"), 1));
        Assert.Equal(0, four.Danger(Tile.Parse("5z"), 1));
    }

    [Fact]
    public void Four_visible_neighbors_discount_kabe()
    {
        Assert.True(Model(State("2222s")).Danger(Tile.Parse("1s"), 1)
            < Model(State("")).Danger(Tile.Parse("1s"), 1));
    }

    [Fact]
    public void Terminals_are_safer_than_middles()
    {
        var model = Model(State(""));
        Assert.True(model.Danger(Tile.Parse("1s"), 1) < model.Danger(Tile.Parse("5s"), 1));
    }

    [Fact]
    public void Tenpai_increases_with_discards_and_riichi()
    {
        var early = Model(Seat(State(""), discards: "12m"));
        var late = Model(Seat(State(""), discards: "123m456p789s123z"));
        var riichi = Model(Seat(State(""), riichi: true));
        Assert.True(early.TenpaiProbability(1) < late.TenpaiProbability(1));
        Assert.True(late.TenpaiProbability(1) < riichi.TenpaiProbability(1));
        Assert.Equal(1, riichi.TenpaiProbability(1));
    }

    [Fact]
    public void Open_melds_increase_tenpai()
    {
        Assert.True(Model(Seat(State(""), melds: [Meld.MakePon(Tile.Parse("2s"), true)])).TenpaiProbability(1)
            > Model(State("")).TenpaiProbability(1));
    }

    [Fact]
    public void Early_outside_and_late_middle_discards_increase_tenpai()
    {
        Assert.True(Model(Seat(State(""), discards: "19m19p19s456m")).TenpaiProbability(1)
            > Model(Seat(State(""), discards: "234m567p123z")).TenpaiProbability(1));
    }

    [Fact]
    public void Expected_cost_sums_three_seat_contributions()
    {
        var state = State("");
        for (var seat = 1; seat <= 3; seat++)
            state = Seat(state, seat, riichi: true);
        var model = Model(state);
        var expected = Enumerable.Range(1, 3).Sum(s => model.TenpaiProbability(s) * model.Danger(Tile.Parse("5s"), s) * 4000);
        Assert.Equal(expected, model.ExpectedDealInCost(Tile.Parse("5s")), 8);
    }

    [Fact]
    public void Visible_dora_in_opponent_melds_increases_cost()
    {
        var state = Seat(State(""), melds: [Meld.MakePon(Tile.Parse("5p"), true)]);
        Assert.True(Model(state with { DoraIndicators = [Tile.Parse("4p")] }).ExpectedDealInCost(Tile.Parse("5s"))
            > Model(state).ExpectedDealInCost(Tile.Parse("5s")));
    }

    [Fact]
    public void Updates_are_idempotent_and_replace_old_round_data()
    {
        var state = Seat(State(""), discards: "5s", riichi: true);
        var model = Model(state);
        var cost = model.ExpectedDealInCost(Tile.Parse("4s"));
        model.Update(state);
        Assert.Equal(cost, model.ExpectedDealInCost(Tile.Parse("4s")));
        model.Update(State(""));
        Assert.True(model.TenpaiProbability(1) < 1);
        Assert.True(model.Danger(Tile.Parse("5s"), 1) > 0);
    }

    [Fact]
    public void Danger_and_probability_stay_in_range()
    {
        var state = Seat(State(""), discards: "123456789m123456789p123456789s1234567z");
        var model = Model(state);
        Assert.InRange(model.TenpaiProbability(1), 0, 1);
        for (var kind = 0; kind < 34; kind++)
            Assert.InRange(model.Danger(TileHelpers.FromIndex(kind), 1), 0, 1);
    }
}
