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
    // A type-23 options frame: row count (including Pass) then the two row codes.
    private static AtkFrame CallCodes(int rowCount, int first, int second)
        => AtkFrame.OfInts([23, rowCount, first, second, .. new int[18]]);

    // A real options frame as the game sends it: codes in [2]/[3], the banner in [6] and the
    // row labels in [7]/[8] (a two-row window is the offer plus Pass).
    private static AtkFrame CallWindow(params string[] options)
    {
        var codes = options.Select(Code).ToArray();
        var frame = CallCodes(options.Length + 1, codes.ElementAtOrDefault(0), codes.ElementAtOrDefault(1))
            .WithString(6, options[0] + "!");
        for (var i = 0; i < options.Length && i < 2; i++)
            frame = frame.WithString(7 + i, options[i]);
        return options.Length < 2 ? frame.WithString(8, "Pass") : frame;
    }

    // A bare type-19 carrying the same payload with NO row count: the event the game also
    // uses for another seat's Pon!/Chi! banner. Nothing in it says a local list is on screen.
    private static AtkFrame CallBanner(params string[] options)
    {
        var codes = options.Select(Code).ToArray();
        var frame = AtkFrame.OfInts([19, 0, codes.ElementAtOrDefault(0), codes.ElementAtOrDefault(1), .. new int[18]])
            .WithString(6, options[0] + "!");
        for (var i = 0; i < options.Length && i < 2; i++)
            frame = frame.WithString(7 + i, options[i]);
        return options.Length < 2 ? frame.WithString(8, "Pass") : frame;
    }

    // The turn advancing to us (type-5, [2]=0). A self-declare offer can only follow our own
    // draw, and the label edge now requires that event just as a claim edge requires a fresh
    // opponent discard - which is why stale "Chi" text goes quiet on its own and stale
    // "Riichi" text used not to.
    private static AtkFrame OurDraw(int wall = 50) => AtkFrame.OfInts([5, wall, 0, .. new int[18]]);

    private static int Code(string option) => option switch
    {
        "Tsumo" => 1, "Ron" => 2, "Riichi" => 3, "Kan" => 4, "Pon" => 5, "Chi" => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, "not an option"),
    };

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
        t.OnRefresh(CallWindow("Pon"), T0);
        Assert.True(t.CallWindowActive);
        Assert.Equal(["Pon"], t.CallOptions);
        Assert.Equal(Tile.Parse("2z"), t.CallTile);
        Assert.Equal(1, t.CallFromSeat);

        t.OnRefresh(AtkFrame.OfInts(5, 42, 2, 76041), T0);
        Assert.False(t.CallWindowActive);
        Assert.Equal(42, t.EventWallRemaining);
    }

    // Options come from the integer lane: [2]/[3] are the row codes (1=Tsumo 2=Ron 3=Riichi
    // 4=Kan 5=Pon 6=Chi) and on a type-23 [1] is the row count including Pass. The row strings
    // are only a cross-check, because a type-19 also carries unrelated integer payloads.
    [Fact]
    public void Options_come_from_the_codes_not_the_button_text()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("123m456p789s11z22z", null), [], T0);
        t.OnRefresh(Discard(1, "2z"), T0);
        // [1]=3 rows (Ron, Pon, Pass), codes Ron + Pon, with the banner in [6].
        t.OnRefresh(CallCodes(3, 2, 5).WithString(6, "Ron!").WithString(7, "Ron").WithString(8, "Pon"), T0);
        Assert.True(t.CallWindowActive);
        Assert.Equal(["Ron", "Pon"], t.CallOptions);
        Assert.True(t.CallIsClaim);
    }

    [Fact]
    public void An_integer_payload_that_is_not_an_option_frame_is_ignored()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("123m456p789s11z22z", null), [], T0);
        t.OnRefresh(Discard(1, "2z"), T0);
        t.OnRefresh(CallCodes(2, 2, 0).WithString(7, "Ron").WithString(8, "Pass"), T0);
        Assert.Equal(["Ron"], t.CallOptions);

        // Live 2026-09-22: a type-19 carrying [1]=13 [2]=0 [3]=1 [4]=2 … an index ramp, whose
        // [3]=1 would otherwise read as "Tsumo offered".
        t.OnRefresh(AtkFrame.OfInts([19, 13, 0, 1, 2, 3, 4, 5, 6]), T0);
        Assert.Equal(["Ron"], t.CallOptions);
    }

    // The panel keeps its texts after a prompt closes: a window only the labels opened is a
    // guess, and every genuine one in the 2026-09-22 session had its event within 5 ms.
    [Fact]
    public void A_label_only_window_expires_unless_an_event_confirms_it()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("123m456p789s1122z", "3z");
        t.OnRefresh(OurDraw(), T0);
        t.OnTick(hand, ["Riichi", "Pass"], T0);
        Assert.True(t.CallWindowActive);
        Assert.True(t.CallWindowFromLabels);

        t.OnTick(hand, ["Riichi", "Pass"], T0.AddMilliseconds(200));   // still within the grace
        Assert.True(t.CallWindowActive);

        t.OnTick(hand, ["Riichi", "Pass"], T0.AddSeconds(1));          // no event ever arrived
        Assert.False(t.CallWindowActive);
    }

    [Fact]
    public void An_event_confirms_a_label_window_and_it_stays()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("123m456p789s1122z", "3z");
        t.OnTick(hand, ["Riichi", "Pass"], T0);
        t.OnRefresh(CallWindow("Riichi"), T0);
        Assert.False(t.CallWindowFromLabels);

        t.OnTick(hand, ["Riichi", "Pass"], T0.AddSeconds(30));
        Assert.True(t.CallWindowActive);
        Assert.Equal(["Riichi"], t.CallOptions);
    }

    [Fact]
    public void Call_window_for_an_uncallable_tile_is_not_ours()
    {
        var t = new EventTracker();
        // No souzu at all: an 8s can be neither pon'd, chi'd nor ron'd.
        t.OnTick(StructFixture.Decoded("333m456m89m567p7p7z", null), [], T0);
        t.OnRefresh(Discard(2, "8s"), T0);
        t.OnRefresh(CallBanner("Chi"), T0);
        Assert.False(t.CallWindowActive);
    }

    // ...but a type-23 whose row count matches its options is the game saying a local list is
    // on screen. That outranks our hand read: dropping it would throw away a real prompt over
    // a bug in the read, which is how a Ron on our own declared wait was passed
    // (docs/research/WIN_OFFERS_2026_09_22.md).
    [Fact]
    public void A_corroborated_row_list_opens_even_when_our_read_cannot_explain_it()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("333m456m89m567p7p7z", null), [], T0);
        t.OnRefresh(Discard(2, "8s"), T0);
        t.OnRefresh(CallWindow("Pon"), T0);
        Assert.True(t.CallWindowActive);
        Assert.Equal(["Pon"], t.CallOptions);
        Assert.Contains(t.RecentNotes(20), n => n.Contains("the read is wrong"));
    }

    [Fact]
    public void Chi_window_from_a_non_kamicha_seat_is_not_ours()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("45m111p222p333s7z9s", null), [], T0);
        t.OnRefresh(Discard(1, "3m"), T0); // shimocha: chi impossible
        t.OnRefresh(CallBanner("Chi"), T0);
        Assert.False(t.CallWindowActive);

        t.OnRefresh(Discard(3, "3m"), T0); // kamicha: chi legal
        t.OnRefresh(CallWindow("Chi"), T0);
        Assert.True(t.CallWindowActive);
        Assert.True(t.CallIsClaim);
    }

    [Fact]
    public void Own_turn_prompt_is_a_self_declare_not_a_claim()
    {
        var t = new EventTracker();
        t.OnRefresh(OurDraw(), T0);
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
        t.OnRefresh(CallWindow("Pon"), T0);
        // Post-call: 11 closed, claimed tile parked in slot 13.
        t.OnTick(StructFixture.PostPon, [], T0);
        var meld = Assert.Single(t.Melds);
        Assert.Equal(MeldType.Pon, meld.Type);
        Assert.False(t.CallWindowActive);
    }

    [Fact]
    public void AtkType74_does_not_book_and_hand_delta_books_once()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("22z34567m11p3459s", null), [], T0);
        t.OnRefresh(Discard(1, "2z"), T0);
        var payload = AtkFrame.OfInts([74, 0, 0, 0, 0, 0, 0, 0, 76069, 76069, 76069, 0]);
        t.OnReceiveEvent(74, payload);
        t.OnReceiveEvent(74, payload);
        Assert.Empty(t.Melds);
        t.OnTick(StructFixture.PostPon, [], T0);   // hand delta 13→11 books the pon
        Assert.Single(t.Melds);
        t.OnTick(StructFixture.PostPon, [], T0);
        Assert.Single(t.Melds);
    }

    [Fact]
    public void Stale_melds_clear_when_the_struct_shows_a_full_closed_hand()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("22z34567m11p3459s", null), [], T0);
        t.OnRefresh(Discard(1, "2z"), T0);
        t.OnTick(StructFixture.PostPon, [], T0);
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

    // Live 2026-09-19: after the actuator picks a row, the game echoes a type-19 with the
    // same labels; a riichi then waits for our discard, so the window must be gone.
    [Fact]
    public void Answered_window_clears_and_ignores_its_echo()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("34m788m111p789p99s", "5m");
        t.OnTick(hand, [], T0);
        t.OnRefresh(CallWindow("Riichi"), T0);
        Assert.True(t.CallWindowActive);
        Assert.Equal(["Riichi"], t.CallOptions); // banner deduped

        t.MarkCallAnswered(isWin: false, t.CallWindowGeneration);
        Assert.False(t.CallWindowActive);
        t.OnRefresh(CallWindow("Riichi"), T0); // echo
        Assert.False(t.CallWindowActive);
        t.OnTick(hand, ["Riichi", "Pass"], T0);                     // panel texts persist
        Assert.False(t.CallWindowActive);

        t.OnRefresh(Discard(0, "8m"), T0);                          // our riichi discard
        t.OnRefresh(Discard(1, "1p"), T0);                          // we hold three 1p
        t.OnRefresh(CallWindow("Pon"), T0);         // a genuinely new window
        Assert.True(t.CallWindowActive);
    }

    [Fact]
    public void Answered_win_holds_until_the_win_screen()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("44m77m3p55p556s6699s", "3p"), [], T0);
        t.OnRefresh(CallWindow("Tsumo", "Riichi"), T0);
        t.MarkCallAnswered(isWin: true, t.CallWindowGeneration);
        Assert.True(t.WinDeclared);
        t.OnRefresh(AtkFrame.OfInts([32, .. new int[21]]).WithString(2, "East 2 East Wind"), T0);
        Assert.False(t.WinDeclared);
    }

    // A win click that never landed is proven by play continuing: the next discard or
    // turn advance must release the RoundEnd hold instead of freezing every decision.
    [Fact]
    public void Answered_win_is_dropped_when_play_continues()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("44m77m3p55p556s6699s", "3p"), [], T0);
        t.OnRefresh(CallWindow("Tsumo", "Riichi"), T0);
        t.MarkCallAnswered(isWin: true, t.CallWindowGeneration);
        Assert.True(t.WinDeclared);
        t.OnRefresh(Discard(2, "9m"), T0);
        Assert.False(t.WinDeclared);
    }

    [Fact]
    public void Answered_chi_keeps_the_claimed_tile_for_the_shape_chooser()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("233m2345p0p23456s", null), [], T0);
        t.OnRefresh(Discard(3, "4s"), T0);
        t.OnRefresh(CallWindow("Chi"), T0);
        t.MarkCallAnswered(isWin: false, t.CallWindowGeneration);                          // "Chi" row clicked
        Assert.False(t.CallWindowActive);

        int I(string tile) => StructFixture.IconOf(Tile.Parse(tile), 76041);
        t.OnRefresh(AtkFrame.OfInts(25, 6, 0, 3,
            I("2s"), I("3s"), I("4s"), 76041,
            I("3s"), I("4s"), I("5s"), 76041,
            I("4s"), I("5s"), I("6s"), 76041).WithString(2, "Chi"), T0);
        Assert.True(t.CallWindowActive);
        Assert.Equal(Tile.Parse("4s"), t.CallTile);
        Assert.Equal(3, t.CallFromSeat);
        Assert.Equal(3, t.CallShapes.Count);
    }

    // The reverse of the test above, and the ordering the audit asked for: a Chi row can
    // raise its type-25 shape chooser synchronously, INSIDE the ReceiveEvent our dispatch
    // is still in. The answer names the window it was aimed at, so it must not clear the
    // chooser that is now on screen (docs/research/CALL_WINDOW_AUDIT_2026_09_22.md).
    [Fact]
    public void Answer_aimed_at_a_superseded_window_leaves_the_new_one_open()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("233m2345p0p23456s", null), [], T0);
        t.OnRefresh(Discard(3, "4s"), T0);
        t.OnRefresh(CallWindow("Chi"), T0);
        var answered = t.CallWindowGeneration;           // captured before the click, as the actuator does

        int I(string tile) => StructFixture.IconOf(Tile.Parse(tile), 76041);
        t.OnRefresh(AtkFrame.OfInts(25, 6, 0, 3,
            I("2s"), I("3s"), I("4s"), 76041,
            I("3s"), I("4s"), I("5s"), 76041,
            I("4s"), I("5s"), I("6s"), 76041).WithString(2, "Chi"), T0);
        Assert.NotEqual(answered, t.CallWindowGeneration);

        t.MarkCallAnswered(isWin: false, answered);
        Assert.True(t.CallWindowActive);
        Assert.Equal(3, t.CallShapes.Count);
        Assert.Contains(t.RecentNotes(20), n => n.Contains($"answer for call window #{answered} ignored"));
    }

    // A win answered against the wrong window must not set WinDeclared either: that holds
    // the phase at RoundEnd and would freeze every decision after it.
    [Fact]
    public void A_superseded_win_answer_does_not_declare_the_win()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("44m77m3p55p556s6699s", "3p");
        t.OnTick(hand, [], T0);
        t.OnRefresh(CallWindow("Tsumo", "Riichi"), T0);
        var stale = t.CallWindowGeneration - 1;

        t.MarkCallAnswered(isWin: true, stale);
        Assert.False(t.WinDeclared);
        Assert.True(t.CallWindowActive);
    }

    [Fact]
    public void Each_window_gets_its_own_generation()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("22z34567m11p3459s", null), [], T0);
        t.OnRefresh(Discard(1, "2z"), T0);
        t.OnRefresh(CallWindow("Pon"), T0);
        var first = t.CallWindowGeneration;

        t.MarkCallAnswered(isWin: false, first);
        t.OnRefresh(Discard(2, "2z"), T0);
        t.OnRefresh(CallWindow("Pon"), T0);
        Assert.True(t.CallWindowActive);
        Assert.True(t.CallWindowGeneration > first);
    }

    // After a riichi the panel keeps its "Riichi"/"Pass" rows visible - on 2026-09-23 they sat
    // under the round recap - and the label edge re-opened a phantom self-declare on every turn
    // advance, 219 times in one session. The game never offers riichi to a player already in
    // it, so that label is dropped while we are in riichi.
    [Fact]
    public void A_riichi_label_is_ignored_once_we_are_already_in_riichi()
    {
        var t = new EventTracker();
        var seated = StructFixture.Decoded("34m788m111p789p99s", "5m", riichiIndices: [3, 255, 255, 255]);
        t.OnTick(seated, ["Riichi", "Pass"], T0);
        Assert.False(t.CallWindowActive);

        // ...and the same panel text on a hand that is NOT in riichi still opens one.
        var free = new EventTracker();
        free.OnRefresh(OurDraw(), T0);
        free.OnTick(StructFixture.Decoded("34m788m111p789p99s", "5m"), ["Riichi", "Pass"], T0);
        Assert.True(free.CallWindowActive);
        Assert.Equal(["Riichi"], free.CallOptions);
    }

    // A riichi hand can still be offered Tsumo or a concealed Kan, so only the impossible
    // label is dropped - the rest still opens a window.
    [Fact]
    public void A_riichi_hand_can_still_be_offered_tsumo()
    {
        var t = new EventTracker();
        var seated = StructFixture.Decoded("44m77m3p55p556s6699s", "3p", riichiIndices: [3, 255, 255, 255]);
        t.OnRefresh(OurDraw(), T0);
        t.OnTick(seated, ["Riichi", "Tsumo", "Pass"], T0);
        Assert.True(t.CallWindowActive);
        Assert.Equal(["Tsumo"], t.CallOptions);
    }

    // [1] of a win screen is sometimes an empty string rather than a seat index. Int() then
    // yields 0, which reads as "seat 0 won" - us - and on 2026-09-23 a 3,000 point loss was
    // reported as our own win, which in turn made the recap check compare our hand against
    // the winner's.
    [Fact]
    public void A_win_screen_without_a_seat_index_leaves_the_winner_unknown()
    {
        var t = new EventTracker();
        t.OnRefresh(AtkFrame.OfInts([32, 0, .. new int[20]]).WithString(1, "").WithString(2, "East 1 East Wind"), T0);
        Assert.Equal(-1, t.LastWinnerSeat);

        var named = new EventTracker();
        named.OnRefresh(AtkFrame.OfInts([32, 2, .. new int[20]]).WithString(2, "East 1 East Wind"), T0);
        Assert.Equal(2, named.LastWinnerSeat);
    }

    // The asymmetry this fixes: a claim edge dies on its own because the discard it hangs on
    // ages out after 8 s, while a self-declare edge used to hang on hand SHAPE, which is true
    // on every draw forever. Stale panel text with no recent draw behind it now opens nothing.
    [Fact]
    public void A_self_declare_label_needs_a_recent_draw_the_way_a_claim_needs_a_discard()
    {
        var stale = new EventTracker();
        stale.OnRefresh(OurDraw(), T0);
        stale.OnTick(StructFixture.Decoded("123m456p789s1122z", "3z"), ["Tsumo", "Pass"], T0.AddSeconds(30));
        Assert.False(stale.CallWindowActive);

        var fresh = new EventTracker();
        fresh.OnRefresh(OurDraw(), T0);
        fresh.OnTick(StructFixture.Decoded("123m456p789s1122z", "3z"), ["Tsumo", "Pass"], T0.AddSeconds(2));
        Assert.True(fresh.CallWindowActive);
    }

    [Fact]
    public void Notes_are_kept_for_stall_dumps()
    {
        var t = new EventTracker();
        t.OnRefresh(Discard(1, "2z"), T0);
        var notes = t.RecentNotes(10);
        Assert.Contains(notes, n => n.Contains("discard seat=1 2z"));
    }

    [Fact]
    public void Self_declare_label_edge_needs_the_draw_in_hand()
    {
        var t = new EventTracker();
        t.OnRefresh(OurDraw(), T0);
        t.OnTick(StructFixture.Decoded("15m6m12p568p5s1356z", null), ["Tsumo", "Riichi", "Pass"], T0);
        Assert.False(t.CallWindowActive);
        t.OnTick(StructFixture.Decoded("15m6m12p568p5s1356z", null), [], T0);
        t.OnTick(StructFixture.Decoded("15m6m12p568p5s1356z", "9m"), ["Tsumo", "Riichi", "Pass"], T0);
        Assert.True(t.CallWindowActive);
        Assert.False(t.CallIsClaim);
    }

    // Live 2026-09-19 (AtkValues verbatim): after "Chi" the game asks which sequence:
    // [0]=25 [1]=6 [2]="Chi" [3]=3, then 2s3s4s·, 3s4s5s·, 4s5s6s· (· = 76041 placeholder).
    [Fact]
    public void Type25_offers_chi_shapes_and_keeps_the_claimed_tile()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("233m2345p0p23456s", null);
        t.OnTick(hand, [], T0);
        t.OnRefresh(Discard(3, "4s"), T0);
        t.OnRefresh(CallWindow("Chi"), T0);
        Assert.True(t.CallWindowActive);

        // The player clicks "Chi"; the game answers with the state-25 chooser.
        int I(string tile) => StructFixture.IconOf(Tile.Parse(tile), 76041);
        t.OnRefresh(AtkFrame.OfInts(25, 6, 0, 3,
            I("2s"), I("3s"), I("4s"), 76041,
            I("3s"), I("4s"), I("5s"), 76041,
            I("4s"), I("5s"), I("6s"), 76041).WithString(2, "Chi"), T0);
        Assert.True(t.CallWindowActive);
        Assert.True(t.CallIsClaim);
        Assert.Equal(["Chi"], t.CallOptions);
        Assert.Equal(Tile.Parse("4s"), t.CallTile);
        Assert.Equal(3, t.CallFromSeat);
        Assert.Equal(3, t.CallShapes.Count);
        Assert.Equal(TestTiles.Parse("4s5s6s"), t.CallShapes[2]);

        // The chosen shape arrives as type-13 (chi: [6]=255, claimed tile first) and closes it.
        t.OnRefresh(AtkFrame.OfInts(13, 0, 1, 5, 1, 3, 255, 3, I("4s"), I("5s"), I("6s")), T0);
        Assert.False(t.CallWindowActive);
        Assert.Empty(t.CallShapes);
        Assert.Single(t.SeatMeldsOf(0));
        Assert.Equal(TestTiles.Parse("4s5s6s"), t.SeatMeldsOf(0)[0].Tiles);
    }

    // Live 2026-09-19: the 74 payload read the placeholder as 1m and booked a bogus kan.
    [Fact]
    public void AtkType74_closes_the_window_but_never_books_a_meld()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("233m2345p0p23456s", null), [], T0);
        t.OnRefresh(Discard(3, "4s"), T0);
        t.OnRefresh(CallWindow("Chi"), T0);
        int I(string tile) => StructFixture.IconOf(Tile.Parse(tile), 76041);
        t.OnReceiveEvent(74, AtkFrame.OfInts([0, .. new int[7], 76041, I("4s"), I("5s"), I("6s")]));
        Assert.False(t.CallWindowActive);
        Assert.Empty(t.SeatMeldsOf(0));
    }
}
