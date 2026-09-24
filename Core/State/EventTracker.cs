namespace MahjongHater.Core.State;

// The minimum of the AtkValues event logic the struct read does not cover: per-seat
// discards (type-8), call windows (type-19 + panel labels), meld reconstruction
// (hand delta / atkType=74 payload), round boundaries (type-21/29/32 + struct discard
// counts), winds, doras and session stats. Event meanings: docs/EMJ_ADDON_REFERENCE.md
// "Event model". No Dalamud types; fed by EmjStateReader, replayable in tests.
// An answer the actuator sent and the game has not yet acted on. Pure intent: it says what
// we asked for and when, never what happened. Resolved by observing the game.
public sealed record PendingAnswer(string Option, bool IsWin, long Generation, DateTime SentUtc);

public sealed class EventTracker
{
    private const int NoteRingCap = 400;

    // How long an answer may sit unacknowledged before we stop waiting on it. Real answers are
    // acted on within a frame or two; this only has to be long enough not to race the game.
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(3);

    private readonly Action<string>? logInfo;
    // Last NoteRingCap tracker lines (events, decisions, operator actions) so a stall
    // dump can show what led up to it without digging through dalamud.log.
    private readonly Queue<string> noteRing = new(NoteRingCap);

    private readonly List<Tile>[] seatDiscards = [[], [], [], []];
    // Meld compositions from type-13 and added-kan upgrades from type-14.
    // Struct records alone do not distinguish all meld kinds or retain red fives.
    private readonly MeldLedger[] seatMelds = [new(), new(), new(), new()];
    private readonly Wind?[] seatWinds = new Wind?[4];

    // Global discard order this round (0-based, every seat), parallel to seatDiscards, so
    // "discarded after seat X's riichi" survives calls that skip turns.
    private readonly List<int>[] seatDiscardOrder = [[], [], [], []];
    private int discardCounter;
    // Set by a discard, cleared by the next turn advance: a type-32 win that arrives while
    // it is set was a ron on that discard (a tsumo needs a draw, i.e. a type-5, first).
    private bool discardSinceTurnAdvance;
    private int lastDiscardSeat = -1;
    private Tile? lastDiscardTile;
    // Round-end tile runs seen in events (≥ 10 tile icons in one frame): the draw recap
    // reveals every hand and this is where its layout gets confirmed (docs/DEFENSE_PLAN.md §3.6).
    private readonly List<(int Type, int Count, List<(int Index, Tile Tile)> Tiles)> revealedRuns = [];

    private int eventWallRemaining = 70;
    private Tile? lastOpponentDiscard;
    private int lastOpponentDiscardSeat = -1;
    private DateTime lastOpponentDiscardUtc = DateTime.MinValue;
    // When the turn last advanced to us. A self-declare offer can only follow our own draw,
    // and this is what gives the label edge an event to hang on - see the selfDeclare guard.
    private DateTime lastOwnDrawUtc = DateTime.MinValue;


    private DateTime lastTickUtc;
    private bool callWindowActive;
    private List<string> callOptions = [];
    // Type-25 chi-shape chooser: the sequences offered, in the game's button order.
    private List<Tile[]> callShapes = [];
    private Tile? callTile;
    private int callFromSeat = -1;

    // Identity of the CURRENT window: bumped on every open (event, label edge or the
    // type-25 shape chooser). An operator captures it before dispatch and hands it back
    // with the answer, so a handler that synchronously opens the NEXT prompt inside
    // ReceiveEvent cannot have that new prompt cleared by the old one's answer.
    private long callWindowGeneration;
    private PendingAnswer? pendingAnswer;
    private long answeredGeneration = -1;
    // Options of the window the actuator already answered: the game echoes the selection
    // as a type-19 with the same labels (verified live 2026-09-19), which must not re-open it.
    private string? answeredSignature;
    private Tile? answeredCallTile;
    private int answeredCallFromSeat = -1;
    // True after a score/win screen (or at load): the next type-21 Layout-1 deal is a
    // real round start; mid-round type-21 refreshes must not wipe state (reference doc,
    // "Cold-start reader lifecycle").
    private bool roundEnded = true;
    private int lastTotalDiscards;
    private List<Tile> prevClosedAll = [];
    // Last struct view (slots 0..12 and the slot-13 tile) for the legality gate and the
    // offered-tile fallback when a type-19 arrives between ticks.
    private List<Tile> lastClosed = [];
    private Tile? lastDrawnTile;

    private Wind trackedRoundWind = Wind.East;
    private Wind? eventSeatWind;
    private List<Tile> eventDoras = [];

    private bool riichiDeclared;

    public EventTracker(Action<string>? logInfo = null)
    {
        this.logInfo = logInfo;
    }

    public IReadOnlyList<Meld> Melds => this.seatMelds[0].Melds;

    public IReadOnlyList<Meld> SeatMeldsOf(int seat) => this.seatMelds[seat].Melds;

    internal IReadOnlyList<MeldObservation> MeldObservations(int seat) => this.seatMelds[seat].Entries;

    public IReadOnlyList<Tile> SeatDiscardsOf(int seat) => this.seatDiscards[seat];

    // Global order index of each discard in SeatDiscardsOf(seat), same length.
    public IReadOnlyList<int> SeatDiscardOrderOf(int seat) => this.seatDiscardOrder[seat];

    // Hand number inside the round (East 1 → 1), 0 when no text has said yet.
    public int HandNumber { get; private set; }

    // Set at the type-32 win screen: true when the win was a ron on the last discard.
    public bool LastWinByRon { get; private set; }

