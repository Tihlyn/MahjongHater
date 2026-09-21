using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

// Pins the table-driven danger model (docs/DEFENSE_PLAN.md §1.2, §3.1) to the measured
// Houou rates in resources/policy/deal_in_rates.json and to the literature's modifiers.
public class TileDangerModelTests
{
    private static readonly DealInRateTable Table = DealInRateTable.Default;

    private static double Danger(StateSnapshot state, string tile, int seat = 1) => Model(state).Danger(Tile.Parse(tile), seat);

    private static DangerEstimate Explain(StateSnapshot state, string tile, int seat = 1) => Model(state).Explain(Tile.Parse(tile), seat);

    // A mid-game riichi: six manzu/pinzu discards (12 live suji, souzu and honors untouched),
    // riichi tile 9p; extra discards kill more lines on request.
    private static StateSnapshot MidRiichi(string extra = "", string hand = "", int seat = 1) =>
        Seat(Snap(hand), seat, "1m9m2m8m1p9p" + extra, riichi: true, riichiIndex: 5);

    // Manzu/pinzu discards that kill 1-4m, 6-9m, 1-4p, 6-9p (14 live suji) without touching souzu.
    private const string KillFour = "1m9m1p9p";

    [Fact]
    public void Table_rows_match_the_research_csv()
    {
        // Smoothed (3-point) cells: non-suji 4 at 10 live = mean(13.2, 14.3, 15.5) %.
        Assert.Equal((0.132 + 0.143 + 0.155) / 3, Table.Number(DealInRateTable.NumberClass.NonSuji, 4, 10), 4);
        Assert.Equal((0.012 + 0.012 + 0.013) / 3, Table.Number(DealInRateTable.NumberClass.Suji, 1, 10), 4);
        // Honors: the row total scaled by the live-suji shape (about 1.0 near 11 live suji).
        Assert.InRange(Table.Honor(DealInRateTable.HonorClass.Dragon, 0, false, 11), 0.028, 0.037);
        Assert.Equal(0.0, Table.Honor(DealInRateTable.HonorClass.Guest, 3, false, 11), 6);
        Assert.True(Table.Honor(DealInRateTable.HonorClass.Dragon, 0, false, 4) > Table.Honor(DealInRateTable.HonorClass.Dragon, 0, false, 14));
    }

    [Fact]
    public void Missing_cells_fall_back_to_the_nearest_sample()
    {
        // Suji rows have no 18/17 live-suji samples (a suji needs a genbutsu); use the 16 column.
        Assert.Equal(Table.Number(DealInRateTable.NumberClass.Suji, 1, 16), Table.Number(DealInRateTable.NumberClass.Suji, 1, 18), 6);
        // Non-suji 4 has no 1-live-suji sample; use the 2 column.
        Assert.Equal(Table.Number(DealInRateTable.NumberClass.NonSuji, 4, 2), Table.Number(DealInRateTable.NumberClass.NonSuji, 4, 1), 6);
    }

    [Fact]
    public void Honors_are_safer_than_terminals_which_are_safer_than_middles()
    {
        var state = MidRiichi(KillFour);
        var honor = Danger(state, "5z");
        var terminal = Danger(state, "1s");
        var edge = Danger(state, "2s");
        var three = Danger(state, "3s");
        var middle = Danger(state, "5s");
        Assert.True(honor < terminal, $"honor {honor} vs terminal {terminal}");
        Assert.True(terminal < edge, $"terminal {terminal} vs 2 {edge}");
        Assert.True(edge < three, $"2 {edge} vs 3 {three}");
        Assert.True(three < middle, $"3 {three} vs 5 {middle}");
    }

    [Fact]
    public void Honor_danger_drops_with_visible_copies_and_vanishes_at_three()
    {
        // The copy we throw is always visible; "n visible" in the tables means the others
        // (Riichi Book 1: the "fourth honor" has three already out). Two others visible
        // still allows a tanki; three leaves only kokushi.
        var one = Danger(MidRiichi(hand: "5z"), "5z");
        var two = Danger(MidRiichi(hand: "55z"), "5z");
        var three = Danger(MidRiichi(hand: "555z"), "5z");
        Assert.True(two < one && three < two, $"{one} {two} {three}");
        Assert.InRange(three, 0.0001, 0.004);
        // The fourth copy (three in seat 2's pond, ours in hand) is only a kokushi risk.
        var fourth = Seat(MidRiichi(hand: "5z"), 2, "555z");
        Assert.Equal(Table.Multipliers.HonorKokushiFloor, Danger(fourth, "5z"), 6);
        // Four visible 1m rule kokushi out: the fourth honor is then absolutely safe.
        Assert.Equal(0, Danger(Seat(MidRiichi(hand: "5z1111m"), 2, "555z"), "5z"));
    }

