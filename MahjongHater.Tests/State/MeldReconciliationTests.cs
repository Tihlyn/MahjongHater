using System.Text.Json;
using MahjongHater.Core;
using MahjongHater.Core.Operate;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.State;

public class MeldReconciliationTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 14, 44, 45, DateTimeKind.Utc);
    private static AtkFrame MeldEvent(string tiles, int kind, int from, int seat = 0)
    {
        var icons = TestTiles.Parse(tiles).Select(t => StructFixture.IconOf(t, 76041)).ToArray();
        return AtkFrame.OfInts([13, seat, 0, kind, kind - 4, from, 255, icons.Length, .. icons]);
    }
    private static void AddMeld(EventTracker tracker, int ordinal, string tiles, int kind, int from, int seat = 0)
        => tracker.OnRefresh(MeldEvent(tiles, kind, from, seat), Now, meldSlot: ordinal - 1);

    private static StateSnapshot Build(DecodedStruct decoded, EventTracker tracker)
    {
        tracker.OnTick(decoded, Now);
        return new SnapshotBuilder().Build(decoded, tracker, StructFixture.Layout, RulesetOptions.Default);
    }

    [Fact]
    public void Recorded_chi_then_closed_kan_retains_replacement_draw_and_discard_decision()
    {
        var t = new EventTracker();
        AddMeld(t, 1, "567p", 5, 3);
        AddMeld(t, 2, "4444p", 6, 0);
        var s = Build(StructFixture.Decoded("340m2345s", "9s",
            melds: [[new(255, 3), new(255, 0)], null, null, null]), t);
        Assert.Equal(2, s.OurMelds.Count);
        Assert.Equal(MeldType.Ankan, s.OurMelds[1].Type);
        Assert.False(s.OurMelds[1].IsOpen);
        Assert.Equal(8, s.Hand.Count);
        Assert.Equal(Tile.Parse("9s"), s.DrawnTile);
        Assert.Equal(GamePhase.OurTurn, s.Phase);
        Assert.True(s.Can(LegalAction.Discard));
        Assert.Empty(s.Notes);
        Assert.Equal(ActionKind.Discard, new DecisionPolicy().Choose(s, CancellationToken.None).Kind);
    }

    // Four sets, including identical chis and a red-five kan, in every order.
    public static IEnumerable<object[]> Orders()
    {
        foreach (var a in Enumerable.Range(0, 4))
        foreach (var b in Enumerable.Range(0, 4).Except([a]))
        foreach (var c in Enumerable.Range(0, 4).Except([a, b]))
            yield return [new[] { a, b, c, 6 - a - b - c }];
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public void Every_order_of_repeated_chis_and_mixed_kans_is_reconciled_once(int[] order)
    {
        var shapes = new[] { ("123m", 5, 3), ("123m", 5, 3), ("0555p", 6, 0), ("7777s", 6, 1) };
        var t = new EventTracker();
        var records = new StructFixture.MeldRecord[4];
        for (var i = 0; i < 4; i++)
        {
            var (tiles, kind, from) = shapes[order[i]];
            var frame = MeldEvent(tiles, kind, from);
            t.OnRefresh(frame, Now, meldSlot: i);
            t.OnRefresh(frame, Now, meldSlot: i); // retransmitted payload is not a fifth set
            records[i] = new(from == 1 ? 24 : 255, from);
        }
        var s = Build(StructFixture.Decoded("1z", "1z", melds: [records, null, null, null]), t);
        Assert.Equal(4, t.Melds.Count);
        Assert.Equal(4, s.OurMelds.Count);
        Assert.True(s.Us.MeldsVerified);
        Assert.True(s.Can(LegalAction.Discard));
        Assert.Equal(2, s.OurMelds.Count(m => m.IsSequence));
        Assert.Single(s.OurMelds.SelectMany(m => m.Tiles), tile => tile.IsRedFive);
        Assert.Empty(s.Notes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void One_to_four_concealed_kans_keep_the_hand_closed(int count)
    {
        var t = new EventTracker();
        var records = Enumerable.Repeat(new StructFixture.MeldRecord(255, 0), count).ToArray();
        for (var i = 0; i < count; i++)
            AddMeld(t, i + 1, new string((char)('1' + i), 4) + "z", 6, 0);
        var closed = new[] { "123m456p789s5z", "123m456p5z", "123m5z", "5z" }[count - 1];
        var s = Build(StructFixture.Decoded(closed, "5z", melds: [records, null, null, null]), t);
        Assert.Equal(count, s.OurMelds.Count);
        Assert.False(s.IsOpen);
        Assert.True(s.Can(LegalAction.Discard));
        Assert.Equal(14, s.Hand.Count + 3 * s.OurMelds.Count);
    }

    [Theory]
    [InlineData(255)]
    [InlineData(999)] // a new marker can still use a confirmed slot/type/direction
    public void Confirmed_event_resolves_a_marker_without_guessing_its_meaning(int marker)
    {
        var t = new EventTracker();
        AddMeld(t, 1, "4444p", 6, 0);
        var s = Build(StructFixture.Decoded("123m456p789s1z", "1z",
            melds: [[new(marker, 0)], null, null, null]), t);
        Assert.True(s.Us.MeldsVerified);
        Assert.True(s.Can(LegalAction.Discard));
    }

    [Fact]
    public void Missing_first_event_does_not_move_second_meld_into_its_slot()
    {
        var t = new EventTracker();
        AddMeld(t, 2, "4444p", 6, 0);
        var d = StructFixture.Decoded("340m2345s", "9s",
            melds: [[new(255, 3), new(255, 0)], null, null, null]);
        var s = Build(d, t);
        Assert.Equal(2, s.Us.MeldCount);
        Assert.Equal(MeldType.Ankan, Assert.Single(s.OurMelds).Type);
        Assert.False(s.Us.MeldsVerified);
        Assert.False(s.LayoutHealthy);
        Assert.Equal(GamePhase.OurTurn, s.Phase);
        Assert.Equal(Tile.Parse("9s"), s.DrawnTile);
        Assert.False(s.Can(LegalAction.Discard));
        Assert.Equal(ActionKind.None, new DecisionPolicy().Choose(s, CancellationToken.None).Kind);
        AddMeld(t, 1, "567p", 5, 3);
        var restored = Build(d, t);
        Assert.True(restored.Us.MeldsVerified);
        Assert.True(restored.Can(LegalAction.Discard));
    }

    [Fact]
    public void Empty_struct_slot_is_not_compacted_and_cannot_steal_a_later_event()
    {
        var bytes = StructFixture.BytesFor("340m2345s", "9s",
            melds: [[new(-1, 0), new(12, 1)], null, null, null]);
        var d = StructFrame.FromBytes(bytes, StructFixture.Layout, 6, 0, 50).Decode(StructFixture.Layout);
        Assert.Equal(2, d.Us.Melds.Count);
        Assert.True(d.Us.Melds[0].IsEmpty);
        var t = new EventTracker();
        AddMeld(t, 2, "444p", 4, 1);
        var s = Build(d, t);
        Assert.Single(s.OurMelds);
        Assert.False(s.Us.MeldsVerified);
        Assert.Contains(s.Notes, n => n.Contains("meld 0: unresolved"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Added_kan_upgrades_original_slot_including_late_pon_and_red_five(bool latePon)
    {
        var t = new EventTracker();
        var pon = MeldEvent("055p", 4, 1);
        if (!latePon) t.OnRefresh(pon, Now, meldSlot: 0);
        var upgrade = AtkFrame.OfInts(14, 0, 13, 76054);
        t.OnRefresh(upgrade, Now);
        t.OnRefresh(pon, Now, meldSlot: 0);
        t.OnRefresh(upgrade, Now);
        t.OnRefresh(pon, Now, meldSlot: 0);
        var s = Build(StructFixture.Decoded("123m456p789s1z", "1z",
            melds: [[new(13, 1)], null, null, null]), t);
        var kan = Assert.Single(s.OurMelds);
        Assert.Equal(MeldType.Shouminkan, kan.Type);
        Assert.Equal(4, kan.Tiles.Length);
        Assert.Single(kan.Tiles, tile => tile.IsRedFive);
        Assert.True(s.Can(LegalAction.Discard));
        t.Reset();
        t.OnRefresh(pon, Now, meldSlot: 0);
        Assert.Equal(MeldType.Pon, Assert.Single(t.Melds).Type);
    }

    [Fact]
    public void Truncated_malformed_and_unknown_meld_events_do_not_invent_sets()
    {
        var t = new EventTracker();
        t.OnRefresh(AtkFrame.OfInts(13, 0, 0, 6, 1, 0, 255, 4, 76053, 76053, 76053), Now);
        AddMeld(t, 1, "124m", 5, 3);
        AddMeld(t, 1, "4444p", 123, 0);
        t.OnRefresh(AtkFrame.OfInts(14, 0, 13, 76053), Now); // index/icon disagree
        Assert.Empty(t.Melds);
    }

    [Fact]
    public void Missed_kan_event_can_be_recovered_from_hand_delta()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("123m444p456s1112z", "4p"), Now);
        var s = Build(StructFixture.Decoded("123m456s1112z", "2z",
            melds: [[new(255, 0)], null, null, null]), t);
        Assert.Equal(MeldType.Ankan, Assert.Single(s.OurMelds).Type);
        Assert.True(s.Us.MeldsVerified);
        Assert.True(s.Can(LegalAction.Discard));
    }

    [Fact]
    public void Incomplete_opponent_melds_have_distinct_analysis_identity()
    {
        var complete = StateSnapshot.Empty;
        var seats = complete.Seats.ToArray();
        seats[2] = seats[2] with { MeldCount = 1, MeldsVerified = false };
        Assert.NotEqual(AnalysisService.ComputeFingerprint(complete),
            AnalysisService.ComputeFingerprint(complete with { Seats = seats }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Opponent_concealed_kans_and_round_reset_use_the_same_reconciliation(int seat)
    {
        var t = new EventTracker();
        AddMeld(t, 1, "4444p", 6, 0, seat);
        var records = new StructFixture.MeldRecord[]?[4];
        records[seat] = [new(255, 0)];
        var s = Build(StructFixture.Decoded("123m456p789s1122z", null, melds: records), t);
        Assert.True(s.Seats[seat].MeldsVerified);
        Assert.Equal(MeldType.Ankan, Assert.Single(s.Seats[seat].Melds).Type);
        var next = Build(StructFixture.Decoded("123m456p789s1122z", "3z"), t);
        Assert.All(next.Seats, panel => Assert.Empty(panel.Melds));
        Assert.Empty(t.SeatMeldsOf(seat));
    }

    [Fact]
    public void Legacy_layout_marker_key_remains_compatible()
    {
        var legacy = EmjLayout.ReadEmbedded().Replace("tileIndexUnknown", "tileIndexChi");
        Assert.Equal(255, EmjLayout.Parse(legacy).MeldTileIndexUnknown);
    }

    [Fact]
    public void Unresolved_melds_do_not_suppress_a_confirmed_win()
    {
        var t = new EventTracker();
        var d = StructFixture.Decoded("340m2345s", null, stateCode: 15,
            melds: [[new(255, 3), new(255, 0)], null, null, null]);
        t.OnTick(d, Now);
        t.OnRefresh(AtkFrame.OfInts(8, 2, 76063), Now);
        t.OnRefresh(AtkFrame.OfInts(23, 2, 2, 0).WithString(6, "Ron!").WithString(7, "Ron").WithString(8, "Pass"), Now);
        var s = new SnapshotBuilder().Build(d, t, StructFixture.Layout, RulesetOptions.Default);
        Assert.False(s.Us.MeldsVerified);
        Assert.True(s.Can(LegalAction.Ron));
        Assert.Equal(ActionKind.Ron, new DecisionPolicy().Choose(s, CancellationToken.None).Kind);
    }

    [Fact]
    public void Diagnostic_watch_survives_state_activity_and_reports_once_until_recovered()
    {
        var watch = new IncompleteMeldWatch();
        var d = StructFixture.Decoded("123m456p789s1z", "1z", melds: [[new(255, 0)], null, null, null]);
        var s = Build(d, new EventTracker());
        Assert.False(watch.Observe(s, Now));
        Assert.False(watch.Observe(s with { Sequence = 10 }, Now.AddSeconds(4)));
        Assert.True(watch.Observe(s with { Sequence = 20 }, Now.AddSeconds(6)));
        Assert.False(watch.Observe(s with { Sequence = 30 }, Now.AddSeconds(60)));
        Assert.False(watch.Observe(StateSnapshot.Empty, Now.AddSeconds(61)));
        Assert.False(watch.Observe(s, Now.AddSeconds(62)));
        Assert.True(watch.Observe(s, Now.AddSeconds(68)));
    }

    [Fact]
    public void Captured_normal_pons_and_repeated_chis_continue_to_discard()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(StructFixture.FixturesDirectory,
            "emj_normal_calls_20260923.json")));
        var snapshots = json.RootElement.GetProperty("snapshots").EnumerateArray().ToArray();
        Assert.Equal(4, snapshots.Length);
        foreach (var frame in snapshots)
        {
            var t = new EventTracker();
            var slot = 0;
            foreach (var payload in frame.GetProperty("events").EnumerateArray())
                t.OnRefresh(AtkFrame.OfInts(payload.EnumerateArray().Select(x => x.GetInt32()).ToArray()), Now,
                    meldSlot: slot++);
            var closed = frame.GetProperty("closedIcons").EnumerateArray().Select(x => x.GetInt32()).Where(x => x != 0)
                .Select(icon => { Assert.True(TileHelpers.TryTileFromIconId(icon, out var tile)); return tile; }).ToArray();
            var records = frame.GetProperty("records").EnumerateArray().Select(x =>
                new StructFixture.MeldRecord(x.GetProperty("index").GetInt32(), x.GetProperty("from").GetInt32())).ToArray();
            var d = StructFixture.Decoded(string.Join("", closed), null,
                stateCode: frame.GetProperty("state").GetInt32(), melds: [records, null, null, null]);
            var s = Build(d, t);
            Assert.Equal(frame.GetProperty("meldCount").GetInt32(), s.OurMelds.Count);
            Assert.Equal(frame.GetProperty("closedCount").GetInt32(), s.Hand.Count);
            Assert.True(s.Us.MeldsVerified);
            Assert.Equal(GamePhase.OurTurn, s.Phase);
            Assert.True(s.Can(LegalAction.Discard));
            Assert.Empty(s.Notes);
            Assert.Equal(ActionKind.Discard, new DecisionPolicy().Choose(s, CancellationToken.None).Kind);
            Assert.True(AutoPlayer.RecoveryAllowed(s));
        }
    }

    [Theory]
    [InlineData(LegalAction.Discard, true)]
    [InlineData(LegalAction.Pass, true)]
    [InlineData(LegalAction.None, true)]
    [InlineData(LegalAction.Ron | LegalAction.Pass, false)]
    [InlineData(LegalAction.Tsumo | LegalAction.Pass, false)]
    public void Recovery_keeps_normal_cases_but_cannot_pass_a_win_or_act_on_incomplete_melds(LegalAction legal, bool allowed)
    {
        var s = StateSnapshot.Empty with { Phase = GamePhase.OurTurn, Legal = legal };
        Assert.Equal(allowed, AutoPlayer.RecoveryAllowed(s));
        Assert.False(AutoPlayer.RecoveryAllowed(s with { AwaitingOurWin = true }));
        var seats = s.Seats.ToArray();
        seats[0] = seats[0] with { MeldCount = 1, MeldsVerified = false };
        Assert.False(AutoPlayer.RecoveryAllowed(s with { Seats = seats }));
    }
}
