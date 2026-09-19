using MahjongHater.Core;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.State;

public class EventTrackerTests
{
    private static readonly DateTime T0 = new(2026, 9, 18, 20, 0, 0, DateTimeKind.Utc);

    private static AtkFrame Discard(int seat, string tile)
        => AtkFrame.OfInts(8, seat, StructFixture.IconOf(Tile.Parse(tile), 76041));

    // Type-19 frame: [6]=Chi slot, [7]=Pon slot, [8]=Ron slot ("Pass" = unavailable).
    private static AtkFrame CallWindow(string chi, string pon, string ron)
        => AtkFrame.OfInts([19, .. new int[21]]).WithString(6, chi).WithString(7, pon).WithString(8, ron);

    [Fact]
    public void Type8_books_discards_per_seat()
    {
        var t = new EventTracker();
        t.OnRefresh(Discard(0, "1m"), T0);
        t.OnRefresh(Discard(2, "9p"), T0);
        t.OnRefresh(Discard(2, "7z"), T0);
        Assert.Equal(TestTiles.Parse("1m"), t.SeatDiscardsOf(0));
        Assert.Equal(TestTiles.Parse("9p7z"), t.SeatDiscardsOf(2));
        Assert.Empty(t.SeatDiscardsOf(1));
        Assert.Equal(Tile.Parse("7z"), t.LastOpponentDiscard);
    }