    [Fact]
    public void Yakuhai_honors_are_more_dangerous_than_guest_winds()
    {
        // Seat 1 is South when we are the dealer (East); East round. Honors it discarded
        // would be genbutsu, so this riichi seat only threw number tiles.
        var state = Seat(Snap(""), riichi: true, riichiIndex: 3, discards: "1m9m2m8m") with { DealerSeat = 0, RoundWind = Wind.East };
        Assert.True(Danger(state, "2z") > Danger(state, "3z"), "seat wind > guest wind");
        Assert.True(Danger(state, "1z") > Danger(state, "3z"), "round wind > guest wind");
        Assert.True(Danger(state, "5z") > Danger(state, "3z"), "dragon > guest wind");
    }

    [Fact]
    public void Suji_classes_follow_the_measured_ratios()
    {
        // 5s as the riichi tile makes 2s a riichi-tile suji; 5s thrown before the riichi tile makes it a plain suji.
        var onRiichi = Seat(Snap(""), riichi: true, riichiIndex: 4, discards: KillFour + "5s");
        var before = Seat(Snap(""), riichi: true, riichiIndex: 5, discards: KillFour + "5s1z");
        var raw = Seat(Snap(""), riichi: true, riichiIndex: 4, discards: KillFour + "1z");
        var nonSuji = Danger(raw, "2s");
        var riichiSuji = Danger(onRiichi, "2s");
        var normalSuji = Danger(before, "2s");
        Assert.Equal("riichi-tile suji", Explain(onRiichi, "2s").Class);
        Assert.Equal("suji", Explain(before, "2s").Class);
        Assert.True(normalSuji < riichiSuji, $"suji {normalSuji} should be safer than riichi-tile suji {riichiSuji}");
        Assert.True(riichiSuji < nonSuji, $"riichi-tile suji {riichiSuji} should be safer than non-suji {nonSuji}");
        Assert.True(normalSuji / nonSuji < 0.5, $"suji 2 ({normalSuji}) is well under half of non-suji 2 ({nonSuji})");
        // Suji 1/9 are the safest suji: about a quarter of the raw tile.
        var suji1 = Danger(Seat(Snap(""), riichi: true, riichiIndex: 5, discards: KillFour + "4s1z"), "1s");
        Assert.True(suji1 / Danger(raw, "1s") < 0.4, $"suji 1 {suji1} vs raw 1 {Danger(raw, "1s")}");
    }

    [Fact]
    public void Middle_tiles_go_non_suji_half_suji_nakasuji()
    {
        var none = Seat(Snap(""), riichi: true, riichiIndex: 0, discards: "1z" + KillFour);
        var half = Seat(Snap(""), riichi: true, riichiIndex: 0, discards: "1z" + KillFour + "2s");
        var naka = Seat(Snap(""), riichi: true, riichiIndex: 0, discards: "1z" + KillFour + "2s8s");
        Assert.Equal("non-suji", Explain(none, "5s").Class);
        Assert.Equal("half-suji", Explain(half, "5s").Class);
        Assert.Equal("nakasuji", Explain(naka, "5s").Class);
        Assert.True(Danger(naka, "5s") < Danger(half, "5s") && Danger(half, "5s") < Danger(none, "5s"));
        Assert.True(Danger(naka, "5s") / Danger(none, "5s") < 0.25, "nakasuji is about 15 % of non-suji");
    }

    [Fact]
    public void Kabe_makes_a_terminal_suji_equivalent_and_one_chance_discounts()
    {
        var open = MidRiichi(KillFour);
        var oneChance = MidRiichi(KillFour, hand: "222s");
        var noChance = MidRiichi(KillFour, hand: "2222s");
        Assert.True(Danger(oneChance, "1s") < Danger(open, "1s"));
        Assert.True(Danger(noChance, "1s") < Danger(oneChance, "1s"));
        Assert.Equal("suji", Explain(noChance, "1s").Class);
        Assert.Contains("one-chance", Explain(oneChance, "1s").Why);
    }

    [Fact]
    public void Tiles_outside_an_early_discard_are_safer()
    {
        var early = Seat(Snap(""), riichi: true, riichiIndex: 6, discards: "2s1z2z3z4z5z6z" + KillFour);
        var late = Seat(Snap(""), riichi: true, riichiIndex: 6, discards: "1z2z3z4z5z6z2s" + KillFour);
        Assert.True(Danger(early, "1s") < Danger(late, "1s"));
        Assert.Contains("outside an early discard", Explain(early, "1s").Why);
        Assert.Equal(Danger(early, "9s"), Danger(late, "9s"), 9);
    }