    // The seat whose discard was ron'd and the tile, valid when LastWinByRon.
    public int RonVictimSeat { get; private set; } = -1;

    public Tile? RonTile { get; private set; }

    // Tile runs captured from round-end events (see revealedRuns).
    public IReadOnlyList<(int Type, int Count, List<(int Index, Tile Tile)> Tiles)> RevealedRuns => this.revealedRuns;

    // Relative seat showing "East" in the wind texts, -1 when unknown.
    public int DealerSeat => Array.IndexOf(this.seatWinds, Wind.East);

    public bool CallWindowActive => this.callWindowActive;

    // Monotonic id of the open window; 0 before the first one. See callWindowGeneration.
    public long CallWindowGeneration => this.callWindowGeneration;

    public IReadOnlyList<string> CallOptions => this.callOptions;

    // Non-empty while the game asks which two hand tiles form the chi (state 25).
    public IReadOnlyList<Tile[]> CallShapes => this.callShapes;

    public Tile? CallTile => this.callTile;

    public int CallFromSeat => this.callFromSeat;

    // True for a Pon/Chi/Kan/Ron window on an opponent's discard, false for an own-turn
    // Riichi/Tsumo/Kan prompt.
    public bool CallIsClaim { get; private set; }

    public int EventWallRemaining => this.eventWallRemaining;

    public Wind RoundWind => this.trackedRoundWind;

    public Wind? SeatWind => this.seatWinds[0] ?? this.eventSeatWind;

    public IReadOnlyList<Tile> EventDoras => this.eventDoras;

    public bool RoundEnded => this.roundEnded;

    // An answer we SENT and the game has not yet acted on. This is our own bookkeeping, not
    // an observation: it exists so a dispatch in flight is not re-sent or decided over, and it
    // is resolved only by watching the game, never by having sent it.
    public PendingAnswer? Answer => this.pendingAnswer;

    // A Tsumo/Ron we answered and the game has not confirmed. Suppresses further decisions on
    // a hand the game may already have scored. Previously called WinDeclared, which read as a
    // statement about the game - and was then used to force the phase to RoundEnd, so sending
    // a win MADE the plugin believe the round had ended whether or not the game agreed.
    public bool WinAnswerPending => this.pendingAnswer is { IsWin: true };

    // How long an answer has been waiting, for the log and for the stall ladder.
    public TimeSpan? AnswerPendingFor(DateTime utc)
        => this.pendingAnswer is { } a ? utc - a.SentUtc : null;

    // We have already answered the window that is open. One prompt gets one answer.
    //
    // A call answer is not idempotent - Riichi and Pass are different rows of the same list -
    // so "we are not sure it landed" is never a reason to send another one. On 2026-09-23 the
    // retry ladder sent [11, 0] three times into one Riichi window because nothing the tracker
    // watches closes that window; the answer sits pending until the riichi DISCARD happens,
    // and the discard is what the plugin should be doing in the meantime.
    //
    // This deliberately outlives the pending answer's timeout: if a dispatch really was lost
    // we give up that one prompt rather than guess again at a window whose rows we have
    // already acted on. The flag clears when the game gives us a new prompt (a new
    // generation) or the round moves on.
    public bool CurrentWindowAnswered => this.callWindowActive && this.answeredGeneration == this.callWindowGeneration;

    // The actuator SENT an answer for the open window. That is all this records.
    //
    // It used to also close the window and set the win flag, which made dispatch its own
    // acknowledgement: the snapshot moved on the instant we sent, whether or not the game
    // did anything, and a refused or ignored answer was indistinguishable from an accepted
    // one. The window is the game's to close - it does so on the following draw, discard,
    // meld or score event - and this only notes that we are waiting.
    //
    // `generation` is the window the answer was aimed at, captured BEFORE dispatch. A native
    // handler may open the next prompt while ReceiveEvent is still running (a Chi row raises
    // its type-25 shape chooser that way), so an answer aimed at a window that has already
    // been replaced is dropped rather than attributed to the new one.
    public void NoteAnswerSent(string option, bool isWin, long generation, DateTime? now = null)
    {
        if (!this.callWindowActive)
            return;
        if (generation != this.callWindowGeneration)
        {
            this.Note($"answer for call window #{generation} ignored: #{this.callWindowGeneration} [{string.Join(",", this.callOptions)}] is open now");
            return;
        }

        this.answeredSignature = string.Join(",", this.callOptions);
        this.answeredCallTile = this.callTile;
        this.answeredCallFromSeat = this.callFromSeat;
        this.pendingAnswer = new PendingAnswer(option, isWin, generation, now ?? this.lastTickUtc);
        this.answeredGeneration = generation;
        this.Note($"answer \'{option}\' sent for window #{generation}; waiting for the game to act on it");
    }

    // A new prompt replacing the one we answered IS the game acting on that answer - most
    // visibly when accepting Chi raises its shape chooser 7 ms later. Every path that advances
    // the generation goes through here, so the answer that caused the new window is confirmed
    // by it instead of being orphaned on the old generation and timing out with a false
    // "the game never acted on it" (observed live 2026-09-23, 13:31:42).
    private void AdvanceCallWindowGeneration(string because)
    {
        this.ResolvePendingAnswer(this.callWindowGeneration, because);
        this.callWindowGeneration++;
    }