    [Fact]
    public void Type5_tracks_the_wall_and_ends_a_call_window()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("22z34567m11p3459s", null), [], T0);
        t.OnRefresh(Discard(1, "2z"), T0);
        t.OnRefresh(CallWindow("Pass", "Pon", "Pass"), T0);
        Assert.True(t.CallWindowActive);
        Assert.Equal(["Pon"], t.CallOptions);
        Assert.Equal(Tile.Parse("2z"), t.CallTile);
        Assert.Equal(1, t.CallFromSeat);

        t.OnRefresh(AtkFrame.OfInts(5, 42, 2, 76041), T0);
        Assert.False(t.CallWindowActive);
        Assert.Equal(42, t.EventWallRemaining);
    }

    [Fact]
    public void Call_window_for_an_uncallable_tile_is_not_ours()
    {
        var t = new EventTracker();
        // No souzu at all: an 8s can be neither pon'd, chi'd nor ron'd.
        t.OnTick(StructFixture.Decoded("333m456m89m567p7p7z", null), [], T0);
        t.OnRefresh(Discard(2, "8s"), T0);
        t.OnRefresh(CallWindow("Chi", "Pass", "Pass"), T0);
        Assert.False(t.CallWindowActive);
    }

    [Fact]
    public void Chi_window_from_a_non_kamicha_seat_is_not_ours()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("45m111p222p333s7z9s", null), [], T0);
        t.OnRefresh(Discard(1, "3m"), T0); // shimocha: chi impossible
        t.OnRefresh(CallWindow("Chi", "Pass", "Pass"), T0);
        Assert.False(t.CallWindowActive);

        t.OnRefresh(Discard(3, "3m"), T0); // kamicha: chi legal
        t.OnRefresh(CallWindow("Chi", "Pass", "Pass"), T0);
        Assert.True(t.CallWindowActive);
        Assert.True(t.CallIsClaim);
    }

    [Fact]
    public void Own_turn_prompt_is_a_self_declare_not_a_claim()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("123m456p789s1122z", "3z"), ["Riichi", "Pass"], T0);
        Assert.True(t.CallWindowActive);
        Assert.False(t.CallIsClaim);
        Assert.Null(t.CallTile);
        Assert.Equal(["Riichi"], t.CallOptions);
    }

    [Fact]
    public void Label_edges_open_and_label_disappearance_closes()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("22z34567m11p3459s", null);
        t.OnTick(hand, [], T0);
        t.OnRefresh(Discard(3, "2z"), T0);
        t.OnTick(hand, ["Pon", "Pass", "Time remaining: 9"], T0);
        Assert.True(t.CallWindowActive);
        Assert.Equal(Tile.Parse("2z"), t.CallTile);

        t.OnTick(hand, ["Pon", "Pass"], T0);   // same labels: no re-trigger, still active
        Assert.True(t.CallWindowActive);
        t.OnTick(hand, [], T0);
        Assert.False(t.CallWindowActive);
    }

    [Fact]
    public void Stuck_labels_reactivate_on_the_next_opponent_discard()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("22z34567m11p3459s", null);
        t.OnTick(hand, [], T0);
        t.OnRefresh(Discard(3, "2z"), T0);
        t.OnTick(hand, ["Pon", "Pass"], T0);
        t.OnRefresh(AtkFrame.OfInts(5, 40, 0, 76041), T0);   // window resolved by play continuing
        Assert.False(t.CallWindowActive);
        t.OnTick(hand, ["Pon", "Pass"], T0);                  // stale panel: no edge
        Assert.False(t.CallWindowActive);

        t.OnRefresh(Discard(1, "2z"), T0);                    // a fresh discard we can pon
        t.OnTick(hand, ["Pon", "Pass"], T0);
        Assert.True(t.CallWindowActive);
    }

    [Fact]
    public void Hand_delta_after_a_pon_reconstructs_the_meld()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("22z34567m11p3459s", null), [], T0);
        t.OnRefresh(Discard(1, "2z"), T0);
        t.OnRefresh(CallWindow("Pass", "Pon", "Pass"), T0);
        // Post-call: 11 closed, claimed tile parked in slot 13.
        t.OnTick(StructFixture.Decoded("34567m11p3459s", "2z"), [], T0);
        var meld = Assert.Single(t.Melds);
        Assert.Equal(MeldType.Pon, meld.Type);
        Assert.False(t.CallWindowActive);
    }

    [Fact]
    public void AtkType74_payload_is_deduped_and_hand_delta_does_not_double_add()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("22z34567m11p3459s", null), [], T0);
        t.OnRefresh(Discard(1, "2z"), T0);
        var payload = AtkFrame.OfInts([74, 0, 0, 0, 0, 0, 0, 0, 76069, 76069, 76069, 0]);
        t.OnReceiveEvent(74, payload);
        t.OnReceiveEvent(74, payload);
        Assert.Single(t.Melds);
        t.OnTick(StructFixture.Decoded("34567m11p3459s", "2z"), [], T0);
        Assert.Single(t.Melds);
    }

    [Fact]
    public void Stale_melds_clear_when_the_struct_shows_a_full_closed_hand()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("22z34567m11p3459s", null), [], T0);
        t.OnRefresh(Discard(1, "2z"), T0);
        t.OnTick(StructFixture.Decoded("34567m11p3459s", "2z"), [], T0);
        Assert.Single(t.Melds);
        t.OnTick(StructFixture.Decoded("123m456p789s1122z", null), [], T0); // next deal, 13 closed
        Assert.Empty(t.Melds);
    }

    [Fact]
    public void Discard_counts_dropping_to_zero_resets_the_round()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("22z34567m11p3459s", null, [3, 3, 2, 2]), [], T0);
        t.OnRefresh(Discard(1, "9p"), T0);
        Assert.NotEmpty(t.SeatDiscardsOf(1));
        t.OnTick(StructFixture.Decoded("123m456p789s1122z", null, [0, 0, 0, 0]), [], T0);
        Assert.Empty(t.SeatDiscardsOf(1));
        Assert.Equal(70, t.EventWallRemaining);
    }

    [Fact]
    public void Type21_after_a_round_end_resets_but_mid_round_does_not()
    {
        var t = new EventTracker();
        t.OnRefresh(Discard(1, "9p"), T0);
        t.OnRefresh(AtkFrame.OfInts([21, 13, 0, .. new int[19]]), T0);  // mid-round refresh
        Assert.NotEmpty(t.SeatDiscardsOf(1));

        t.OnRefresh(AtkFrame.OfInts([29, 12, 0, 0, 0]), T0);             // score screen: +1200
        Assert.Equal(1, t.WinsThisSession);
        t.OnRefresh(AtkFrame.OfInts([21, 13, 0, .. new int[19]]), T0);  // genuine deal
        Assert.Empty(t.SeatDiscardsOf(1));
    }

    [Fact]
    public void Round_wind_and_seat_wind_come_from_events()
    {
        var t = new EventTracker();
        t.OnRefresh(AtkFrame.OfInts([32, 0, 0]).WithString(2, "South 2 West Wind"), T0);
        Assert.Equal(Wind.South, t.RoundWind);
        t.OnRefresh(AtkFrame.OfInts([15, 0, 76070]), T0);
        Assert.Equal(Wind.West, t.SeatWind);
        t.OnRefresh(AtkFrame.OfInts([5, 50, 1, 76068]), T0);  // type-5 reuses [2]: ignored
        Assert.Equal(Wind.West, t.SeatWind);
    }
}
