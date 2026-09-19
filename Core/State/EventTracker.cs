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
    private readonly List<Meld> melds = [];
    private string? lastMeldSignature;

    private int eventWallRemaining = 70;
    private Tile? lastOpponentDiscard;
    private int lastOpponentDiscardSeat = -1;
    private DateTime lastOpponentDiscardUtc = DateTime.MinValue;

    private bool callWindowActive;
    private List<string> callOptions = [];
    private Tile? callTile;
    private int callFromSeat = -1;
    private string lastPromptSignature = string.Empty;

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

    public IReadOnlyList<Meld> Melds => this.melds;

    public IReadOnlyList<Tile> SeatDiscardsOf(int seat) => this.seatDiscards[seat];

    public bool CallWindowActive => this.callWindowActive;

    public IReadOnlyList<string> CallOptions => this.callOptions;

    public Tile? CallTile => this.callTile;

    public int CallFromSeat => this.callFromSeat;

    // True for a Pon/Chi/Kan/Ron window on an opponent's discard, false for an own-turn
    // Riichi/Tsumo/Kan prompt.
    public bool CallIsClaim { get; private set; }

    public int EventWallRemaining => this.eventWallRemaining;

    public Wind RoundWind => this.trackedRoundWind;

    public Wind? SeatWind => this.eventSeatWind;

    public IReadOnlyList<Tile> EventDoras => this.eventDoras;

    public bool RoundEnded => this.roundEnded;

    public bool RiichiDeclared => this.riichiDeclared;

    public int WinsThisSession { get; private set; }

    public int LossesThisSession { get; private set; }

    public Tile? LastOpponentDiscard => this.lastOpponentDiscard;

    // Manual override until a riichi signal is mapped in the struct (/riichi?declared=).
    public void SetRiichiDeclared(bool declared) => this.riichiDeclared = declared;

    // Best-effort round wind from the addon's visible text (fed by the reader).
    public void HintRoundWind(Wind wind) => this.trackedRoundWind = wind;

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

            case 19: // call window (or another seat's Pon!/Chi! banner — same event)
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
        if (this.TryAddMeld(new Meld(type, [.. tiles], true), "atkType=74"))
            this.ClearCallWindow("meld accepted (atkType=74)");
    }

    // ──────────────────────────────────────── TICK ──────────────────────────────────────────

    // Per frame, after the struct read. promptLabels = visible call-panel button texts.
    public void OnTick(DecodedStruct s, IReadOnlyList<string> promptLabels, DateTime? now = null)
    {
        var utc = now ?? DateTime.UtcNow;
        var closed = s.ClosedTiles;
        var closedAll = s.HandInVisualOrder();

        // 13-14 tiles in the closed slots is only possible with zero melds: a tracked
        // meld contradicting that is stale (round rolled over unnoticed).
        if (this.melds.Count > 0 && closedAll.Count >= 13)
        {
            this.Note($"melds [{string.Join(", ", this.melds)}] contradict {closedAll.Count} closed tiles — clearing");
            this.melds.Clear();
            this.lastMeldSignature = null;
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

        // Meld reconstruction from the closed-hand delta (see MeldInference). The claimed
        // tile is the event's offered tile, else whatever the struct parked in slot 13.
        var drop = this.prevClosedAll.Count - closed.Count;
        if (drop >= 2 && this.prevClosedAll.Count > 0 && this.melds.Count < 4)
        {
            var called = this.callTile ?? this.FreshOpponentDiscard(utc) ?? s.DrawnTile;
            var meld = MeldInference.Infer(this.prevClosedAll, closed, drop == 4 ? null : called)
                       ?? MeldInference.Infer(this.prevClosedAll, closed, null);
            if (meld is not null && this.TryAddMeld(meld, "hand delta"))
                this.ClearCallWindow("meld inferred from hand delta");
        }

        this.prevClosedAll = closedAll;
        this.lastClosed = [.. closed];
        this.lastDrawnTile = s.DrawnTile;

        // Prompt panel labels: edge-triggered — in count=109 mode the panel keeps stale
        // labels on screen after a window closes (reference doc, "Call window lifecycle").
        var labels = promptLabels.Where(l => l is "Chi" or "Pon" or "Kan" or "Ron" or "Riichi" or "Tsumo").ToList();
        var signature = string.Join(",", labels);
        if (labels.Count == 0)
        {
            if (this.callWindowActive)
                this.ClearCallWindow("prompt labels gone");
        }
        else if (signature != this.lastPromptSignature)
        {
            var offered = this.FreshOpponentDiscard(utc) ?? this.callTile;
            this.OpenCallWindow(labels, offered, "label edge");
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
        var expected = HandTracking.MaxClosedTiles(this.melds.Count) - 1;
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
            && !HandTracking.HasAnyLegalCall(this.lastClosed, tile, this.melds.Count, allowChi: this.lastOpponentDiscardSeat is 3 or -1))
        {
            this.Note($"call window ({source}) for {tile}: no legal local call — not ours; ignoring");
            this.ClearCallWindow("not our window");
            return;
        }

        this.callWindowActive = true;
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
        this.callWindowActive = false;
        this.callOptions = [];
        this.CallIsClaim = false;
        this.callTile = null;
        this.callFromSeat = -1;
    }

    private bool TryAddMeld(Meld meld, string source)
    {
        var signature = MeldInference.Signature(meld);
        if (signature == this.lastMeldSignature || this.melds.Count >= 4)
            return false;
        this.lastMeldSignature = signature;
        this.melds.Add(meld);
        this.Log($"[Meld] {source}: {meld.Type} [{string.Join(" ", meld.Tiles)}]");
        return true;
    }

    private void ResetRound(string why)
    {
        foreach (var list in this.seatDiscards)
            list.Clear();
        this.melds.Clear();
        this.lastMeldSignature = null;
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
