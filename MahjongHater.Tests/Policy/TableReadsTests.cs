using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

// Table reads: the fold read, tenpai corrected by what draws revealed, our win estimate scaled
// by our own results, and the open-hand value scale.
public class TableReadsTests
{
    private static readonly PolicyWeights V2 = PolicyWeights.Default with { DefenseModel = DefenseModel.V2 };

    private static readonly Meld Pon = new(MeldType.Pon, TestTiles.Parse("555z").ToArray(), true);
    private static readonly Meld Chi = new(MeldType.Chi, TestTiles.Parse("234s").ToArray(), true);

    private static SeatState Open(int calls) => new(1, TestTiles.Parse("1z9m2p8s"), [.. new[] { Pon, Chi, Pon }.Take(calls)], false, -1, 25000);

    private static double Logistic(double z) => 1 / (1 + Math.Exp(-z));

    [Fact]
    public void Without_reveals_the_estimate_is_unchanged()
    {
        var store = new TenpaiReveals();
        Assert.Equal(0.55, store.Adjust(1, 0.55, Open(2), 0, apply: true), 9);
    }

    [Fact]
    public void Noten_calls_at_draws_lower_that_call_group_only()
    {
        // 200 two-call seats read at 55 %, 30 of them tenpai: the measured arena casual seats.
        var rows = Enumerable.Range(0, 200).Select(i => new TenpaiReveals.Row(2, Math.Log(0.55 / 0.45), 0, i < 30));
        var store = new TenpaiReveals(rows);
        var corrected = store.Adjust(1, 0.55, Open(2), 0, apply: true);
        Assert.InRange(corrected, 0.13, 0.18);
        Assert.Equal(0.55, store.Adjust(1, 0.55, Open(1), 0, apply: true), 9);
    }

    [Fact]
    public void Loose_seats_are_corrected_more_when_loose_seats_were_the_noten_ones()
    {
        // Steady seats: as read. Loose seats (never fold): one in five of the read rate.
        var rows = Enumerable.Range(0, 400).Select(i => i % 2 == 0
            ? new TenpaiReveals.Row(1, 0, 0, i % 4 == 0)
            : new TenpaiReveals.Row(1, 0, 1, i % 20 == 1));
        var store = new TenpaiReveals(rows);
        var steady = store.Adjust(1, 0.5, Open(1), 0, apply: true);
        var loose = store.Adjust(2, 0.5, Open(1), 1, apply: true);
        Assert.InRange(steady, 0.42, 0.58);
        Assert.InRange(loose, 0.06, 0.16);
    }

    [Fact]
    public void The_fit_converges_on_confident_estimates_that_were_mostly_wrong()
    {
        // The arena case that overshot to a = +673: v2's network reads three-call seats at
        // 99.5-99.99 % (no river-tell cap), 41 % were tenpai, looseness near zero.
        var random = new Random(7);
        var rows = Enumerable.Range(0, 691).Select(i =>
        {
            var p = 0.995 + 0.0049 * random.NextDouble();
            return new TenpaiReveals.Row(3, Math.Log(p / (1 - p)), 0.15 * random.NextDouble(), random.NextDouble() < 0.41);
        }).ToList();
        var store = new TenpaiReveals(rows);
        var (a, b) = store.Coefficients(3);
        Assert.InRange(a, -10, 0);
        Assert.InRange(b, -5, 5);
        var mean = rows.Average(r => Logistic(r.Logit + a + b * r.Looseness));
        Assert.InRange(mean, 0.36, 0.46);
    }

    [Fact]
    public void A_draw_labels_the_last_estimate_and_skips_riichi_seats()
    {
        var store = new TenpaiReveals();
        store.Adjust(1, 0.6, Open(2), 0.5, apply: true);
        store.Adjust(2, 0.4, Open(1) with { Seat = 2, Riichi = true, RiichiDiscardIndex = 0 }, 0, apply: true);
        store.Adjust(3, 0.3, Open(0) with { Seat = 3 }, 0, apply: true);
        store.Draw([null, false, true, null]);
        var row = Assert.Single(store.Rows);
        Assert.Equal(new TenpaiReveals.Row(2, Math.Log(0.6 / 0.4), 0.5, false), row);
        Assert.True(store.SeatOffset(1) < 0);
        Assert.Equal(0, store.SeatOffset(3));
    }

    [Fact]
    public void A_frozen_store_keeps_its_population_fit_but_follows_the_match()
    {
        var rows = Enumerable.Range(0, 100).Select(i => new TenpaiReveals.Row(2, 0, 0, i < 20)).ToList();
        var store = new TenpaiReveals(rows, learn: false);
        var before = store.Coefficients(2);
        store.Adjust(1, 0.5, Open(2), 0, apply: true);
        store.Draw([null, false, null, null]);
        Assert.Equal(100, store.Rows.Count);
        Assert.Equal(before, store.Coefficients(2));
        Assert.True(store.SeatOffset(1) < 0);
    }

    [Fact]
    public void A_new_match_forgets_the_seat_offsets()
    {
        var store = new TenpaiReveals(learn: false);
        store.Adjust(1, 0.5, Open(2), 0, apply: true);
        store.Draw([null, false, null, null]);
        Assert.NotEqual(0, store.SeatOffset(1));
        store.Observe(Snap() with { HandNumber = 1, RoundWind = Wind.East, Seats = [.. Enumerable.Range(0, 4).Select(i => SeatState.Empty(i) with { Score = 25000 })] });
        Assert.Equal(0, store.SeatOffset(1));
    }

