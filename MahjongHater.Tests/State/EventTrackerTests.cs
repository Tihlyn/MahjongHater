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
        t.OnTick(StructFixture.Decoded("22z34567m11p3459s", null), T0);
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
        t.OnTick(StructFixture.Decoded("123m456p789s11z22z", null), T0);
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
        t.OnTick(StructFixture.Decoded("123m456p789s11z22z", null), T0);
        t.OnRefresh(Discard(1, "2z"), T0);
        t.OnRefresh(CallCodes(2, 2, 0).WithString(7, "Ron").WithString(8, "Pass"), T0);
        Assert.Equal(["Ron"], t.CallOptions);

        // Live 2026-09-22: a type-19 carrying [1]=13 [2]=0 [3]=1 [4]=2 … an index ramp, whose
        // [3]=1 would otherwise read as "Tsumo offered".
        t.OnRefresh(AtkFrame.OfInts([19, 13, 0, 1, 2, 3, 4, 5, 6]), T0);
        Assert.Equal(["Ron"], t.CallOptions);
    }

    // The call panel's button TEXT used to open windows on its own, on a rising edge of the
    // visible labels. It no longer does anything at all. The panel keeps its texts after a
    // prompt closes, so the edge was a guess that an event then had to confirm or expire; it
    // never found a prompt the events missed (all nine self-declares answered in the
    // 2026-09-22 session came from the type-19/23), and a window it opened was refused by the
    // actuator anyway. What it did produce was 219 phantoms in one 2026-09-23 session.
    [Fact]
    public void Panel_text_alone_never_opens_a_window()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("123m456p789s1122z", "3z");
        t.OnRefresh(OurDraw(), T0);
        t.OnTick(hand, T0);
        Assert.False(t.CallWindowActive);

        t.OnTick(hand, T0.AddSeconds(1));
        Assert.False(t.CallWindowActive);
    }

    // The event is the whole story now: it opens the window and nothing textual expires it.
    [Fact]
    public void An_event_opens_the_window_and_it_stays()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("123m456p789s1122z", "3z");
        t.OnRefresh(CallWindow("Riichi"), T0);
        Assert.True(t.CallWindowActive);

        t.OnTick(hand, T0.AddSeconds(30));
        Assert.True(t.CallWindowActive);
        Assert.Equal(["Riichi"], t.CallOptions);
    }

    [Fact]
    public void Call_window_for_an_uncallable_tile_is_not_ours()
    {
        var t = new EventTracker();
        // No souzu at all: an 8s can be neither pon'd, chi'd nor ron'd.
        t.OnTick(StructFixture.Decoded("333m456m89m567p7p7z", null), T0);
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
        t.OnTick(StructFixture.Decoded("333m456m89m567p7p7z", null), T0);
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
        t.OnTick(StructFixture.Decoded("45m111p222p333s7z9s", null), T0);
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
        t.OnTick(StructFixture.Decoded("123m456p789s1122z", "3z"), T0);
        t.OnRefresh(CallWindow("Riichi"), T0);
        Assert.True(t.CallWindowActive);
        Assert.False(t.CallIsClaim);
        Assert.Null(t.CallTile);
        Assert.Equal(["Riichi"], t.CallOptions);
    }

    // A claim window is the event's to open and the events' to close: play continuing
    // (type-5 draw) ends it, and a later discard opens a fresh one.
    [Fact]
    public void A_claim_window_opens_on_its_event_and_closes_when_play_moves_on()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("22z34567m11p3459s", null);
        t.OnTick(hand, T0);
        t.OnRefresh(Discard(3, "2z"), T0);
        t.OnRefresh(CallWindow("Pon"), T0);
        Assert.True(t.CallWindowActive);
        Assert.Equal(Tile.Parse("2z"), t.CallTile);

        t.OnTick(hand, T0);   // ticks alone neither re-open nor close it
        Assert.True(t.CallWindowActive);

        t.OnRefresh(AtkFrame.OfInts(5, 40, 0, 76041), T0);   // play continues
        Assert.False(t.CallWindowActive);
        t.OnTick(hand, T0);                                   // stale panel text: still nothing
        Assert.False(t.CallWindowActive);

        t.OnRefresh(Discard(1, "2z"), T0);                    // a fresh discard we can pon
        t.OnRefresh(CallWindow("Pon"), T0);
        Assert.True(t.CallWindowActive);
    }

    [Fact]
    public void Hand_delta_after_a_pon_reconstructs_the_meld()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("22z34567m11p3459s", null), T0);
        t.OnRefresh(Discard(1, "2z"), T0);
        t.OnRefresh(CallWindow("Pon"), T0);
        // Post-call: 11 closed, claimed tile parked in slot 13.
        t.OnTick(StructFixture.PostPon, T0);
        var meld = Assert.Single(t.Melds);
        Assert.Equal(MeldType.Pon, meld.Type);
        Assert.False(t.CallWindowActive);
    }

    [Fact]
    public void AtkType74_does_not_book_and_hand_delta_books_once()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("22z34567m11p3459s", null), T0);
        t.OnRefresh(Discard(1, "2z"), T0);
        var payload = AtkFrame.OfInts([74, 0, 0, 0, 0, 0, 0, 0, 76069, 76069, 76069, 0]);
        t.OnReceiveEvent(74, payload);
        t.OnReceiveEvent(74, payload);
        Assert.Empty(t.Melds);
        t.OnTick(StructFixture.PostPon, T0);   // hand delta 13→11 books the pon
        Assert.Single(t.Melds);
        t.OnTick(StructFixture.PostPon, T0);
        Assert.Single(t.Melds);
    }

    [Fact]
    public void Stale_melds_clear_when_the_struct_shows_a_full_closed_hand()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("22z34567m11p3459s", null), T0);
        t.OnRefresh(Discard(1, "2z"), T0);
        t.OnTick(StructFixture.PostPon, T0);
        Assert.Single(t.Melds);
        t.OnTick(StructFixture.Decoded("123m456p789s1122z", null), T0); // next deal, 13 closed
        Assert.Empty(t.Melds);
    }

    [Fact]
    public void Discard_counts_dropping_to_zero_resets_the_round()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("22z34567m11p3459s", null), T0);
        t.OnRefresh(Discard(1, "9p"), T0);
        Assert.NotEmpty(t.SeatDiscardsOf(1));
        t.OnTick(StructFixture.Decoded("123m456p789s1122z", null), T0);
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

        t.OnRefresh(AtkFrame.OfInts(32, 0, 0, 0, 0, 0, 0, 0, 0), T0);
        t.OnRefresh(AtkFrame.OfInts([29, 12, -12, 0, 0]), T0);         // score screen: +1200
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
    // Sending an answer does not close the window - the GAME closes it, on the discard or
    // draw that follows. The tracker used to clear it the instant the actuator dispatched,
    // which made a dispatch its own acknowledgement: an answer the game ignored looked
    // exactly like one it accepted, and the plugin moved on either way.
    [Fact]
    public void An_answer_stays_pending_until_the_game_acts_on_it()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("34m788m111p789p99s", "5m");
        t.OnTick(hand, T0);
        t.OnRefresh(CallWindow("Riichi"), T0);
        Assert.True(t.CallWindowActive);
        Assert.Equal(["Riichi"], t.CallOptions); // banner deduped

        var answered = t.CallWindowGeneration;
        t.NoteAnswerSent("Riichi", isWin: false, answered, T0);
        Assert.NotNull(t.Answer);
        Assert.True(t.CallWindowActive);          // still the game's window

        // The echo must not open a NEW generation: the pending answer is tied to one, and a
        // bumped generation would orphan it into a spurious timeout.
        t.OnRefresh(CallWindow("Riichi"), T0);
        Assert.Equal(answered, t.CallWindowGeneration);
        Assert.Equal(["Riichi"], t.CallOptions);
        Assert.NotNull(t.Answer);

        t.OnRefresh(Discard(0, "8m"), T0);        // our riichi discard: the game acted
        Assert.False(t.CallWindowActive);
        Assert.Null(t.Answer);
        Assert.Contains(t.RecentNotes(20), n => n.Contains("confirmed after"));

        t.OnRefresh(Discard(1, "1p"), T0);        // we hold three 1p
        t.OnRefresh(CallWindow("Pon"), T0);       // a genuinely new window
        Assert.True(t.CallWindowActive);
    }

    // An answer the game never acts on must not wait forever, or one ignored dispatch parks
    // the plugin for the rest of the hand.
    [Fact]
    public void An_unacknowledged_answer_times_out_and_says_so()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("34m788m111p789p99s", "5m");
        t.OnTick(hand, T0);
        t.OnRefresh(CallWindow("Riichi"), T0);
        t.NoteAnswerSent("Riichi", isWin: false, t.CallWindowGeneration, T0);
        Assert.NotNull(t.Answer);

        t.OnTick(hand, T0.AddSeconds(1));
        Assert.NotNull(t.Answer);                 // still inside the window

        t.OnTick(hand, T0.AddSeconds(5));
        Assert.Null(t.Answer);
        Assert.Contains(t.RecentNotes(20), n => n.Contains("UNACKNOWLEDGED"));
    }

    // dalamud.log 13:31:42.685-42.692: accepting Chi raises the shape chooser 7 ms later,
    // which IS the game acting on the Chi - but the chooser advances the window generation,
    // so the answer sat on the old one and timed out with a false "never acted on it".
    [Fact]
    public void A_new_window_replacing_the_answered_one_confirms_the_answer()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("233m2345p0p23456s", null), T0);
        t.OnRefresh(Discard(3, "4s"), T0);
        t.OnRefresh(CallWindow("Chi"), T0);
        var answered = t.CallWindowGeneration;
        t.NoteAnswerSent("Chi", isWin: false, answered, T0);
        Assert.NotNull(t.Answer);

        int I(string tile) => StructFixture.IconOf(Tile.Parse(tile), 76041);
        t.OnRefresh(AtkFrame.OfInts(25, 6, 0, 3,
            I("2s"), I("3s"), I("4s"), 76041,
            I("3s"), I("4s"), I("5s"), 76041,
            I("4s"), I("5s"), I("6s"), 76041).WithString(2, "Chi"), T0);

        Assert.NotEqual(answered, t.CallWindowGeneration);
        Assert.Null(t.Answer);
        Assert.False(t.CurrentWindowAnswered);   // the NEW window is answerable
        Assert.Contains(t.RecentNotes(20), n => n.Contains("confirmed after") && n.Contains("chooser"));
    }

    // The 13:49:08 furiten, replayed. A Tsumo window opened one millisecond before a type-6
    // carrying a 13-wide 0..12 ramp. A previous fix read that ramp as "riichi accepted" and
    // cleared the call window on it - killing the live Tsumo prompt, so the policy fell
    // through to a discard and tsumogiri'd the winning tile.
    //
    // The ramp is NOT an acknowledgement: it recurs on every draw while in riichi. Nothing
    // outside the measured set (type-5/8/13/74/29/32, or a new prompt) may close a window.
    [Fact]
    public void A_post_riichi_slot_ramp_does_not_close_a_live_window()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("123m456p789s1122z", "3z");
        t.OnTick(hand, T0);
        t.OnRefresh(AtkFrame.OfInts([23, 2, 1, 0, .. new int[18]])
            .WithString(6, "Tsumo!").WithString(7, "Tsumo").WithString(8, "Pass"), T0);
        Assert.True(t.CallWindowActive);
        Assert.Equal(["Tsumo"], t.CallOptions);

        // The exact frame from the log: type-6, [1]=13, then 0..12.
        t.OnRefresh(AtkFrame.OfInts([6, 13, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, .. new int[7]]), T0);

        Assert.True(t.CallWindowActive);
        Assert.Equal(["Tsumo"], t.CallOptions);
    }

    // One prompt gets one answer. A call answer is not idempotent - Riichi and Pass are rows
    // of the same list - so an answer we are unsure about is never re-sent. On 2026-09-23 the
    // retry ladder put [11, 0] into one Riichi window three times, because nothing the tracker
    // watches closes that window until the riichi discard happens.
    [Fact]
    public void A_window_is_answered_once_even_after_the_answer_times_out()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("123m456p789s1122z", "3z");
        t.OnRefresh(OurDraw(), T0);
        t.OnTick(hand, T0);
        t.OnRefresh(CallWindow("Riichi"), T0);
        Assert.False(t.CurrentWindowAnswered);

        t.NoteAnswerSent("Riichi", isWin: false, t.CallWindowGeneration, T0);
        Assert.True(t.CurrentWindowAnswered);

        // The pending answer times out, but the window stays answered.
        t.OnTick(hand, T0.AddSeconds(5));
        Assert.Null(t.Answer);
        Assert.True(t.CurrentWindowAnswered);

        // A genuinely new prompt is answerable again.
        t.OnRefresh(Discard(0, "8m"), T0.AddSeconds(6));
        t.OnRefresh(Discard(1, "1p"), T0.AddSeconds(6));
        t.OnRefresh(CallWindow("Pon"), T0.AddSeconds(6));
        Assert.True(t.CallWindowActive);
        Assert.False(t.CurrentWindowAnswered);
    }

    [Fact]
    public void Answered_win_holds_until_the_win_screen()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("44m77m3p55p556s6699s", "3p"), T0);
        t.OnRefresh(CallWindow("Tsumo", "Riichi"), T0);
        t.NoteAnswerSent("Tsumo", isWin: true, t.CallWindowGeneration, T0);
        Assert.True(t.WinAnswerPending);
        t.OnRefresh(AtkFrame.OfInts([32, .. new int[21]]).WithString(2, "East 2 East Wind"), T0);
        Assert.False(t.WinAnswerPending);
    }

    // A win answer that never landed is proven by play continuing: the next discard or turn
    // advance must release the hold instead of freezing every decision after it.
    [Fact]
    public void Answered_win_is_dropped_when_play_continues()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("44m77m3p55p556s6699s", "3p"), T0);
        t.OnRefresh(CallWindow("Tsumo", "Riichi"), T0);
        t.NoteAnswerSent("Tsumo", isWin: true, t.CallWindowGeneration, T0);
        Assert.True(t.WinAnswerPending);
        t.OnRefresh(Discard(2, "9m"), T0);
        Assert.False(t.WinAnswerPending);
    }

    [Fact]
    public void Answered_chi_keeps_the_claimed_tile_for_the_shape_chooser()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("233m2345p0p23456s", null), T0);
        t.OnRefresh(Discard(3, "4s"), T0);
        t.OnRefresh(CallWindow("Chi"), T0);
        t.NoteAnswerSent("Chi", isWin: false, t.CallWindowGeneration, T0);                 // "Chi" row clicked
        Assert.True(t.CallWindowActive);                                                   // the game has not moved yet

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
        t.OnTick(StructFixture.Decoded("233m2345p0p23456s", null), T0);
        t.OnRefresh(Discard(3, "4s"), T0);
        t.OnRefresh(CallWindow("Chi"), T0);
        var answered = t.CallWindowGeneration;           // captured before the click, as the actuator does

        int I(string tile) => StructFixture.IconOf(Tile.Parse(tile), 76041);
        t.OnRefresh(AtkFrame.OfInts(25, 6, 0, 3,
            I("2s"), I("3s"), I("4s"), 76041,
            I("3s"), I("4s"), I("5s"), 76041,
            I("4s"), I("5s"), I("6s"), 76041).WithString(2, "Chi"), T0);
        Assert.NotEqual(answered, t.CallWindowGeneration);

        t.NoteAnswerSent("Chi", isWin: false, answered, T0);
        Assert.True(t.CallWindowActive);
        Assert.Equal(3, t.CallShapes.Count);
        Assert.Contains(t.RecentNotes(20), n => n.Contains($"answer for call window #{answered} ignored"));
    }

    // A win answered against the wrong window must not be recorded as pending either: that
    // suppresses every decision after it.
    [Fact]
    public void A_superseded_win_answer_does_not_declare_the_win()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("44m77m3p55p556s6699s", "3p");
        t.OnTick(hand, T0);
        t.OnRefresh(CallWindow("Tsumo", "Riichi"), T0);
        var stale = t.CallWindowGeneration - 1;

        t.NoteAnswerSent("Tsumo", isWin: true, stale, T0);
        Assert.False(t.WinAnswerPending);
        Assert.True(t.CallWindowActive);
    }

    [Fact]
    public void Each_window_gets_its_own_generation()
    {
        var t = new EventTracker();
        t.OnTick(StructFixture.Decoded("22z34567m11p3459s", null), T0);
        t.OnRefresh(Discard(1, "2z"), T0);
        t.OnRefresh(CallWindow("Pon"), T0);
        var first = t.CallWindowGeneration;

        t.NoteAnswerSent("Pon", isWin: false, first, T0);
        t.OnRefresh(Discard(2, "2z"), T0);
        t.OnRefresh(CallWindow("Pon"), T0);
        Assert.True(t.CallWindowActive);
        Assert.True(t.CallWindowGeneration > first);
    }



    // [1] of a win screen is NOT a winner seat, whether it is typed Int or String.
    [Fact]
    public void A_win_screen_without_a_seat_index_leaves_the_winner_unknown()
    {
        var t = new EventTracker();
        t.OnRefresh(AtkFrame.OfInts([32, 0, .. new int[20]]).WithString(1, "").WithString(2, "East 1 East Wind"), T0);
        Assert.Equal(-1, t.LastWinnerSeat);

        var named = new EventTracker();
        named.OnRefresh(AtkFrame.OfInts([32, 2, .. new int[20]]).WithString(2, "East 1 East Wind"), T0);
        Assert.Equal(-1, named.LastWinnerSeat);
    }


    [Fact]
    public void Notes_are_kept_for_stall_dumps()
    {
        var t = new EventTracker();
        t.OnRefresh(Discard(1, "2z"), T0);
        var notes = t.RecentNotes(10);
        Assert.Contains(notes, n => n.Contains("discard seat=1 2z"));
    }


    // Live 2026-09-19 (AtkValues verbatim): after "Chi" the game asks which sequence:
    // [0]=25 [1]=6 [2]="Chi" [3]=3, then 2s3s4s·, 3s4s5s·, 4s5s6s· (· = 76041 placeholder).
    [Fact]
    public void Type25_offers_chi_shapes_and_keeps_the_claimed_tile()
    {
        var t = new EventTracker();
        var hand = StructFixture.Decoded("233m2345p0p23456s", null);
        t.OnTick(hand, T0);
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
        t.OnTick(StructFixture.Decoded("233m2345p0p23456s", null), T0);
        t.OnRefresh(Discard(3, "4s"), T0);
        t.OnRefresh(CallWindow("Chi"), T0);
        int I(string tile) => StructFixture.IconOf(Tile.Parse(tile), 76041);
        t.OnReceiveEvent(74, AtkFrame.OfInts([0, .. new int[7], 76041, I("4s"), I("5s"), I("6s")]));
        Assert.False(t.CallWindowActive);
        Assert.Empty(t.SeatMeldsOf(0));
    }
}