    // An answer stops being pending only on an observation. The game closing the window it
    // targeted is that observation; anything else leaves it pending until it times out.
    private void ResolvePendingAnswer(long generation, string because)
    {
        if (this.pendingAnswer is not { } a || a.Generation != generation)
            return;
        var waited = (this.lastTickUtc - a.SentUtc).TotalMilliseconds;
        this.Note($"answer \'{a.Option}\' for window #{generation} confirmed after {waited:F0} ms ({because})");
        this.pendingAnswer = null;
    }

    // A dispatch the game never acted on must not wait forever, or one ignored answer parks
    // the plugin for the rest of the hand. Timing out is reported as what it is - we do not
    // know whether the game refused it, dropped it, or never saw it.
    private void ExpirePendingAnswer(DateTime utc)
    {
        if (this.pendingAnswer is not { } a || utc - a.SentUtc <= AnswerTimeout)
            return;
        this.Note($"answer \'{a.Option}\' for window #{a.Generation} went UNACKNOWLEDGED for "
                  + $"{(utc - a.SentUtc).TotalSeconds:F1} s - the game never acted on it");
        this.pendingAnswer = null;
    }

    // The fu/han/limit/payment the game itself printed for the last win, or null when the
    // screen carried none. It is the only ground truth we get for the scoring rules, so it
    // is checked against ScoringEngine every hand rather than trusted from documentation
    // (docs/research/RULES_CROSSCHECK_2026_09_22.md).
    public WinScreen? LastWinScreen { get; private set; }

    // The game's own scoring of the last round: the winner's hand, every yaku it awarded with
    // that yaku's han, the fu/han total and the dora. This is the only place the game explains
    // its reasoning rather than just its result, so it is what our own scoring is checked
    // against (docs/research/ADDON_PROTOCOL_2026_09_23.md).
    public RoundRecap? LastRecap { get; private set; }

    public IReadOnlyList<string> RecentNotes(int tail) => this.noteRing.TakeLast(Math.Clamp(tail, 1, NoteRingCap)).ToList();

    // Winner resolved from a win announcement plus the type-29 transfers.
    // -1 before settlement, on draws, or when the result is ambiguous.
    public int LastWinnerSeat { get; private set; } = -1;

    public bool RiichiDeclared => this.riichiDeclared;

    public int WinsThisSession { get; private set; }

    // Our point change announced by the last type-29 score screen (0 until one arrives).
    public int LastScoreDelta { get; private set; }

    public HandSettlement? Settlement { get; private set; }
    private bool? resultWinByRon;
    private bool resultIsDraw;

    public int LossesThisSession { get; private set; }

    public Tile? LastOpponentDiscard => this.lastOpponentDiscard;

    // Best-effort round wind from the addon's visible text (fed by the reader).
    public void HintRoundWind(Wind wind) => this.trackedRoundWind = wind;

    // "South 4 South Wind": the round text also carries the hand number.
    public void HintRound(Wind wind, int handNumber)
    {
        this.trackedRoundWind = wind;
        if (handNumber is >= 1 and <= 4)
            this.HandNumber = handNumber;
    }

    // Seat winds from the four score-panel texts (nodes.seatWindTexts), null = unreadable.
    public void HintSeatWinds(IReadOnlyList<Wind?> winds)
    {
        for (var i = 0; i < 4 && i < winds.Count; i++)
            if (winds[i] is { } w)
                this.seatWinds[i] = w;
    }

    // ──────────────────────────────────────── EVENTS ────────────────────────────────────────