    [Fact]
    public void Rows_round_trip_through_csv()
    {
        var row = new TenpaiReveals.Row(3, -0.4321, 0.25, true);
        Assert.Equal(row, TenpaiReveals.FromCsv(TenpaiReveals.ToCsv(row)));
        Assert.Null(TenpaiReveals.FromCsv("9,0,0,1"));
    }

    [Fact]
    public void A_seat_that_pushes_through_riichi_reads_as_not_folding()
    {
        // Seat 2 declares on its third discard (order 11); seat 1 then throws three tiles.
        StateSnapshot After(string seat1)
        {
            var state = Seat(Seat(Snap(), 2, "1z9m5p", riichi: true, riichiIndex: 2), 1, "2z8s" + seat1);
            return state with
            {
                Seats = [.. state.Seats.Select(s => s.Seat switch
                {
                    1 => s with { DiscardOrder = [2, 6, 12, 14, 18] },
                    2 => s with { DiscardOrder = [3, 7, 11] },
                    _ => s,
                })],
            };
        }

        var unknown = new OpponentProfiles();
        Assert.Equal(1, unknown.Steadiness(1, V2));
        var folded = new OpponentProfiles();
        folded.Observe(After("1z9m5p"));
        var pushed = new OpponentProfiles();
        pushed.Observe(After("4s6m3p"));
        Assert.Equal(1, folded.Steadiness(1, V2));
        Assert.True(pushed.Steadiness(1, V2) < 0.4, $"steadiness {pushed.Steadiness(1, V2):0.00}");
        Assert.Equal((0, 3), OpponentProfiles.FoldCounts(After("4s6m3p").Seats[1], After("4s6m3p")));
    }

    [Fact]
    public void Our_wins_on_loose_tables_raise_the_win_factor()
    {
        Assert.Equal(1, new WinReveals().Factor(false));
        // Hazard 0.5 predicts 39 %; these tables let 70 % win.
        var rows = Enumerable.Range(0, 300).Select(i => new WinReveals.Row(false, 0.5, i % 10 < 7));
        var store = new WinReveals(rows);
        var factor = store.Factor(false);
        Assert.InRange(1 - Math.Exp(-0.5 * factor), 0.66, 0.74);
        Assert.Equal(1, store.Factor(true));
        var w = PolicyWeights.Default;
        Assert.True(HandValue.WinProbability(0, 6, 0, 50, w, factor: factor) > HandValue.WinProbability(0, 6, 0, 50, w));
    }

    [Fact]
    public void Only_the_first_tenpai_of_a_finished_hand_is_a_row()
    {
        var store = new WinReveals();
        store.Tenpai(false, 0.3);
        store.Tenpai(true, 0.9);
        store.HandEnded(won: true);
        Assert.Equal(new WinReveals.Row(false, 0.3, true), Assert.Single(store.Rows));

        // A tenpai whose hand end was never seen is dropped when our river restarts.
        store.Observe(Seat(Snap(), 0, "1z9m5p2z"));
        store.Tenpai(false, 0.4);
        store.Observe(Seat(Snap(), 0, "1z"));
        store.HandEnded(won: false);
        Assert.Single(store.Rows);
        Assert.Equal(new WinReveals.Row(true, 0.12345, false), WinReveals.FromCsv(WinReveals.ToCsv(new WinReveals.Row(true, 0.12345, false))));
    }

    [Fact]
    public void The_win_factor_applies_only_when_switched_on()
    {
        var rows = Enumerable.Range(0, 300).Select(i => new WinReveals.Row(false, 0.5, i % 10 < 7)).ToList();
        var off = new OpponentModel(V2, wins: new WinReveals(rows));
        var on = new OpponentModel(V2 with { WinReveals = true }, wins: new WinReveals(rows));
        Assert.Equal(1, off.WinRateFactor(false));
        Assert.True(on.WinRateFactor(false) > 1.5);
    }

    [Fact]
    public void Open_hand_value_follows_the_scale()
    {
        var state = Snap() with { Seats = [.. Snap().Seats.Select(s => s.Seat == 1 ? Open(2) : s)] };
        var full = Model(state, V2).Value(1);
        var scaled = Model(state, V2 with { OpenValueScale = 0.75 }).Value(1);
        Assert.Equal(full * 0.75, scaled, 6);
        Assert.Equal(1, ThreatValue.OpenScale(Open(0), V2 with { OpenValueScale = 0.75 }));
    }

    [Fact]
    public void The_opponent_model_applies_reveals_only_when_switched_on()
    {
        var rows = Enumerable.Range(0, 200).Select(i => new TenpaiReveals.Row(2, 0, 0, i < 20));
        var state = Snap() with { Seats = [.. Snap().Seats.Select(s => s.Seat == 1 ? Open(2) : s)] };
        var off = new OpponentModel(V2, reveals: new TenpaiReveals(rows));
        off.Update(state);
        var on = new OpponentModel(V2 with { TenpaiReveals = true }, reveals: new TenpaiReveals(rows));
        on.Update(state);
        var plain = Model(state, V2);
        Assert.Equal(plain.TenpaiProbability(1), off.TenpaiProbability(1), 9);
        Assert.True(on.TenpaiProbability(1) < plain.TenpaiProbability(1) * 0.5);
        Assert.Equal(Logistic(Math.Log(plain.TenpaiProbability(1) / (1 - plain.TenpaiProbability(1))) + new TenpaiReveals(rows).Coefficients(2).A), on.TenpaiProbability(1), 6);
    }
}
