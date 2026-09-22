using MahjongHater.Core;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.State;

public class SnapshotBuilderTests
{
    private static readonly DateTime T0 = new(2026, 9, 18, 20, 0, 0, DateTimeKind.Utc);
    private static readonly EmjLayout Layout = StructFixture.Layout;

    private static StateSnapshot Build(SnapshotBuilder b, EventTracker t, DecodedStruct d, IReadOnlyList<string>? labels = null)
    {
        t.OnTick(d, labels ?? [], T0);
        return b.Build(d, t, Layout, RulesetOptions.Default);
    }

    [Fact]
    public void Our_turn_with_fourteen_tiles_is_discardable()
    {
        var s = Build(new SnapshotBuilder(), new EventTracker(), StructFixture.Decoded("123m456p789s1122z", "3z"));
        Assert.Equal(GamePhase.OurTurn, s.Phase);
        Assert.Equal(14, s.Hand.Count);
        Assert.Equal(Tile.Parse("3z"), s.DrawnTile);
        Assert.True(s.Can(LegalAction.Discard));
        Assert.Equal(Tile.Parse("3z"), s.Hand[^1]);
        Assert.Equal(1, s.Sequence);
        Assert.True(s.LayoutHealthy);
        Assert.Equal(25000, s.Us.Score);
        Assert.Equal(Tile.Parse("5p"), s.DoraIndicators[0]);
    }

    // A hand only ever totals 13 or 14 with every meld counted as three. When it does not,
    // every decision built on it is guesswork - and the plugin used to notice only deep
    // inside the win evaluation, where it answered a Ron window with Pass
    // (docs/research/WIN_OFFERS_2026_09_22.md).
    [Fact]
    public void A_hand_that_does_not_add_up_is_reported_as_a_health_note()
    {
        // One meld tracked, but the closed read still holds a full 13 tiles: 13 + 3 = 16.
        var s = Build(new SnapshotBuilder(), new EventTracker(),
            StructFixture.Decoded("123m456p789s1122z", null, stateCode: 15,
                melds: [[new StructFixture.MeldRecord(28, 1)], null, null, null]));
        Assert.Contains(s.Notes, n => n.Contains("does not add up"));
    }

    [Fact]
    public void A_consistent_hand_has_no_arithmetic_note()
    {
        var s = Build(new SnapshotBuilder(), new EventTracker(), StructFixture.Decoded("123m456p789s1122z", "3z"));
        Assert.DoesNotContain(s.Notes, n => n.Contains("does not add up"));
    }

    // The window worked backwards: what the game offers on a tile must match what our closed
    // hand allows, because Chi/Pon/Kan are pure tile counting. A difference is an assertion
    // that the hand read is wrong, and it rides out on the snapshot's health notes.
    [Fact]
    public void An_offer_our_hand_cannot_support_is_reported()
    {
        var t = new EventTracker();
        var d = StructFixture.Decoded("123m456p789s1122z", null, stateCode: 15);
        t.OnTick(d, [], T0);
        t.OnRefresh(AtkFrame.OfInts(8, 3, StructFixture.IconOf(Tile.Parse("7z"), 76041)), T0);
        t.OnRefresh(AtkFrame.OfInts([23, 2, 5, 0, .. new int[18]]).WithString(6, "Pon!").WithString(7, "Pon").WithString(8, "Pass"), T0);

        var s = new SnapshotBuilder().Build(d, t, Layout, RulesetOptions.Default);
        Assert.True(s.CallWindowConfirmed);
        Assert.Contains(s.Notes, n => n.Contains("offers Pon") && n.Contains("missing tiles"));
    }