    public void OnRefresh(AtkFrame f, DateTime? now = null, int? meldSlot = null)
    {
        var utc = now ?? DateTime.UtcNow;
        var type = f.EventType;
        this.NoteRefresh(f);

        // Seat wind icon rides on [2] of stable-state events (never 5/6/8, which reuse [2]).
        if (type is not (5 or 6 or 8) && f.IsInt(2) && WindFromIcon(f.Int(2)) is { } sw)
            this.eventSeatWind = sw;

        // The deal (type-21) also carries our 14 tiles; everything else with a tile run
        // after a discard/turn is a reveal candidate (draw recap, win screen).
        if (type is not (21 or 13 or 14 or 25) && this.discardCounter > 0)
            this.CaptureRevealRun(f);

        switch (type)
        {
            case 5: // turn advance: [1]=wall remaining, [2]=seat that draws next
                if (f.Int(1) is > 0 and <= 70)
                    this.eventWallRemaining = f.Int(1);
                if (f.IsInt(2) && f.Int(2) == 0)
                    this.lastOwnDrawUtc = utc;
                this.discardSinceTurnAdvance = false;
                this.ClearCallWindow("turn advance (type-5)");
                this.DropWinDeclared("turn advance (type-5)");
                break;

            case 8: // THE discard event: [1]=seat, [2]=real tile icon, every seat
            {
                var seat = f.Int(1);
                if (seat is < 0 or > 3 || !TileHelpers.TryTileFromIconId(f.Int(2), out var tile))
                    break;
                this.seatDiscards[seat].Add(tile);
                this.seatDiscardOrder[seat].Add(this.discardCounter++);
                this.discardSinceTurnAdvance = true;
                this.lastDiscardSeat = seat;
                this.lastDiscardTile = tile;
                if (seat != 0)
                {
                    this.lastOpponentDiscard = tile;
                    this.lastOpponentDiscardSeat = seat;
                    this.lastOpponentDiscardUtc = utc;
                    // A fresh opponent discard can open a window whose labels are identical
                    // to the previous one (no edge) — let the next label scan re-evaluate.
                }

                this.roundEnded = false;
                this.ClearCallWindow($"discard (type-8) seat={seat} {tile}");
                this.DropWinDeclared($"discard (type-8) seat={seat}");
                this.Note($"discard seat={seat} {tile}");
                break;
            }

            case 13: // [1]=caller, [3]=4 pon/5 chi/6 kan, [5]=from-direction,
                     // [6]=tile index or 255, [7]=tile count, [8..11]=icons
            {
                var seat = f.Int(1);
                if (!f.IsInt(1) || seat is < 0 or > 3)
                    break;
                // Require the complete payload and a real meld shape. A truncated kan
                // must never be booked as a pon, nor an unknown type guessed from its tiles.
                var count = f.Int(7);
                if (!f.IsInt(3) || !f.IsInt(5) || !f.IsInt(7) || count is not (3 or 4)
                    || f.Int(5) is < 0 or > 3)
                    break;
                var tiles = new List<Tile>(4);
                for (var i = 0; i < count; i++)
                {
                    if (!f.IsInt(8 + i) || !TileHelpers.TryTileFromIconId(f.Int(8 + i), out var t)) break;
                    tiles.Add(t);
                }
                if (tiles.Count != count) break;
                var from = f.Int(5);
                var ordered = tiles.OrderBy(TileHelpers.Normalize).ToArray();
                var same = tiles.All(t => TileHelpers.SameKind(t, tiles[0]));
                var run = count == 3 && ordered.All(t => !t.IsHonor && t.Suit == ordered[0].Suit)
                    && ordered[1].Number == ordered[0].Number + 1 && ordered[2].Number == ordered[1].Number + 1;
                MeldType? kind = (f.Int(3), count, from) switch
                {
                    (4, 3, > 0) when same => MeldType.Pon,
                    (5, 3, > 0) when run => MeldType.Chi,
                    (6, 4, 0) when same => MeldType.Ankan,
                    (6, 4, > 0) when same => MeldType.Daiminkan,
                    _ => null,
                };
                if (kind == null)
                {
                    this.Note($"unresolved meld event: type={f.Int(3)} count={count} from={from}");
                    break;
                }
                // The live reader supplies the slot from the post-refresh struct count.
                // Payload [4] repeats across melds and is NOT a slot or ordinal.
                int? slot = meldSlot is >= 0 and < 4 ? meldSlot : null;
                if (this.TryAddMeld(seat, new Meld(kind.Value, ordered, kind != MeldType.Ankan),
                        $"type-13 from={from}", slot, from))
                    this.ClearCallWindow($"meld (type-13) seat={seat}");
                break;
            }

            case 14: // added kan: [1]=seat, [2]=tile index, [3]=fourth tile icon
            {
                if (!f.IsInt(1) || f.Int(1) is < 0 or > 3 || !f.IsInt(2) || !f.IsInt(3)
                    || !TileHelpers.TryTileFromIconId(f.Int(3), out var tile)
                    || TileHelpers.ToIndex(tile) != f.Int(2)) break;
                if (this.seatMelds[f.Int(1)].Upgrade(tile))
                    this.Log($"[Meld] seat {f.Int(1)} type-14: Shouminkan {tile}");
                this.ClearCallWindow("added kan (type-14)");
                break;
            }

            case 19: // call window (or another seat's Pon!/Chi! banner — same event)
            case 23: // call options (mirrors 19's rows)
            {
                // The options live in the INTEGER lane: [2] is the first row, [3] the second,
                // 0 = none, and on a type-23 [1] is the row count including Pass. Codes are
                // locale-independent and carry no "Pass"/banner convention; across the
                // 2026-09-22 session they agreed with the row strings on all 257 option
                // frames ([1] matched the row count on all 137 type-23 frames).
                // The strings stay as a cross-check because a type-19 also carries unrelated
                // integer payloads — one live frame read [1]=13 [2]=0 [3]=1 [4]=2 … [8]=6,
                // a plain 0..6 ramp that must never be read as "Tsumo offered".
                var opts = new List<string>(2);
                foreach (var i in new[] { 2, 3 })
                    if (f.IsInt(i) && OptionOfCode(f.Int(i)) is { } name && !opts.Contains(name))
                        opts.Add(name);
                var rows = new List<string>(2);
                for (var i = 7; i <= 8; i++)      // [6] is the banner slot ("Ron!", "Discard")
                {
                    var s = f.Str(i)?.TrimEnd('!');
                    if (!string.IsNullOrEmpty(s) && IsOption(s))
                        rows.Add(s);
                }

                var rowsMatch = rows.Count == opts.Count && rows.All(opts.Contains);
                var countMatches = type == 23 && f.IsInt(1) && f.Int(1) == opts.Count + 1;
                if (opts.Count > 0 && !rowsMatch && !countMatches)
                {
                    this.Note($"type-{type} ignored: codes [{string.Join(",", opts)}] do not match rows [{string.Join(",", rows)}]");
                    break;
                }

                if (opts.Count == 0 && rows.Count > 0)
                {
                    // Never seen live; if the integer lane ever changes meaning, the visible
                    // rows still carry the window rather than silently losing it.
                    this.Note($"type-{type}: no option codes, falling back to the rows [{string.Join(",", rows)}]");
                    opts = rows;
                }

                // Doras: Layout 1 exposes up to six at [16..21]; Layout 2 ([14]>0) only [16].
                var doraEnd = f.Int(14) > 0 ? 16 : 21;
                var doras = new List<Tile>(6);
                for (var i = 16; i <= doraEnd; i++)
                    if (f.IsInt(i) && TileHelpers.TryTileFromIconId(f.Int(i), out var d))
                        doras.Add(d);
                if (doras.Count > 0)
                    this.eventDoras = doras;

                Tile? offered = this.FreshOpponentDiscard(utc);
                if (offered is null && f.IsInt(4) && TileHelpers.TryTileFromIconId(f.Int(4), out var called))
                    offered = called;
                // A type-23 whose row count is exactly "options + Pass" describes a LIST the
                // game is asking us to choose from - an announcement of someone else's call
                // has no such list. That is the game's own statement that the prompt is
                // local, and it held on all 145 type-23 frames of 2026-09-22.
                this.OpenCallWindow(opts, offered, "type-19", localList: countMatches);
                break;
            }

            case 21: // new deal (Layout 1, seat 0, after a round end) or post-meld refresh
                if (f.Int(2) == 0 && f.Int(14) == 0 && this.roundEnded)
                {
                    this.ResetRound("new deal (type-21)");
                    this.roundEnded = false;
                }

                break;

            case 25: // chi-shape chooser (live 2026-09-19): [2]="Chi", [3]=count, then per option
                     // [4+4i..6+4i] = three tile icons, [7+4i] = 76041 placeholder
            {
                if (!string.Equals(f.Str(2), "Chi", StringComparison.OrdinalIgnoreCase))
                    break;
                var count = Math.Clamp(f.Int(3), 0, 4);
                var shapes = new List<Tile[]>(count);
                for (var i = 0; i < count; i++)
                {
                    var shape = new List<Tile>(3);
                    for (var k = 0; k < 3; k++)
                    {
                        if (f.IsInt(4 + (4 * i) + k) && TileHelpers.TryTileFromIconId(f.Int(4 + (4 * i) + k), out var tile))
                            shape.Add(tile);
                    }

                    if (shape.Count == 3)
                        shapes.Add(shape.ToArray());
                }

                if (shapes.Count == 0)
                    break;

                // The claimed tile is the one every shape contains; the window that was just
                // answered (still open when a human clicked) or the fresh opponent discard names
                // it when the shapes are ambiguous.
                var common = shapes.Skip(1).Aggregate(shapes[0].AsEnumerable(),
                    (acc, s) => acc.Where(t => s.Any(x => TileHelpers.SameKind(x, t)))).ToList();
                var claimed = this.callTile ?? this.answeredCallTile ?? this.FreshOpponentDiscard(utc) ?? (common.Count == 1 ? common[0] : (Tile?)null);
                var fromSeat = this.callFromSeat >= 0 ? this.callFromSeat : this.answeredCallFromSeat;
                this.answeredSignature = null;
                this.callWindowActive = true;
                this.AdvanceCallWindowGeneration("the chi shape chooser replaced it");
                this.callOptions = ["Chi"];
                this.callShapes = shapes;
                this.CallIsClaim = true;
                this.callTile = claimed;
                this.callFromSeat = fromSeat >= 0 ? fromSeat : 3;
                this.Note($"chi shapes (type-25): [{string.Join(" | ", shapes.Select(s => string.Join("", s.Select(x => x.ToString()))))}] tile={claimed?.ToString() ?? "-"}");
                break;
            }

            case 31: // conclusion: observed draw layout has mode [1]=0 and banner [4]=1
                if (this.Settlement == null && f.IsInt(1) && f.IsInt(4))
                    this.resultIsDraw = f.Int(1) == 0 && f.Int(4) == 1;
                break;

            case 29: // four final per-seat point transfers, in hundreds
            {
                this.roundEnded = true;
                this.pendingAnswer = null;
                this.ClearCallWindow("score (type-29)");
                var settlement = HandSettlement.Read(f, this.resultWinByRon, this.resultIsDraw);
                if (settlement == null || this.Settlement != null) break;
                this.Settlement = settlement;
                this.LastScoreDelta = settlement.SeatDeltas[0];
                this.LastWinnerSeat = settlement.WinnerSeat;
                this.LastWinByRon = settlement.WinByRon;
                if (settlement.RonVictimSeat != this.RonVictimSeat) this.RonTile = null;
                this.RonVictimSeat = settlement.RonVictimSeat;
                if (this.LastRecap is { } recap)
                    this.LastRecap = recap with { SeatDeltas = settlement.SeatDeltas };
                if (settlement.OutcomeKnown && settlement.WinnerSeat == 0) this.WinsThisSession++;
                else if (this.LastScoreDelta < 0) this.LossesThisSession++;
                this.Note($"settlement: winner={settlement.WinnerSeat} ron={settlement.WinByRon} "
                    + $"victim={settlement.RonVictimSeat} draw={settlement.IsDraw} known={settlement.OutcomeKnown} "
                    + $"deltas=[{string.Join(",", settlement.SeatDeltas)}]");
                break;
            }

            case 32: // win screen: [1] is NOT a winner seat; [2]="East 3 South Wind", [3]=1 when the
                     // winner is the dealer, [6]="40 Fu 3 Han [Mangan]", [7]=points/100,
                     // [8]=1 on tsumo (all four confirmed against 23 win screens, 2026-09-22).
            {
                this.roundEnded = true;
                this.pendingAnswer = null;
                // A repeated win screen must not overwrite an already settled result.
                if (this.Settlement != null) break;
                this.LastWinScreen = ParseWinScreen(f);
                this.LastRecap = RoundRecapReader.Read(f);
                this.resultIsDraw = false;
                this.resultWinByRon = f.IsInt(8) && f.Int(8) is 0 or 1 ? f.Int(8) == 0 : null;
                this.LastWinnerSeat = -1; // [1] is commonly zero for opponents too.
                this.LastWinByRon = this.resultWinByRon == true;
                this.RonVictimSeat = this.LastWinByRon && this.discardSinceTurnAdvance ? this.lastDiscardSeat : -1;
                this.RonTile = this.RonVictimSeat >= 0 ? this.lastDiscardTile : null;
                this.ClearCallWindow("win screen (type-32)");
                var round = f.Str(2) ?? string.Empty;
                if (ParseHandNumber(round) is { } handNo)
                    this.HandNumber = handNo;
                if (round.StartsWith("East", StringComparison.OrdinalIgnoreCase)) this.trackedRoundWind = Wind.East;
                else if (round.StartsWith("South", StringComparison.OrdinalIgnoreCase)) this.trackedRoundWind = Wind.South;
                else if (round.StartsWith("West", StringComparison.OrdinalIgnoreCase)) this.trackedRoundWind = Wind.West;
                else if (round.StartsWith("North", StringComparison.OrdinalIgnoreCase)) this.trackedRoundWind = Wind.North;
                break;
            }
        }
    }