    [Fact]
    public void Dora_and_its_neighbours_are_more_dangerous()
    {
        var state = MidRiichi(KillFour);
        var withDora = state with { DoraIndicators = [Tile.Parse("4s")] };
        Assert.True(Danger(withDora, "5s") > Danger(state, "5s"));
        Assert.True(Danger(withDora, "6s") > Danger(state, "6s"));
        Assert.Equal(Danger(withDora, "5p"), Danger(state, "5p"), 9);
        Assert.Equal(Table.Multipliers.DoraTile, Danger(withDora, "5s") / Danger(state, "5s"), 6);
    }

    [Fact]
    public void Non_suji_danger_rises_as_suji_die()
    {
        // The riichi seat's discards kill suji lines; a raw 5p gets more dangerous.
        var early = Seat(Snap(""), riichi: true, riichiIndex: 0, discards: "1z");
        var late = Seat(Snap(""), riichi: true, riichiIndex: 0, discards: "1z1m4m7m1s4s7s2m5m8m");
        Assert.True(Danger(late, "5p") > Danger(early, "5p"), $"late {Danger(late, "5p")} vs early {Danger(early, "5p")}");
    }

    [Fact]
    public void Live_suji_counts_lines_killed_by_genbutsu_and_kabe()
    {
        var visible = new int[34];
        var genbutsu = new bool[34];
        Assert.Equal(18, TileDangerModel.CountLiveSuji(genbutsu, visible));
        genbutsu[TileHelpers.ToIndex(Tile.Parse("4s"))] = true;          // kills 1-4s and 4-7s
        Assert.Equal(16, TileDangerModel.CountLiveSuji(genbutsu, visible));
        visible[TileHelpers.ToIndex(Tile.Parse("2m"))] = 4;               // kills 1-4m (protorun 2,3)
        Assert.Equal(15, TileDangerModel.CountLiveSuji(genbutsu, visible));
    }

    [Fact]
    public void Discard_order_marks_post_riichi_discards_safe_across_calls()
    {
        var state = Seat(Seat(Snap(""), 1, "1m", true, 0), 2, "2p5s", melds: [Meld.MakePon(Tile.Parse("5z"), true)]);
        // Without order the call blocks the inference (round indices are unreliable).
        Assert.True(Danger(state, "5s") > 0);
        var seats = state.Seats.ToArray();
        seats[1] = seats[1] with { DiscardOrder = [0] };
        seats[2] = seats[2] with { DiscardOrder = [1, 2] };
        var ordered = state with { Seats = seats };
        Assert.Equal(0, Danger(ordered, "5s"));
        Assert.True(Danger(ordered, "2p") == 0);
    }

    [Fact]
    public void Kamichas_fresh_discard_is_safe_against_everyone()
    {
        var state = Seat(Seat(Snap(""), 3, "5s"), 1, "1z", riichi: true, riichiIndex: 0);
        var seats = state.Seats.ToArray();
        seats[1] = seats[1] with { DiscardOrder = [0] };
        seats[3] = seats[3] with { DiscardOrder = [1] };
        Assert.Equal(0, Danger(state with { Seats = seats }, "5s", 1));
    }

    [Fact]
    public void Primary_threat_prefers_riichi_then_dealer()
    {
        var none = Model(Snap(""));
        Assert.Equal(-1, none.PrimaryThreat());
        var one = Model(Seat(Snap(""), 2, riichi: true));
        Assert.Equal(2, one.PrimaryThreat());
        var two = Model(Seat(Seat(Snap(""), 2, riichi: true), 3, riichi: true) with { DealerSeat = 3 });
        Assert.Equal(3, two.PrimaryThreat());
    }

    [Fact]
    public void Ranks_follow_the_probability_bands()
    {
        var model = new TileDangerModel();
        Assert.Equal(DangerRank.S, model.RankOf(0));
        Assert.Equal(DangerRank.APlus, model.RankOf(0.009));
        Assert.Equal(DangerRank.B, model.RankOf(0.029));
        Assert.Equal(DangerRank.C, model.RankOf(0.034));
        Assert.Equal(DangerRank.D, model.RankOf(0.05));
        Assert.Equal(DangerRank.E, model.RankOf(0.07));
        Assert.Equal(DangerRank.F, model.RankOf(0.123));
    }

    [Fact]
    public void Candidates_carry_the_primary_threats_danger()
    {
        var state = Seat(Snap("123m456m4578p447s1z"), 2, "4s", riichi: true, riichiIndex: 0);
        var model = Model(state);
        var ranked = new HeuristicDiscardPolicy().Rank(state, model, CancellationToken.None);
        var honor = ranked.Single(c => c.Tile.Equals(Tile.Parse("1z")));
        Assert.Equal(2, honor.DangerSeat);
        Assert.Equal(model.Danger(Tile.Parse("1z"), 2), honor.Danger, 9);
        Assert.NotEqual(string.Empty, honor.DangerNote);
    }
}
