using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.State;

// Phase-0 instrumentation for the defense rework (docs/DEFENSE_PLAN.md §3.6, §3.8):
// global discard order, ron detection, hand number, reveal capture, deal-in sampling.
public class DefenseTrackingTests
{
    private static readonly DateTime T0 = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    private static AtkFrame Discard(int seat, string tile)
        => AtkFrame.OfInts(8, seat, StructFixture.IconOf(Tile.Parse(tile), 76041));

    private static AtkFrame TurnAdvance(int wall, int seat) => AtkFrame.OfInts(5, wall, seat, 76041);

    private static AtkFrame Win(int winner, string round) => AtkFrame.OfInts(32, winner).WithString(2, round);

    [Fact]
    public void Discards_get_a_global_order_across_seats()
    {
        var t = new EventTracker();
        t.OnRefresh(Discard(3, "1m"), T0);
        t.OnRefresh(Discard(0, "2m"), T0);
        t.OnRefresh(Discard(1, "3m"), T0);
        t.OnRefresh(Discard(3, "4m"), T0);
        Assert.Equal([0, 3], t.SeatDiscardOrderOf(3));
        Assert.Equal([1], t.SeatDiscardOrderOf(0));
        Assert.Equal([2], t.SeatDiscardOrderOf(1));
        Assert.Empty(t.SeatDiscardOrderOf(2));
    }

    [Fact]
    public void Win_right_after_a_discard_is_a_ron_on_it()
    {
        var t = new EventTracker();
        t.OnRefresh(TurnAdvance(50, 0), T0);
        t.OnRefresh(Discard(0, "5p"), T0);
        t.OnRefresh(Win(2, "East 3 South Wind"), T0);
        Assert.True(t.LastWinByRon);
        Assert.Equal(0, t.RonVictimSeat);
        Assert.Equal(Tile.Parse("5p"), t.RonTile);
        Assert.Equal(2, t.LastWinnerSeat);
        Assert.Equal(3, t.HandNumber);
    }

    [Fact]
    public void Win_after_a_draw_is_a_tsumo()
    {
        var t = new EventTracker();
        t.OnRefresh(Discard(0, "5p"), T0);
        t.OnRefresh(TurnAdvance(49, 1), T0);
        t.OnRefresh(Win(1, "South 1 East Wind"), T0);
        Assert.False(t.LastWinByRon);
        Assert.Equal(-1, t.RonVictimSeat);
        Assert.Null(t.RonTile);
        Assert.Equal(1, t.HandNumber);
    }

    [Fact]
    public void Own_ron_is_not_a_deal_in_by_us()
    {
        var t = new EventTracker();
        t.OnRefresh(Discard(2, "5p"), T0);
        t.OnRefresh(Win(0, "East 4 East Wind"), T0);
        Assert.True(t.LastWinByRon);
        Assert.Equal(2, t.RonVictimSeat);
    }

    [Theory]
    [InlineData("East 3 South Wind", 3)]
    [InlineData("South 4 North Wind", 4)]
    [InlineData("South Wind", null)]
    [InlineData("", null)]
    public void Hand_number_parses_the_first_integer_token(string text, int? expected)
    {
        Assert.Equal(expected, EventTracker.ParseHandNumber(text));
    }

    [Fact]
    public void Tile_runs_after_play_started_are_captured_as_reveal_candidates()
    {
        var t = new EventTracker();
        t.OnRefresh(Discard(0, "1m"), T0);
        var icons = TestTiles.Parse("123m456p789s1122z").Select(x => StructFixture.IconOf(x, 76041)).ToArray();
        t.OnRefresh(AtkFrame.OfInts([40, 2, .. icons]), T0);
        var run = Assert.Single(t.RevealedRuns);
        Assert.Equal(40, run.Type);
        Assert.Equal(13, run.Tiles.Count);
        Assert.Equal(2, run.Tiles[0].Index);
        Assert.Contains(t.RecentNotes(5), n => n.Contains("reveal? type=40"));
        // A short run (fewer than ten icons) is not a reveal.
        t.OnRefresh(AtkFrame.OfInts([41, .. icons.Take(5)]), T0);
        Assert.Single(t.RevealedRuns);
    }

    [Fact]
    public void Snapshot_carries_turn_hand_number_and_all_last()
    {
        var snap = Snap("123m456m4578p447s1z");
        Assert.Equal(1, snap.Turn);
        Assert.False(snap.IsAllLast);
        var east4 = Seat(snap, 0, "1z2z3z") with { HandNumber = 4, RoundWind = Wind.East, Ruleset = new RulesetOptions(true, 4) };
        Assert.Equal(4, east4.Turn);
        Assert.True(east4.IsAllLast);
        Assert.False((east4 with { Ruleset = new RulesetOptions(true, 8) }).IsAllLast);
        Assert.True((east4 with { Ruleset = new RulesetOptions(true, 8), RoundWind = Wind.South }).IsAllLast);
    }

    [Fact]
    public void Recorder_samples_our_discards_against_a_riichi_and_labels_the_ron()
    {
        var recorder = new DealInRecorder { Population = "human" };
        var decision = Seat(Snap("123m456m4578p447s1z"), 2, "4s1m", riichi: true, riichiIndex: 1);
        recorder.Observe(decision, T0);
        // We threw 1z: the snapshot after the discard has it in our pond.
        var after = Seat(decision with { Hand = TestTiles.Parse("123m456m4578p447s"), DrawnTile = null, Legal = LegalAction.None }, 0, "1z");
        recorder.Observe(after, T0);
        Assert.Equal(1, recorder.PendingRows);

        var rows = recorder.Finish(winnerSeat: 2, winByRon: true, ronVictimSeat: 0, ronTile: Tile.Parse("1z"));
        var row = Assert.Single(rows);
        Assert.Equal(2, row.Seat);
        Assert.True(row.Riichi);
        Assert.Equal(Tile.Parse("1z"), row.Tile);
        Assert.True(row.DealtIn);
        Assert.Equal(1, row.Turn);
        Assert.Equal("human", row.Population);
        Assert.InRange(row.Predicted, 0, 1);
        Assert.Equal(0, recorder.PendingRows);
    }

    [Fact]
    public void Recorder_labels_passed_discards_as_safe_and_skips_quiet_seats()
    {
        var recorder = new DealInRecorder();
        var decision = Seat(Snap("123m456m4578p447s1z"), 3, "4s", riichi: true, riichiIndex: 0);
        recorder.Observe(decision, T0);
        recorder.Observe(Seat(decision with { Legal = LegalAction.None }, 0, "1z"), T0);
        // Only seat 3 (riichi) is a threat; seats 1 and 2 are quiet.
        Assert.Equal(1, recorder.PendingRows);
        var rows = recorder.Finish(winnerSeat: -1, winByRon: false, ronVictimSeat: -1, ronTile: null);
        Assert.False(Assert.Single(rows).DealtIn);
    }

    [Fact]
    public void Recorder_csv_round_trips_the_header_shape()
    {
        var sample = new DealInSample(T0, "East", 2, 7, 1, true, 0, 1, Tile.Parse("5p"), "non-suji", DangerRank.F, 0.1234, 11, true, "human");
        var csv = DealInCalibration.ToCsv(sample);
        Assert.Equal(DealInCalibration.CsvHeader.Split(',').Length, csv.Split(',').Length);
        Assert.Contains(",5p,non-suji,F,0.1234,11,1,human", csv);
    }
}