    // Provenance: only a prompt EVENT makes the offered actions authoritative. A window the
    // panel text alone suggested is a guess, and the policy keeps its own gate for it.
    [Fact]
    public void Call_window_provenance_reaches_the_snapshot()
    {
        var t = new EventTracker();
        var d = StructFixture.Decoded("44m77m3p55p556s6699s", "3p");
        var labelOnly = Build(new SnapshotBuilder(), t, d, ["Tsumo", "Pass"]);
        Assert.False(labelOnly.CallWindowConfirmed);

        var t2 = new EventTracker();
        t2.OnTick(d, [], T0);
        t2.OnRefresh(AtkFrame.OfInts([23, 2, 1, 0, .. new int[18]]).WithString(6, "Tsumo!").WithString(7, "Tsumo").WithString(8, "Pass"), T0);
        var confirmed = t2.CallWindowActive ? new SnapshotBuilder().Build(d, t2, Layout, RulesetOptions.Default) : null;
        Assert.NotNull(confirmed);
        Assert.True(confirmed!.CallWindowConfirmed);
    }

    [Fact]
    public void Thirteen_tiles_without_a_draw_is_the_others_turn()
    {
        var s = Build(new SnapshotBuilder(), new EventTracker(), StructFixture.Decoded("123m456p789s1122z", null, stateCode: 15));
        Assert.Equal(GamePhase.OthersTurn, s.Phase);
        Assert.False(s.Can(LegalAction.Discard));
        Assert.Null(s.DrawnTile);
    }

    [Fact]
    public void Sequence_bumps_only_when_content_changes()
    {
        var b = new SnapshotBuilder();
        var t = new EventTracker();
        var d = StructFixture.Decoded("123m456p789s1122z", "3z");
        var s1 = Build(b, t, d);
        var s2 = Build(b, t, d);
        Assert.Same(s1, s2);
        var s3 = Build(b, t, StructFixture.Decoded("123m456p789s1122z", "4z"));
        Assert.Equal(s1.Sequence + 1, s3.Sequence);
    }

    [Fact]
    public void Claim_prompt_exposes_legal_calls_and_the_offered_tile()
    {
        var b = new SnapshotBuilder();
        var t = new EventTracker();
        var hand = StructFixture.Decoded("22z34567m11p3459s", null, stateCode: 15);
        Build(b, t, hand);
        t.OnRefresh(AtkFrame.OfInts(8, 3, 76069), T0); // kamicha discards S
        var s = Build(b, t, hand, ["Pon", "Pass"]);
        Assert.Equal(GamePhase.CallPrompt, s.Phase);
        Assert.True(s.Can(LegalAction.Pon));
        Assert.True(s.Can(LegalAction.Pass));
        Assert.False(s.Can(LegalAction.Discard));
        Assert.Equal(Tile.Parse("2z"), s.CallTile);
        Assert.Equal(3, s.CallFromSeat);
        Assert.Equal(13, s.Hand.Count);
    }

    [Fact]
    public void Post_call_echo_in_the_draw_slot_is_excluded_and_discard_is_legal()
    {
        var b = new SnapshotBuilder();
        var t = new EventTracker();
        Build(b, t, StructFixture.Decoded("22z34567m11p3459s", null, stateCode: 15));
        t.OnRefresh(AtkFrame.OfInts(8, 1, 76069), T0);
        var s = Build(b, t, StructFixture.PostPon);
        Assert.Single(s.OurMelds);
        Assert.Equal(11, s.Hand.Count);
        Assert.Null(s.DrawnTile);
        Assert.Equal(GamePhase.OurTurn, s.Phase);
        Assert.True(s.Can(LegalAction.Discard));
        Assert.True(s.IsOpen);
    }

    [Fact]
    public void Self_declare_prompt_keeps_the_draw_and_maps_riichi()
    {
        var s = Build(new SnapshotBuilder(), new EventTracker(), StructFixture.Decoded("123m456p789s1122z", "3z"), ["Riichi", "Pass"]);
        Assert.Equal(GamePhase.SelfDeclare, s.Phase);
        Assert.Equal(14, s.Hand.Count);
        Assert.True(s.Can(LegalAction.Riichi));
        Assert.True(s.Can(LegalAction.Discard));
        Assert.Null(s.CallTile);
    }

