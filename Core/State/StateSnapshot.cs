namespace MahjongHater.Core.State;

// Contract between the reader (Phase 1) and everything downstream (policy, UI).
// Immutable; built on the framework thread, consumed anywhere. See docs/REWORK_PLAN.md.

public enum GamePhase
{
    NotInGame,
    Dealing,
    OurTurn,        // 14 tiles in hand (or 13 + claimed), a discard is expected from us
    OthersTurn,
    CallPrompt,     // pon/chi/kan/ron offered on someone's discard
    SelfDeclare,    // riichi/tsumo/ankan list on our own draw
    RoundEnd,       // win/draw recap
    Unknown,
}

[Flags]
public enum LegalAction
{
    None = 0,
    Discard = 1 << 0,
    Pon = 1 << 1,
    Chi = 1 << 2,
    MinKan = 1 << 3,
    AnKan = 1 << 4,
    ShouMinKan = 1 << 5,
    Riichi = 1 << 6,
    Tsumo = 1 << 7,
    Ron = 1 << 8,
    Pass = 1 << 9,
}

// Seat index is always relative: 0 = us, 1 = shimocha (right), 2 = toimen, 3 = kamicha (left).
public sealed record SeatState(
    int Seat,
    IReadOnlyList<Tile> Discards,
    IReadOnlyList<Meld> Melds,
    bool Riichi,
    int RiichiDiscardIndex,   // index into Discards, -1 when not in riichi
    int Score)
{
    // False when the event-tracked discard list disagrees with the struct's discard
    // count for this seat (events missed, e.g. plugin loaded mid-round).
    public bool DiscardsVerified { get; init; } = true;

    // Preserve native counts even when an event/composition is missing. Never silently
    // turn a missing set into an opponent turn or invent a pon from a tile index.
    public int MeldCount { get; init; } = -1;
    public bool MeldsVerified { get; init; } = true;

    // Struct discard count when known (-1 otherwise); authoritative even when Discards is short.
    public int DiscardCount { get; init; } = -1;

    // Global order of each discard this round (parallel to Discards; empty when the
    // tracker did not see them). Lets "discarded after seat X's riichi" survive calls.
    public IReadOnlyList<int> DiscardOrder { get; init; } = [];

    // Simulator/replay observations retain called discards for furiten/history, but
    // do not count their physical copies again in visible-tile availability.
    public IReadOnlyList<int> ClaimedDiscardIndices { get; init; } = [];

    // Order index of the riichi declaration discard, -1 when unknown or not in riichi.
    public int RiichiDiscardOrder => this.Riichi && this.RiichiDiscardIndex >= 0 && this.RiichiDiscardIndex < this.DiscardOrder.Count
        ? this.DiscardOrder[this.RiichiDiscardIndex] : -1;

    public static SeatState Empty(int seat) => new(seat, [], [], false, -1, 0);
}