    // PostReceiveEvent atkType=74: our meld was accepted. Its [8..11] payload is not a
    // safe tile source (live 2026-09-19: the 76041 placeholder of a chi read as a real 1m
    // and produced a bogus kan that outranked the correct type-13 meld); the type-13 that
    // follows within a frame names the tiles for every seat, and the hand-delta inference
    // covers a missed one. Here it only closes the window.
    public void OnReceiveEvent(int atkType, AtkFrame f)
    {
        if (atkType != 74)
            return;

        var tiles = 0;
        for (var i = 8; i < 12; i++)
            if (f.IsInt(i) && TileHelpers.TryTileFromIconId(f.Int(i), out _))
                tiles++;
        if (tiles >= 3)
            this.ClearCallWindow("meld accepted (atkType=74)");
    }

    // ──────────────────────────────────────── TICK ──────────────────────────────────────────

    // Per frame, after the struct read.
    //
    // This used to also open call windows from the call panel's button TEXT, on a rising edge
    // of the visible labels ("label edge"). That path is gone. The panel keeps its texts after
    // a prompt closes, so every window it produced was a guess that an event then had to
    // confirm or expire - and it was never the thing that found a real prompt: all nine
    // self-declares answered in the 2026-09-22 session came from the type-19/23 event, none
    // from the labels. What it did produce was phantoms, 219 of them in one 2026-09-23
    // session, each flipping the phase to SelfDeclare for ~40 ms. It also could not be acted
    // on: a window it opened was marked label-only and then REFUSED by the actuator, so its
    // single observable effect on the plugin was noise. Windows come from events now.
    public void OnTick(DecodedStruct s, DateTime? now = null)
    {
        var utc = now ?? DateTime.UtcNow;
        this.lastTickUtc = utc;
        this.ExpirePendingAnswer(utc);

        var closed = s.ClosedTiles;
        var closedAll = s.HandInVisualOrder();
        var melds = this.seatMelds[0];

        // The struct's meld counts are the authority: drop tracked melds a seat no longer
        // has (round rolled over unnoticed; a chi we never saw the type-13 for can only be
        // missing, never extra). Without counts, 13-14 closed tiles proves zero melds.
        for (var seat = 0; seat < 4; seat++)
        {
            var list = this.seatMelds[seat];
            var structCount = s.Seats[seat].MeldCount;
            if (structCount is >= 0 and <= 4 || (structCount == null && seat == 0 && closedAll.Count >= 13))
            {
                list.Trim(structCount ?? 0);
            }
        }

        // Round boundary from the struct: every discard count back to zero, or the hand
        // replaced wholesale at 13 tiles (a deal), when a round was in progress.
        var total = s.TotalDiscards;
        var countsMapped = s.DiscardCounts.Any(c => c is not null);
        var dealShape = closed.Count == 13 && this.prevClosedAll.Count >= 13
                        && MeldInference.Removed(this.prevClosedAll, closedAll).Count >= 8;
        if ((countsMapped && total == 0 && this.lastTotalDiscards > 0) || dealShape)
        {
            this.ResetRound(dealShape ? "hand replaced (deal shape)" : "discard counts reset");
            this.roundEnded = false;
        }

        this.lastTotalDiscards = countsMapped ? total : this.lastTotalDiscards;

        // Our meld reconstruction from the closed-hand delta (see MeldInference) — the
        // fallback when no type-13/74 payload named the tiles. The claimed tile is the
        // event's offered tile, else whatever the struct parked in slot 13.
        var drop = this.prevClosedAll.Count - closed.Count;
        var missingMeld = s.Us.MeldCount is { } mc ? melds.Count < mc : melds.Count < 4;
        if (drop >= 2 && this.prevClosedAll.Count > 0 && missingMeld)
        {
            var called = this.callTile ?? this.FreshOpponentDiscard(utc) ?? s.DrawnTile;
            var meld = MeldInference.Infer(this.prevClosedAll, closed, drop == 4 ? null : called)
                       ?? MeldInference.Infer(this.prevClosedAll, closed, null);
            if (meld is not null && this.TryAddMeld(0, meld, "hand delta", s.Us.MeldCount is > 0 and <= 4 ? s.Us.MeldCount - 1 : null))
                this.ClearCallWindow("meld inferred from hand delta");
        }

        this.prevClosedAll = closedAll;
        this.lastClosed = [.. closed];
        this.lastDrawnTile = s.DrawnTile;

        // Call windows are opened and closed by EVENTS alone (type-19/23/25 open them;
        // type-5 draw, type-8 discard, type-13/74 meld and 29/32 close them). The panel and
        // its list rows keep their texts unchanged after a window closes — verified live
        // 2026-09-19, the open and closed trees are identical — so the texts can neither
        // open nor close one. A prompt has no auto-pass timer either (one sat open for four
        // minutes), so there is no time-based clear.
    }

