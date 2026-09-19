namespace MahjongHater.Core.State;

// The minimum of the AtkValues event logic the struct read does not cover: per-seat
// discards (type-8), call windows (type-19 + panel labels), meld reconstruction
// (hand delta / atkType=74 payload), round boundaries (type-21/29/32 + struct discard
// counts), winds, doras and session stats. Event meanings: docs/EMJ_ADDON_REFERENCE.md
// "Event model". No Dalamud types; fed by EmjStateReader, replayable in tests.
public sealed class EventTracker
{
    private const int DebugRingCap = 600;

    private readonly Action<string>? logInfo;
    private readonly Queue<string> debugRing = new(DebugRingCap);

    private readonly List<Tile>[] seatDiscards = [[], [], [], []];
    // Meld compositions per relative seat from type-13 (all seats) / atkType=74 (us);
    // the struct holds counts and pon tiles but not chi tiles or red fives.
    private readonly List<Meld>[] seatMelds = [[], [], [], []];
    private readonly string?[] lastMeldSignature = new string?[4];
    private readonly Wind?[] seatWinds = new Wind?[4];

    private int eventWallRemaining = 70;
    private Tile? lastOpponentDiscard;
    private int lastOpponentDiscardSeat = -1;
    private DateTime lastOpponentDiscardUtc = DateTime.MinValue;

    private bool callWindowActive;
    private List<string> callOptions = [];
    private Tile? callTile;
    private int callFromSeat = -1;
    private string lastPromptSignature = string.Empty;
    private bool callWindowFromLabels;
    // Options of the window the operator already answered: the game echoes the selection
    // as a type-19 with the same labels (verified live 2026-09-19), which must not re-open it.
    private string? answeredSignature;

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

    public IReadOnlyList<Meld> Melds => this.seatMelds[0];

    public IReadOnlyList<Meld> SeatMeldsOf(int seat) => this.seatMelds[seat];

    public IReadOnlyList<Tile> SeatDiscardsOf(int seat) => this.seatDiscards[seat];

    // Relative seat showing "East" in the wind texts, -1 when unknown.
    public int DealerSeat => Array.IndexOf(this.seatWinds, Wind.East);

    public bool CallWindowActive => this.callWindowActive;

    public IReadOnlyList<string> CallOptions => this.callOptions;

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

    // True from an answered Tsumo/Ron until the win screen, so nothing is decided in between.
    public bool WinDeclared { get; private set; }

    // The operator answered the open window (list row clicked). Clears it so the next
    // snapshot moves on (a riichi needs its discard right after), and ignores the echo.
    public void MarkCallAnswered(bool isWin)
    {
        if (!this.callWindowActive)
            return;
        this.answeredSignature = string.Join(",", this.callOptions);
        this.WinDeclared |= isWin;
        this.ClearCallWindow("answered by operator");
    }

    public bool RiichiDeclared => this.riichiDeclared;

    public int WinsThisSession { get; private set; }

    public int LossesThisSession { get; private set; }

    public Tile? LastOpponentDiscard => this.lastOpponentDiscard;

    // Manual override until a riichi signal is mapped in the struct (/riichi?declared=).
    public void SetRiichiDeclared(bool declared) => this.riichiDeclared = declared;

    // Best-effort round wind from the addon's visible text (fed by the reader).
    public void HintRoundWind(Wind wind) => this.trackedRoundWind = wind;

    // Seat winds from the four score-panel texts (nodes.seatWindTexts), null = unreadable.
    public void HintSeatWinds(IReadOnlyList<Wind?> winds)
    {
        for (var i = 0; i < 4 && i < winds.Count; i++)
            if (winds[i] is { } w)
                this.seatWinds[i] = w;
    }

    // ──────────────────────────────────────── EVENTS ────────────────────────────────────────