public sealed record StateSnapshot(
    long Sequence,                       // monotonic; bumps only when content changed
    GamePhase Phase,
    int RawStateCode,                    // AtkValues[0] as observed, -1 when unavailable
    IReadOnlyList<Tile> Hand,            // closed tiles incl. the draw (13 or 14), red fives preserved
    Tile? DrawnTile,                     // struct slot 13 when populated
    IReadOnlyList<Meld> OurMelds,
    IReadOnlyList<SeatState> Seats,      // exactly 4, index 0 = us
    IReadOnlyList<Tile> DoraIndicators,
    IReadOnlyList<Tile> UraDoraIndicators,
    Wind RoundWind,
    Wind SeatWind,
    int DealerSeat,
    int WallRemaining,
    int Honba,
    int RiichiSticks,
    bool OurRiichi,
    LegalAction Legal,
    Tile? CallTile,                      // the discard being offered on a call prompt
    int CallFromSeat,                    // relative seat that discarded CallTile, -1 if none
    IReadOnlyList<string> CallOptions,   // raw row labels of the call list, in row order
    RulesetOptions Ruleset,
    bool LayoutHealthy)                  // false when the struct read failed self-validation
{
    // Human-readable health notes from the reader (unverified discards, unknown chi
    // compositions, layout shift…). Empty when everything reconciled.
    public IReadOnlyList<string> Notes { get; init; } = [];

    // Hand number inside the round (East 1 → 1), 0 when unknown.
    public int HandNumber { get; init; }

    // Our turn number: discards made + 1 (a claimed-tile turn counts like any other); the
    // struct's count stands in when the tracker missed the discard events.
    public int Turn => Math.Max(this.Us.Discards.Count, this.Us.DiscardCount) + 1;

    // Last scheduled hand of the match (East 4 in a tonpuusen, South 4 in a hanchan);
    // renchan on it still counts. False while the hand number is unknown.
    public bool IsAllLast => this.HandNumber == 4
        && (this.Ruleset.HandsInMatch <= 4 ? this.RoundWind == Wind.East : this.RoundWind == Wind.South);

    // Chi-shape chooser (game state 25): the sequences the game offers, in its button
    // order. Non-empty only after a Chi was accepted and more than one shape fits.
    public IReadOnlyList<Meld> CallShapes { get; init; } = [];

    // True when an ACTUAL prompt event (type-19/23/25) opened the window that carries Legal,
    // false when only the prompt panel's text suggested one. The panel keeps its labels after
    // every prompt closes, so a label-guessed window is not evidence the game is offering
    // anything - while an offer the game really made is authoritative about legality in a way
    // our own hand read is not (docs/research/WIN_OFFERS_2026_09_22.md).
    public bool CallWindowConfirmed { get; init; }

    // We answered a Tsumo/Ron and the game has not settled it yet. This is OUR intent, not
    // the game's state: the phase above still says what the game is showing. Nothing should be
    // decided against a hand we may already have won, so Legal is empty while this is set.
    public bool AwaitingOurWin { get; init; }

    // Any answer we sent that the game has not acted on yet. Distinct from the window being
    // open: a window can close without our answer being the reason, and an answer can sit
    // unacknowledged with the window still up. Neither is inferred from having dispatched.
    public bool AnswerPending { get; init; }

    // Null for offline observations; live reads contain only tiles with an active
    // native discard handler. This includes riichi/kuikae restrictions and animations.
    public IReadOnlyList<Tile>? DiscardableTiles { get; init; }

    public bool CanDiscard(Tile tile) => this.DiscardableTiles == null || this.DiscardableTiles.Contains(tile);

    public SeatState Us => this.Seats[0];

    public bool IsOpen => this.OurMelds.Any(m => m.IsOpen);

    public int ClosedTileCount => this.Hand.Count;

    public bool Can(LegalAction action) => (this.Legal & action) == action;

    // Everything visible outside our closed hand: all discards + everyone's melds + dora indicators.
    // The analyzer expects seen tiles WITHOUT our own melds and WITHOUT dora indicators (it adds
    // those itself) — use SeenForAnalyzer for that.
    public IEnumerable<Tile> SeenForAnalyzer()
    {
        foreach (var seat in this.Seats)
        {
            for (var i = 0; i < seat.Discards.Count; i++)
                if (!seat.ClaimedDiscardIndices.Contains(i))
                    yield return seat.Discards[i];
            if (seat.Seat == 0)
                continue;
            foreach (var m in seat.Melds)
                foreach (var t in m.Tiles)
                    yield return t;
        }
    }

    public static StateSnapshot Empty { get; } = new(
        Sequence: 0,
        Phase: GamePhase.NotInGame,
        RawStateCode: -1,
        Hand: [],
        DrawnTile: null,
        OurMelds: [],
        Seats: [SeatState.Empty(0), SeatState.Empty(1), SeatState.Empty(2), SeatState.Empty(3)],
        DoraIndicators: [],
        UraDoraIndicators: [],
        RoundWind: Wind.East,
        SeatWind: Wind.East,
        DealerSeat: 0,
        WallRemaining: 70,
        Honba: 0,
        RiichiSticks: 0,
        OurRiichi: false,
        Legal: LegalAction.None,
        CallTile: null,
        CallFromSeat: -1,
        CallOptions: [],
        Ruleset: RulesetOptions.Default,
        LayoutHealthy: true);
}