    public void Reset()
    {
        this.lastOwnDrawUtc = DateTime.MinValue;
        this.ResetRound("reset");
        this.roundEnded = true;
        this.lastTotalDiscards = 0;
        this.prevClosedAll = [];
        this.trackedRoundWind = Wind.East;
        this.eventSeatWind = null;
        Array.Clear(this.seatWinds);
        this.eventDoras = [];
        this.WinsThisSession = 0;
        this.LossesThisSession = 0;
    }

    // Tracker decisions go to the Dalamud log (verbose) and into the note ring, so a
    // session can be reconstructed from dalamud.log and a stall dump from the ring.
    public void Note(string message)
    {
        this.logInfo?.Invoke($"[Tracker] {message}");
        if (this.noteRing.Count >= NoteRingCap)
            this.noteRing.Dequeue();
        this.noteRing.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    // A declared win is final; a later discard or turn advance proves the click never
    // landed, and holding the phase at RoundEnd would freeze every decision after it.
    // Play moving on IS the observation that our answer landed (or that it never will).
    private void DropWinDeclared(string why)
    {
        if (this.pendingAnswer is not { } a)
            return;
        this.pendingAnswer = null;
        this.Note($"answer \'{a.Option}\' for window #{a.Generation} is no longer pending: {why}");
    }

    // ──────────────────────────────────────── INTERNALS ─────────────────────────────────────

    private Tile? FreshOpponentDiscard(DateTime utc)
        => this.lastOpponentDiscard is { } t && (utc - this.lastOpponentDiscardUtc).TotalSeconds < 8 ? t : null;

    // Claim (Pon/Chi/Ron on an opponent's discard) vs self-declare (Riichi/Tsumo on our
    // draw) is decided by the labels; a Kan-only set is a claim when we hold three of
    // the offered tile. The offered tile falls back to whatever the struct parked in
    // slot 13 when no fresh type-8 named it.
    //
    // Legality gate: the game only opens a claim window when a call is legal for OUR
    // hand; a window failing it is another seat's action or a stale panel. Fails open
    // when the hand is not a plausible claim-time size (mid-transition).
    // localList: the frame itself proved the game is showing US a row list (a type-23 whose
    // row count matches its option codes plus Pass). Only then may the window override what
    // our own hand read believes.
    private void OpenCallWindow(List<string> options, Tile? offered, string source, bool localList = false)
    {
        // "Pon!" / "Riichi!" is the announcement banner echoing the same option.
        options = options.Select(o => o.TrimEnd('!')).Distinct().ToList();
        if (this.answeredSignature is { } answered && answered == string.Join(",", options))
        {
            this.Note($"call window ({source}) [{answered}] is the echo of the answered one; ignoring");
            return;
        }

        var meldCount = this.seatMelds[0].Count;
        var expected = HandTracking.MaxClosedTiles(meldCount) - 1;
        var claimShape = this.lastClosed.Count == expected;
        var candidate = offered ?? (claimShape ? this.lastDrawnTile : null);

        bool isClaim;
        if (options.Any(o => o is "Chi" or "Pon" or "Ron"))
            isClaim = true;
        else if (options.Any(o => o is "Riichi" or "Tsumo"))
            isClaim = false;
        else
            isClaim = candidate is { } k && claimShape && TileHelpers.CountKind(this.lastClosed, k) >= 3;

        if (isClaim && candidate is { } tile && claimShape
            && !HandTracking.HasAnyLegalCall(this.lastClosed, tile, meldCount, allowChi: this.lastOpponentDiscardSeat is 3 or -1))
        {
            // Our own hand read is the discriminator only while nothing better exists. A
            // label edge, or a bare type-19 (the event the game also uses for another seat's
            // Pon!/Chi! banner), carries no proof that the prompt is local, so a claim we
            // cannot derive is someone else's announcement or stale panel text.
            if (!localList)
            {
                this.Note($"call window ({source}) for {tile}: no legal local call — not ours; ignoring");
                this.ClearCallWindow("not our window");
                return;
            }

            // A corroborated row list IS the game stating the prompt is ours. Suppressing it
            // because our read cannot explain it would throw a real window away over a bug in
            // the read — the exact failure that passed a Ron on our own declared wait — so the
            // window opens and the disagreement is recorded instead
            // (docs/research/WIN_OFFERS_2026_09_22.md).
            this.Note($"call window ({source}) for {tile}: the game is showing a local row list and our closed read "
                      + $"[{string.Join(" ", this.lastClosed)}] supports no call on it — opening anyway; the read is wrong");
        }

        this.callWindowActive = true;
        this.AdvanceCallWindowGeneration($"a new window ({source}) replaced it");
        this.callOptions = options;
        this.CallIsClaim = isClaim;
        this.callTile = isClaim ? candidate : null;
        this.callFromSeat = isClaim && offered is not null ? this.lastOpponentDiscardSeat : -1;
        this.Note($"call window #{this.callWindowGeneration} ({source}): [{string.Join(",", options)}] tile={this.callTile?.ToString() ?? "-"} claim={isClaim}");
    }

    // Option codes of a type-19/23 row (live 2026-09-22; see the decode above).
    private static string? OptionOfCode(int code) => code switch
    {
        1 => "Tsumo",
        2 => "Ron",
        3 => "Riichi",
        4 => "Kan",
        5 => "Pon",
        6 => "Chi",
        _ => null,
    };

    private static bool IsOption(string s) => s is "Chi" or "Pon" or "Kan" or "Ron" or "Riichi" or "Tsumo";

    // "40 Fu 3 Han" / "25 Fu 5 Han Mangan" - fu, han and the limit name the game applied.
    private static WinScreen? ParseWinScreen(AtkFrame f)
    {
        var text = f.Str(6);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^\s*(\d+)\s*Fu\s+(\d+)\s*Han\s*(.*)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
            return null;
        var fu = int.Parse(m.Groups[1].Value);
        var han = int.Parse(m.Groups[2].Value);
        if (fu is < 20 or > 140 || han is < 1 or > 52)
            return null;
        return new WinScreen(fu, han, m.Groups[3].Value.Trim(), f.IsInt(7) ? f.Int(7) * 100 : -1,
            f.Int(3) == 1, f.Int(8) == 1);
    }

    private void ClearCallWindow(string why)
    {
        if (this.callWindowActive)
        {
            this.Note($"call window cleared: {why}");
            // The game closing the window we answered is the acknowledgement. Anything that
            // closes a window we did NOT answer leaves the pending answer alone, to time out.
            this.ResolvePendingAnswer(this.callWindowGeneration, why);
        }

        this.answeredSignature = null;
        this.answeredCallTile = null;
        this.answeredCallFromSeat = -1;

        this.callWindowActive = false;
        this.callOptions = [];
        this.callShapes = [];
        this.CallIsClaim = false;
        this.callTile = null;
        this.callFromSeat = -1;
    }

    private bool TryAddMeld(int seat, Meld meld, string source, int? slot = null, int? from = null)
    {
        if (!this.seatMelds[seat].Record(meld, slot, from)) return false;
        this.Log($"[Meld] seat {seat} {source}: {meld.Type} [{string.Join(" ", meld.Tiles)}]");
        return true;
    }

    private void ResetRound(string why)
    {
        this.pendingAnswer = null;
        this.answeredGeneration = -1;
        this.answeredSignature = null;
        this.answeredCallTile = null;
        this.answeredCallFromSeat = -1;
        this.LastWinnerSeat = -1;
        this.LastScoreDelta = 0;
        this.Settlement = null;
        this.resultWinByRon = null;
        this.resultIsDraw = false;
        this.LastRecap = null;
        this.LastWinScreen = null;
        this.LastWinByRon = false;
        this.RonVictimSeat = -1;
        this.RonTile = null;
        this.discardSinceTurnAdvance = false;
        this.lastDiscardSeat = -1;
        this.lastDiscardTile = null;
        this.discardCounter = 0;
        this.revealedRuns.Clear();
        foreach (var list in this.seatDiscards)
            list.Clear();
        foreach (var list in this.seatDiscardOrder)
            list.Clear();
        foreach (var list in this.seatMelds)
            list.Clear();
        this.eventWallRemaining = 70;
        this.lastOpponentDiscard = null;
        this.lastOpponentDiscardSeat = -1;
        this.riichiDeclared = false;
        this.ClearCallWindow(why);
        this.Log($"[Round] reset: {why}");
    }

    // "East 3 South Wind" → 3. The hand number is the first integer token.
    public static int? ParseHandNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(token, out var n) && n is >= 1 and <= 4)
                return n;
        return null;
    }

    // Any frame carrying ten or more tile icons after the hand ended is a hand reveal
    // candidate; keep it with its event type and slot indices so the layout can be read
    // off the first live draw (docs/DEFENSE_PLAN.md §3.6) and calibration can use it later.
    private void CaptureRevealRun(AtkFrame f)
    {
        var tiles = new List<(int Index, Tile Tile)>();
        for (var i = 1; i < f.Copied; i++)
            if (f.IsInt(i) && TileHelpers.TryTileFromIconId(f.Int(i), out var t))
                tiles.Add((i, t));
        if (tiles.Count < 10)
            return;
        this.revealedRuns.Add((f.EventType, f.Count, tiles));
        this.Note($"reveal? type={f.EventType} n={f.Count} copied={f.Copied} tiles={tiles.Count}: "
                  + string.Join(" ", tiles.Select(x => $"[{x.Index}]{x.Tile}")));
    }

    private static Wind? WindFromIcon(int icon) => icon switch
    {
        76068 => Wind.East,
        76069 => Wind.South,
        76070 => Wind.West,
        76071 => Wind.North,
        _ => null,
    };

    private void NoteRefresh(AtkFrame f)
    {
        var sb = new System.Text.StringBuilder(160);
        sb.Append("evt type=").Append(f.EventType).Append(" n=").Append(f.Count);
        for (var i = 1; i < Math.Min(f.Copied, 9); i++)
        {
            sb.Append(" [").Append(i).Append("]=");
            if (f.IsInt(i))
            {
                sb.Append(f.Int(i));
                if (TileHelpers.TryTileFromIconId(f.Int(i), out var hint))
                    sb.Append('→').Append(hint);
            }
            else
            {
                sb.Append('"').Append(f.Str(i)).Append('"');
            }
        }

        this.Note(sb.ToString());
    }

    private void Log(string message)
    {
        this.logInfo?.Invoke(message);
        this.Note(message);
    }
}

// What the game printed on its own win screen. Points is the total the winner collects
// (-1 when the frame did not carry it), before any honba bonus.
public sealed record WinScreen(int Fu, int Han, string Limit, int Points, bool WinnerIsDealer, bool Tsumo);