    public void OnRefresh(AtkFrame f, DateTime? now = null)
    {
        var utc = now ?? DateTime.UtcNow;
        var type = f.EventType;
        this.NoteRefresh(f);

        // Seat wind icon rides on [2] of stable-state events (never 5/6/8, which reuse [2]).
        if (type is not (5 or 6 or 8) && f.IsInt(2) && WindFromIcon(f.Int(2)) is { } sw)
            this.eventSeatWind = sw;

        switch (type)
        {
            case 5: // turn advance: [1]=wall remaining, [2]=seat that draws next
                if (f.Int(1) is > 0 and <= 70)
                    this.eventWallRemaining = f.Int(1);
                this.ClearCallWindow("turn advance (type-5)");
                break;

            case 8: // THE discard event: [1]=seat, [2]=real tile icon, every seat
            {
                var seat = f.Int(1);
                if (seat is < 0 or > 3 || !TileHelpers.TryTileFromIconId(f.Int(2), out var tile))
                    break;
                this.seatDiscards[seat].Add(tile);
                if (seat != 0)
                {
                    this.lastOpponentDiscard = tile;
                    this.lastOpponentDiscardSeat = seat;
                    this.lastOpponentDiscardUtc = utc;
                    // A fresh opponent discard can open a window whose labels are identical
                    // to the previous one (no edge) — let the next label scan re-evaluate.
                    this.lastPromptSignature = string.Empty;
                }

                this.roundEnded = false;
                this.ClearCallWindow($"discard (type-8) seat={seat} {tile}");
                this.Note($"discard seat={seat} {tile}");
                break;
            }

            case 13: // meld composition: [1]=caller, [3]=4 pon/5 chi, [5]=from-direction,
                     // [6]=tile index or 255 (chi), [7]=tile count, [8..10]=icons (chi: claimed first)
            {
                var seat = f.Int(1);
                if (seat is < 0 or > 3)
                    break;
                var tiles = new List<Tile>(4);
                var count = Math.Clamp(f.Int(7), 0, 4);
                for (var i = 0; i < count; i++)
                {
                    if (!f.IsInt(8 + i) || !TileHelpers.TryTileFromIconId(f.Int(8 + i), out var t))
                        break;
                    tiles.Add(t);
                }

                if (tiles.Count < 3)
                    break;
                var from = f.Int(5);
                var meldType = tiles.Count == 4 ? (from == 0 ? MeldType.Ankan : MeldType.Daiminkan)
                    : f.Int(3) == 5 || !TileHelpers.SameKind(tiles[0], tiles[1]) ? MeldType.Chi
                    : MeldType.Pon;
                var ordered = meldType == MeldType.Chi ? tiles.OrderBy(TileHelpers.Normalize).ToArray() : tiles.ToArray();
                if (this.TryAddMeld(seat, new Meld(meldType, ordered, meldType != MeldType.Ankan), $"type-13 from={from}"))
                    this.ClearCallWindow($"meld (type-13) seat={seat}");
                break;
            }

            case 19: // call window (or another seat's Pon!/Chi! banner — same event)
            case 23: // call options (mirrors 19's [6..8])
            {
                var opts = new List<string>(3);
                for (var i = 6; i <= 8; i++)
                {
                    var s = f.Str(i);
                    if (!string.IsNullOrEmpty(s) && !s.Equals("Pass", StringComparison.OrdinalIgnoreCase))
                        opts.Add(s);
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
                this.OpenCallWindow(opts, offered, "type-19");
                break;
            }

            case 21: // new deal (Layout 1, seat 0, after a round end) or post-meld refresh
                if (f.Int(2) == 0 && f.Int(14) == 0 && this.roundEnded)
                {
                    this.ResetRound("new deal (type-21)");
                    this.roundEnded = false;
                }

                break;

            case 29: // post-round score delta: [1]=seat-0 delta ×100
            {
                this.roundEnded = true;
                this.WinDeclared = false;
                this.ClearCallWindow("score (type-29)");
                var delta = f.Int(1) * 100;
                if (delta >= 100)
                    this.WinsThisSession++;
                else if (delta <= -100)
                    this.LossesThisSession++;
                break;
            }

            case 32: // win screen: [2]="East 3 South Wind"
            {
                this.roundEnded = true;
                this.WinDeclared = false;
                this.ClearCallWindow("win screen (type-32)");
                var round = f.Str(2) ?? string.Empty;
                if (round.StartsWith("East", StringComparison.OrdinalIgnoreCase)) this.trackedRoundWind = Wind.East;
                else if (round.StartsWith("South", StringComparison.OrdinalIgnoreCase)) this.trackedRoundWind = Wind.South;
                else if (round.StartsWith("West", StringComparison.OrdinalIgnoreCase)) this.trackedRoundWind = Wind.West;
                else if (round.StartsWith("North", StringComparison.OrdinalIgnoreCase)) this.trackedRoundWind = Wind.North;
                break;
            }
        }
    }

    // PostReceiveEvent atkType=74: meld accepted, [8..11] = meld tile icons. Fires
    // constantly without payload (noise) and repeatedly WITH the same payload — dedupe.
    public void OnReceiveEvent(int atkType, AtkFrame f)
    {
        if (atkType != 74)
            return;

        var tiles = new List<Tile>(4);
        for (var i = 8; i < 12; i++)
        {
            if (!f.IsInt(i) || !TileHelpers.TryTileFromIconId(f.Int(i), out var t))
                break;
            tiles.Add(t);
        }

        if (tiles.Count < 3)
            return;

        var type = tiles.Count == 4 ? MeldType.Daiminkan
            : TileHelpers.SameKind(tiles[0], tiles[1]) ? MeldType.Pon
            : MeldType.Chi;
        if (this.TryAddMeld(0, new Meld(type, [.. tiles], true), "atkType=74"))
            this.ClearCallWindow("meld accepted (atkType=74)");
    }

    // ──────────────────────────────────────── TICK ──────────────────────────────────────────

    // Per frame, after the struct read. promptLabels = visible call-panel button texts.
    public void OnTick(DecodedStruct s, IReadOnlyList<string> promptLabels, DateTime? now = null)
    {
        var utc = now ?? DateTime.UtcNow;
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
            if (structCount is { } n ? list.Count > n : seat == 0 && list.Count > 0 && closedAll.Count >= 13)
            {
                this.Note($"seat {seat} melds [{string.Join(", ", list)}] exceed the struct ({structCount?.ToString() ?? "13+ closed"}) — trimming");
                list.RemoveRange(structCount ?? 0, list.Count - (structCount ?? 0));
                this.lastMeldSignature[seat] = list.Count > 0 ? MeldInference.Signature(list[^1]) : null;
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
            if (meld is not null && this.TryAddMeld(0, meld, "hand delta"))
                this.ClearCallWindow("meld inferred from hand delta");
        }

        this.prevClosedAll = closedAll;
        this.lastClosed = [.. closed];
        this.lastDrawnTile = s.DrawnTile;

        // Prompt panel labels are only a fallback for a missed type-19/23: the panel, its
        // list rows and their texts all persist unchanged after a window closes (verified
        // live 2026-09-19, both open and closed trees identical), so they can never clear a
        // window — closing belongs to the events (type-5 draw, type-8 discard, type-13/74
        // meld, 29/32). A prompt also has no auto-pass timer here (one sat open 4 minutes),
        // so no time-based clear either. The edge only opens when a fresh opponent discard
        // names the tile; otherwise a stale "Pon" at cold start would become a phantom call.
        var labels = promptLabels.Select(l => l.TrimEnd('!')).Where(l => l is "Chi" or "Pon" or "Kan" or "Ron" or "Riichi" or "Tsumo").ToList();
        var signature = string.Join(",", labels);
        if (labels.Count == 0)
        {
            if (this.callWindowActive && this.callWindowFromLabels)
                this.ClearCallWindow("prompt labels gone");
        }
        else if (signature != this.lastPromptSignature && !this.callWindowActive)
        {
            // A self-declare window only exists on our draw (14 - 3*melds closed); the panel
            // keeps "Tsumo"/"Riichi" texts across the next deal (verified live 2026-09-19).
            var offered = this.FreshOpponentDiscard(utc);
            var selfDeclare = labels.All(l => l is "Riichi" or "Tsumo" or "Kan")
                              && this.prevClosedAll.Count == HandTracking.MaxClosedTiles(this.seatMelds[0].Count);
            if (offered is not null || selfDeclare)
                this.OpenCallWindow(labels, offered ?? this.callTile, "label edge");
        }

        this.lastPromptSignature = signature;
    }

    public void Reset()
    {
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

    // ──────────────────────────────────────── DEBUG ─────────────────────────────────────────

    public List<string> DebugEvents(int tail) => this.debugRing.TakeLast(Math.Clamp(tail, 1, DebugRingCap)).ToList();

    public void Note(string message)
    {
        if (this.debugRing.Count >= DebugRingCap)
            this.debugRing.Dequeue();
        this.debugRing.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {message}");
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
    private void OpenCallWindow(List<string> options, Tile? offered, string source)
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
            this.Note($"call window ({source}) for {tile}: no legal local call — not ours; ignoring");
            this.ClearCallWindow("not our window");
            return;
        }

        this.callWindowActive = true;
        this.callWindowFromLabels = source == "label edge";
        this.callOptions = options;
        this.CallIsClaim = isClaim;
        this.callTile = isClaim ? candidate : null;
        this.callFromSeat = isClaim && offered is not null ? this.lastOpponentDiscardSeat : -1;
        this.Note($"call window ({source}): [{string.Join(",", options)}] tile={this.callTile?.ToString() ?? "-"} claim={isClaim}");
    }

    private void ClearCallWindow(string why)
    {
        if (this.callWindowActive)
            this.Note($"call window cleared: {why}");
        if (why != "answered by operator")
            this.answeredSignature = null;
        this.callWindowActive = false;
        this.callWindowFromLabels = false;
        this.callOptions = [];
        this.CallIsClaim = false;
        this.callTile = null;
        this.callFromSeat = -1;
    }

    private bool TryAddMeld(int seat, Meld meld, string source)
    {
        var signature = MeldInference.Signature(meld);
        var list = this.seatMelds[seat];
        if (signature == this.lastMeldSignature[seat] || list.Count >= 4)
            return false;
        this.lastMeldSignature[seat] = signature;
        list.Add(meld);
        this.Log($"[Meld] seat {seat} {source}: {meld.Type} [{string.Join(" ", meld.Tiles)}]");
        return true;
    }

    private void ResetRound(string why)
    {
        this.WinDeclared = false;
        this.answeredSignature = null;
        foreach (var list in this.seatDiscards)
            list.Clear();
        foreach (var list in this.seatMelds)
            list.Clear();
        Array.Clear(this.lastMeldSignature);
        this.eventWallRemaining = 70;
        this.lastOpponentDiscard = null;
        this.lastOpponentDiscardSeat = -1;
        this.riichiDeclared = false;
        this.ClearCallWindow(why);
        this.lastPromptSignature = string.Empty;
        this.Log($"[Round] reset: {why}");
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