    [Fact]
    public void Unhealthy_frame_falls_back_to_the_last_good_hand()
    {
        var b = new SnapshotBuilder();
        var t = new EventTracker();
        var good = Build(b, t, StructFixture.Decoded("123m456p789s1122z", "3z"));

        var buf = StructFixture.BytesFor("123m456p789s1122z", "3z");
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(Layout.HandArray, 4), 4242);
        var bad = StructFrame.FromBytes(buf, Layout, 6, 0, 50).Decode(Layout);
        var s = Build(b, t, bad);
        Assert.False(s.LayoutHealthy);
        Assert.Equal(good.Hand, s.Hand);
    }

    // An answered Tsumo/Ron parks the snapshot at RoundEnd (nothing legal) until the win
    // screen, so the auto player never discards from a hand the game is scoring.
    [Fact]
    public void Answered_win_parks_the_snapshot_at_round_end()
    {
        var t = new EventTracker();
        var b = new SnapshotBuilder();
        var d = StructFixture.Decoded("44m77m3p55p556s6699s", "3p");
        t.OnTick(d, [], T0);
        t.OnRefresh(AtkFrame.OfInts([23, .. new int[21]]).WithString(6, "Tsumo!").WithString(7, "Tsumo").WithString(8, "Riichi"), T0);
        var before = Build(b, t, d);
        Assert.Equal(GamePhase.SelfDeclare, before.Phase);
        Assert.True(before.Can(LegalAction.Tsumo));

        t.MarkCallAnswered(isWin: true, t.CallWindowGeneration);
        var after = Build(b, t, d);
        Assert.Equal(GamePhase.RoundEnd, after.Phase);
        Assert.Equal(LegalAction.None, after.Legal);
    }

    [Fact]
    public void Round_end_state_codes_map_to_round_end()
    {
        var s = Build(new SnapshotBuilder(), new EventTracker(), StructFixture.Decoded("123m456p789s1122z", "3z", stateCode: 32));
        Assert.Equal(GamePhase.RoundEnd, s.Phase);
        Assert.False(s.Can(LegalAction.Discard));
    }

    [Fact]
    public void Seen_tiles_come_from_every_seat_but_our_own_melds_are_excluded()
    {
        var b = new SnapshotBuilder();
        var t = new EventTracker();
        t.OnRefresh(AtkFrame.OfInts(8, 0, 76041), T0);
        t.OnRefresh(AtkFrame.OfInts(8, 2, 76050), T0);
        var s = Build(b, t, StructFixture.Decoded("123m456p789s1122z", "3z"));
        Assert.Equal(TestTiles.Parse("1m1p"), s.SeenForAnalyzer().ToList());
        Assert.Single(s.Seats[0].Discards);
        Assert.Single(s.Seats[2].Discards);
    }

    [Fact]
    public void Not_in_game_is_published_once()
    {
        var b = new SnapshotBuilder();
        var first = b.BuildNotInGame();
        var second = b.BuildNotInGame();
        Assert.Same(first, second);
        Assert.Equal(GamePhase.NotInGame, first.Phase);
    }

    [Fact]
    public void Chi_shape_chooser_is_a_claim_prompt_with_the_offered_shapes()
    {
        var b = new SnapshotBuilder();
        var t = new EventTracker();
        var hand = StructFixture.Decoded("233m2345p0p23456s", null);
        Build(b, t, hand);
        t.OnRefresh(AtkFrame.OfInts(8, 3, StructFixture.IconOf(Tile.Parse("4s"), 76041)), default);
        int I(string tile) => StructFixture.IconOf(Tile.Parse(tile), 76041);
        t.OnRefresh(AtkFrame.OfInts(25, 6, 0, 2,
            I("2s"), I("3s"), I("4s"), 76041,
            I("4s"), I("5s"), I("6s"), 76041).WithString(2, "Chi"), default);
        var s = Build(b, t, hand with { StateCode = 25 });
        Assert.Equal(GamePhase.CallPrompt, s.Phase);
        Assert.True(s.Can(LegalAction.Chi));
        Assert.True(s.Can(LegalAction.Pass));
        Assert.Equal(Tile.Parse("4s"), s.CallTile);
        Assert.Equal(3, s.CallFromSeat);
        Assert.Equal(2, s.CallShapes.Count);
        Assert.Equal(TestTiles.Parse("4s5s6s"), s.CallShapes[1].Tiles);
    }
}
