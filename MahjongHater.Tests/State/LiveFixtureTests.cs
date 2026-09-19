using System.Text.Json;
using MahjongHater.Core;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.State;

// Every live dump under resources/fixtures must decode to its sidecar JSON (captured by
// the Phase 0 RE session — see docs/EMJ_STRUCT.md). A failure here means the layout JSON
// and the client's struct have drifted apart.
public class LiveFixtureTests
{
    private static readonly DateTime T0 = new(2026, 9, 18, 21, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<object[]> Fixtures() => StructFixture.FixtureNames().Select(n => new object[] { n });

    [Fact]
    public void All_eight_fixtures_are_present()
        => Assert.Equal(8, StructFixture.FixtureNames().Count());

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Struct_decodes_to_the_sidecar(string name)
    {
        var d = StructFixture.DecodeFixture(name, out var sidecar);
        var s = sidecar.GetProperty("struct");

        Assert.True(d.Healthy, $"{name}: hand slots must decode with the configured base");
        Assert.False(d.BaseShifted);
        Assert.Equal(StructFixture.SidecarTiles(s.GetProperty("hand")), d.ClosedTiles);
        var drawn = s.GetProperty("drawnTile");
        Assert.Equal(drawn.ValueKind == JsonValueKind.Null ? null : StructFixture.SidecarTile(drawn.GetString()!), d.DrawnTile);
        Assert.Equal(StructFixture.SidecarTile(s.GetProperty("doraIndicator").GetString()!), d.DoraIndicator);
        Assert.Equal(s.GetProperty("doraIndicatorCount").GetInt32(), d.DoraIndicatorCount);

        var seats = s.GetProperty("seats").EnumerateArray().ToList();
        for (var i = 0; i < 4; i++)
        {
            var e = seats[i];
            var p = d.Seats[i];
            Assert.Equal(e.GetProperty("closedTileCount").GetInt32(), p.ClosedTileCount);
            Assert.Equal(e.GetProperty("meldCount").GetInt32(), p.MeldCount);
            Assert.Equal(e.GetProperty("discardCount").GetInt32(), p.DiscardCount);
            var ri = e.GetProperty("riichiDiscardIndex");
            Assert.Equal(ri.ValueKind == JsonValueKind.Null ? null : ri.GetInt32(), p.RiichiDiscardIndex);
            Assert.Equal(e.GetProperty("score").GetInt32(), p.Score);
            Assert.Equal(e.GetProperty("pointDifference").GetInt32(), p.PointDifference);

            var melds = e.GetProperty("melds").EnumerateArray().ToList();
            Assert.Equal(melds.Count, p.Melds.Count);
            for (var k = 0; k < melds.Count; k++)
            {
                Assert.Equal(melds[k].GetProperty("tileIndex").GetInt32(), p.Melds[k].TileIndex);
                Assert.Equal(melds[k].GetProperty("fromDirection").GetInt32(), p.Melds[k].FromDirection);
                Assert.Equal(StructFixture.SidecarTile(melds[k].GetProperty("tile").GetString()!), p.Melds[k].Tile);
            }
        }
    }

    [Fact]
    public void Turn1_is_our_discard_turn_with_the_draw_in_slot_13()
    {
        var d = StructFixture.DecodeFixture("emj_struct_round1_turn1", out _);
        var t = new EventTracker();
        t.OnTick(d, [], T0);
        var s = new SnapshotBuilder().Build(d, t, StructFixture.Layout, RulesetOptions.Default);
        Assert.Equal(GamePhase.OurTurn, s.Phase);
        Assert.Equal(14, s.Hand.Count);
        Assert.Equal(StructFixture.SidecarTile("Wh"), s.DrawnTile);
        Assert.True(s.Can(LegalAction.Discard));
        Assert.Equal(13, EmjStateReader.FindVisualIndex(s.Hand, StructFixture.SidecarTile("Wh")));
    }

    [Fact]
    public void Pon_prompt_fixture_yields_a_claim_with_the_offered_tile_and_seat1_riichi()
    {
        var d = StructFixture.DecodeFixture("emj_struct_round1_call_prompt_pon", out var sidecar);
        var t = new EventTracker();
        var b = new SnapshotBuilder();
        t.OnTick(d, [], T0);
        t.OnRefresh(AtkFrame.OfInts(8, 3, StructFixture.IconOf(Tile.Parse("5s"), 76041)), T0); // kamicha discards 5s
        t.OnTick(d, sidecar.GetProperty("callOptions").EnumerateArray().Select(e => e.GetString()!).ToList(), T0);
        var s = b.Build(d, t, StructFixture.Layout, RulesetOptions.Default);

        Assert.Equal(GamePhase.CallPrompt, s.Phase);
        Assert.Equal(Tile.Parse("5s"), s.CallTile);
        Assert.Equal(3, s.CallFromSeat);
        Assert.True(s.Can(LegalAction.Pon));
        Assert.True(s.Can(LegalAction.Pass));
        Assert.False(s.Can(LegalAction.Discard));
        Assert.Equal(13, s.Hand.Count);
        Assert.True(s.Seats[1].Riichi);
        Assert.Equal(7, s.Seats[1].RiichiDiscardIndex);
        Assert.False(s.OurRiichi);
        // Only one type-8 was replayed: every seat's discard list is short of the struct count.
        Assert.All(s.Seats, x => Assert.False(x.DiscardsVerified));
        Assert.Equal(9, s.Seats[0].DiscardCount);
    }

    [Fact]
    public void Melds_fixture_builds_toimens_pons_from_the_struct_and_reconciles_events()
    {
        var d = StructFixture.DecodeFixture("emj_struct_round6_melds", out var sidecar);
        var t = new EventTracker();
        // Replay the type-13 payloads the sidecar recorded for seat 2.
        foreach (var m in sidecar.GetProperty("meldsFromEvents").EnumerateArray())
        {
            var tiles = StructFixture.SidecarTiles(m.GetProperty("tiles"));
            var icons = tiles.Select(x => StructFixture.IconOf(x, 76041)).ToArray();
            t.OnRefresh(AtkFrame.OfInts([13, m.GetProperty("seat").GetInt32(), 0, 4, 0, m.GetProperty("fromDirection").GetInt32(), TileHelpers.ToIndex(tiles[0]), 3, .. icons]), T0);
        }

        foreach (var (seat, list) in sidecar.GetProperty("discards").EnumerateObject().Select(p => (int.Parse(p.Name), StructFixture.SidecarTiles(p.Value))))
            foreach (var tile in list)
                t.OnRefresh(AtkFrame.OfInts(8, seat, StructFixture.IconOf(tile, 76041)), T0);

        t.OnTick(d, [], T0);
        var s = new SnapshotBuilder().Build(d, t, StructFixture.Layout, RulesetOptions.Default);

        Assert.Equal(GamePhase.OthersTurn, s.Phase);
        Assert.Equal(13, s.Hand.Count);
        Assert.Empty(s.OurMelds);
        var toimen = s.Seats[2];
        Assert.Equal(2, toimen.Melds.Count);
        Assert.Equal(MeldType.Pon, toimen.Melds[0].Type);
        Assert.Equal(Tile.Parse("5s"), toimen.Melds[0].Tiles[0]);          // Meld normalizes red fives away
        Assert.Equal(Tile.Parse("8m"), TileHelpers.Normalize(toimen.Melds[1].Tiles[0]));
        Assert.Equal(38500, toimen.Score);
        Assert.All(s.Seats, x => Assert.True(x.DiscardsVerified));
        Assert.Equal(13, toimen.Discards.Count);
        Assert.Contains(Tile.Parse("5s"), s.SeenForAnalyzer());               // opponent meld tiles are visible
        Assert.Equal(70 - 47, s.WallRemaining);                                 // derived from the struct counts
        Assert.Empty(s.Notes);
    }

    [Fact]
    public void Melds_fixture_without_events_still_builds_pons_from_the_struct()
    {
        var d = StructFixture.DecodeFixture("emj_struct_round6_melds", out _);
        var t = new EventTracker();
        t.OnTick(d, [], T0);
        var s = new SnapshotBuilder().Build(d, t, StructFixture.Layout, RulesetOptions.Default);
        var toimen = s.Seats[2];
        Assert.Equal(2, toimen.Melds.Count);
        Assert.Equal(Tile.Parse("5s"), toimen.Melds[0].Tiles[0]);
        Assert.Equal(Tile.Parse("8m"), toimen.Melds[1].Tiles[0]);
        Assert.False(toimen.DiscardsVerified);
        Assert.Contains(s.Notes, n => n.Contains("seat 2 discards"));
    }

    [Fact]
    public void Win_screen_fixture_is_round_end()
    {
        var d = StructFixture.DecodeFixture("emj_struct_round1_win_screen", out _);
        var t = new EventTracker();
        t.OnTick(d, [], T0);
        var s = new SnapshotBuilder().Build(d, t, StructFixture.Layout, RulesetOptions.Default);
        Assert.Equal(GamePhase.RoundEnd, s.Phase);
        Assert.False(s.Can(LegalAction.Discard));
        Assert.True(s.Seats[2].Riichi);
    }

    [Fact]
    public void Fresh_deal_fixture_is_dealing_and_a_following_deal_resets_the_tracker()
    {
        var mid = StructFixture.DecodeFixture("emj_struct_round6_melds", out _);
        var t = new EventTracker();
        t.OnTick(mid, [], T0);
        t.OnRefresh(AtkFrame.OfInts(8, 1, 76041), T0);
        Assert.NotEmpty(t.SeatDiscardsOf(1));

        var deal = StructFixture.DecodeFixture("emj_struct_deal_fresh", out _);
        t.OnTick(deal, [], T0);
        Assert.Empty(t.SeatDiscardsOf(1));
        var s = new SnapshotBuilder().Build(deal, t, StructFixture.Layout, RulesetOptions.Default);
        Assert.Equal(GamePhase.Dealing, s.Phase);
        Assert.Equal(13, s.Hand.Count);
        Assert.Empty(s.DoraIndicators);   // count 0: indicator not revealed yet
        Assert.Equal(70, s.WallRemaining);
    }
}
