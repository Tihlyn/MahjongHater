namespace MahjongHater.Core.State;

// Contract between the reader (Phase 1) and everything downstream (policy, UI, debug API).
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

    // Struct discard count when known (-1 otherwise); authoritative even when Discards is short.
    public int DiscardCount { get; init; } = -1;

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
            foreach (var t in seat.Discards)
                yield return t;
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
