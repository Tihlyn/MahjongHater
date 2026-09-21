using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

// Fukuchi's danger diagram letters, derived from the probability (S is genbutsu only).
public enum DangerRank
{
    S,
    APlus,
    B,
    C,
    D,
    E,
    F,
}

// One tile against one seat, assuming that seat is tenpai.
public readonly record struct DangerEstimate(double Probability, DangerRank Rank, string Class, string Why)
{
    public static DangerEstimate Genbutsu(string why) => new(0, DangerRank.S, "genbutsu", why);
}

// What the danger model knows about one opponent for one snapshot: which tiles are
// genbutsu (own discards, anything discarded after its riichi, the tile just passed to us),
// its riichi declaration tile, early discards, how many suji are still live, and its winds.
public sealed class SeatThreatView
{
    public required int Seat { get; init; }

    public required bool Riichi { get; init; }

    public Tile? RiichiTile { get; init; }

    public required bool[] Genbutsu { get; init; }        // by tile kind index

    public required bool[] EarlyDiscards { get; init; }   // kinds among the seat's first six discards

    public required int LiveSuji { get; init; }

    public required Wind SeatWind { get; init; }

    public required Wind RoundWind { get; init; }

    public int OpenMelds { get; init; }

    public bool IsGenbutsu(Tile tile) => this.Genbutsu[TileHelpers.ToIndex(tile)];

    public bool IsRiichiTile(int kind) => this.RiichiTile is { } r && TileHelpers.ToIndex(r) == kind;
}

// Table-driven tile danger (docs/DEFENSE_PLAN.md §3.1). Base rates are measured deal-in
// rates against a riichi by tile class and live-suji count (DealInRateTable); kabe,
// early-outside, dora and visible-copy effects are multipliers from the same literature.
public sealed class TileDangerModel
{
    private readonly DealInRateTable table;
    private readonly PolicyWeights weights;

    public TileDangerModel(DealInRateTable? table = null, PolicyWeights? weights = null)
    {
        this.table = table ?? DealInRateTable.Default;
        this.weights = weights ?? PolicyWeights.Default;
    }

    // visible[kind] counts every copy we can see (our hand, all discards, all melds, indicators).
    public DangerEstimate Estimate(Tile tile, SeatThreatView seat, int[] visible, IReadOnlyList<Tile> doraIndicators)
    {
        var kind = TileHelpers.ToIndex(tile);
        var kokushiPossible = KokushiPossible(visible, kind);
        if (seat.Genbutsu[kind])
            return DangerEstimate.Genbutsu(seat.Riichi ? $"genbutsu vs seat {seat.Seat} (riichi)" : $"in seat {seat.Seat}'s discards");

        // Copies other than the one we are about to throw (the table's classes already
        // average over the usual one-in-hand).
        var others = Math.Clamp(visible[kind] - 1, 0, 3);
        var m = this.table.Multipliers;
        var doraKinds = doraIndicators.Select(i => TileHelpers.ToIndex(DoraOf(i))).ToArray();

        if (tile.IsHonor)
        {
            var cls = HonorClassFor(tile, seat);
            var isDora = doraKinds.Contains(kind);
            var p = this.table.Honor(cls, others, isDora, seat.LiveSuji);
            if (others >= 3)
                p = kokushiPossible ? Math.Max(p, m.HonorKokushiFloor) : 0;
            var why = $"{cls.ToString().ToLowerInvariant()} honor, {others} visible{(isDora ? ", dora" : string.Empty)}";
            return new DangerEstimate(Clamp(p), RankOf(p), $"honor/{cls}", why);
        }

        var n = tile.Number;
        var (lower, lowerRiichi) = this.SideOf(tile, seat, visible, lower: true);
        var (upper, upperRiichi) = this.SideOf(tile, seat, visible, lower: false);
        var cls2 = ClassOf(n, lower, upper, lowerRiichi || upperRiichi);
        var rate = this.table.Number(cls2, n, seat.LiveSuji);
        var notes = new List<string> { $"{Describe(cls2)} {n}" };

        // Live sides that are almost blockaded (one chance / double one chance).
        var factors = new List<double>();
        foreach (var side in new[] { lower, upper })
        {
            if (side.Status != SideStatus.Live)
                continue;
            factors.Add(side.ThreeVisible switch
            {
                >= 2 => m.DoubleOneChance,
                1 => seat.LiveSuji <= m.OneChanceLateLiveSuji ? m.OneChanceLate : m.OneChanceEarly,
                _ => 1.0,
            });
        }

        if (factors.Count > 0 && factors.Average() < 1)
        {
            rate *= factors.Average();
            notes.Add(factors.Min() <= m.DoubleOneChance ? "double one-chance" : "one-chance");
        }

        if ((cls2 is DealInRateTable.NumberClass.NonSuji or DealInRateTable.NumberClass.HalfSuji or DealInRateTable.NumberClass.HalfSujiRiichi)
            && IsOutsideEarlyDiscard(tile, seat))
        {
            rate *= m.EarlyOutside;
            notes.Add("outside an early discard");
        }

        if (doraKinds.Contains(kind))
        {
            rate *= tile.IsTerminal ? m.DoraTerminal : m.DoraTile;
            notes.Add("dora");
        }
        else if (doraKinds.Any(d => d / 9 == kind / 9 && Math.Abs(d - kind) == 1))
        {
            rate *= m.DoraNeighbour;
            notes.Add("next to dora");
        }

        if (others > 0 && m.VisibleCopies[others] < 1)
        {
            rate *= m.VisibleCopies[others];
            notes.Add($"{others} visible");
        }

        return new DangerEstimate(Clamp(rate), RankOf(rate), Describe(cls2), string.Join(", ", notes));
    }

    // Build the per-seat views for a snapshot. `lastGlobal` (the most recent discard on
    // the table, normally kamicha's) is safe against everyone this turn by furiten.
    public static SeatThreatView[] BuildViews(StateSnapshot state, int[] visible)
    {
        var views = new SeatThreatView[4];
        var ordersKnown = state.Seats.All(s => s.DiscardOrder.Count == s.Discards.Count);
        var anyCall = state.Seats.Any(s => s.Melds.Any(mm => mm.IsOpen)) || state.IsOpen;
        Tile? lastGlobal = null;
        var lastOrder = -1;
        foreach (var s in state.Seats)
            for (var i = 0; i < s.DiscardOrder.Count && i < s.Discards.Count; i++)
                if (s.DiscardOrder[i] > lastOrder)
                {
                    lastOrder = s.DiscardOrder[i];
                    // Our own discard makes nobody furiten; only an opponent's does.
                    lastGlobal = s.Seat == 0 ? null : s.Discards[i];
                }

        if (state.CallTile is { } offered && state.CallFromSeat is >= 1 and <= 3)
            lastGlobal = offered;

        foreach (var seat in state.Seats)
        {
            if (seat.Seat == 0)
                continue;
            var genbutsu = new bool[34];
            foreach (var t in seat.Discards)
                genbutsu[TileHelpers.ToIndex(t)] = true;
            if (lastGlobal is { } lg)
                genbutsu[TileHelpers.ToIndex(lg)] = true;

            if (seat.Riichi)
            {
                if (ordersKnown && seat.RiichiDiscardOrder >= 0)
                {
                    var declared = seat.RiichiDiscardOrder;
                    foreach (var other in state.Seats)
                        for (var i = 0; i < other.DiscardOrder.Count; i++)
                            if (other.DiscardOrder[i] > declared)
                                genbutsu[TileHelpers.ToIndex(other.Discards[i])] = true;
                }
                else if (!anyCall && seat.RiichiDiscardIndex >= 0)
                {
                    // No global order: round indices work only while no seat has called.
                    var declaration = seat.RiichiDiscardIndex * 4 + (seat.Seat - state.DealerSeat + 4) % 4;
                    foreach (var other in state.Seats)
                        for (var i = 0; i < other.Discards.Count; i++)
                            if (i * 4 + (other.Seat - state.DealerSeat + 4) % 4 > declaration)
                                genbutsu[TileHelpers.ToIndex(other.Discards[i])] = true;
                }
            }

            var early = new bool[34];
            foreach (var t in seat.Discards.Take(6))
                early[TileHelpers.ToIndex(t)] = true;

            views[seat.Seat] = new SeatThreatView
            {
                Seat = seat.Seat,
                Riichi = seat.Riichi,
                RiichiTile = seat.Riichi && seat.RiichiDiscardIndex >= 0 && seat.RiichiDiscardIndex < seat.Discards.Count
                    ? seat.Discards[seat.RiichiDiscardIndex] : null,
                Genbutsu = genbutsu,
                EarlyDiscards = early,
                LiveSuji = CountLiveSuji(genbutsu, visible),
                SeatWind = SeatWindOf(seat.Seat, state.DealerSeat),
                RoundWind = state.RoundWind,
                OpenMelds = seat.Melds.Count(mm => mm.IsOpen),
            };
        }

        return views;
    }

    // Kokushi needs one of every terminal/honor; a kind with all four visible rules it out —
    // except the kind we are about to discard, whose fourth copy is the one in our hand.
    public static bool KokushiPossible(int[] visible, int exceptKind = -1)
    {
        foreach (var kind in new[] { 0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33 })
            if (kind != exceptKind && visible[kind] >= 4)
                return false;
        return true;
    }

    public static Wind SeatWindOf(int seat, int dealerSeat) => (Wind)(((seat - dealerSeat + 4) % 4) + 1);

    public static Tile DoraOf(Tile indicator)
    {
        var limit = indicator.Suit == TileSuit.Wind ? 4 : indicator.Suit == TileSuit.Dragon ? 3 : 9;
        return new Tile(indicator.Suit, indicator.Number % limit + 1);
    }

    // 18 suji lines (a, a+3 per suit); a line is dead once either end is genbutsu (furiten)
    // or its protorun cannot exist (a blockade on a+1 or a+2).
    public static int CountLiveSuji(bool[] genbutsu, int[] visible)
    {
        var live = 0;
        for (var suit = 0; suit < 3; suit++)
            for (var a = 1; a <= 6; a++)
            {
                var lo = suit * 9 + a - 1;
                var hi = lo + 3;
                var dead = genbutsu[lo] || genbutsu[hi] || visible[lo + 1] >= 4 || visible[lo + 2] >= 4;
                if (!dead)
                    live++;
            }

        return live;
    }

    public DangerRank RankOf(double p)
    {
        var w = this.weights;
        return p <= 0 ? DangerRank.S
            : p < w.RankAPlusMax ? DangerRank.APlus
            : p < w.RankBMax ? DangerRank.B
            : p < w.RankCMax ? DangerRank.C
            : p < w.RankDMax ? DangerRank.D
            : p < w.RankEMax ? DangerRank.E
            : DangerRank.F;
    }

    private enum SideStatus { None, Live, DeadBySuji, DeadByKabe }

    private readonly record struct Side(SideStatus Status, int ThreeVisible);

    // The ryanmen that catches `tile` from below (n-3/n via protorun n-2,n-1) or above
    // (n/n+3 via n+1,n+2): dead when the far end is genbutsu or a protorun tile is walled.
    private (Side Side, bool ViaRiichiTile) SideOf(Tile tile, SeatThreatView seat, int[] visible, bool lower)
    {
        var n = tile.Number;
        if (lower ? n < 4 : n > 6)
            return (new Side(SideStatus.None, 0), false);
        var baseIndex = TileHelpers.ToIndex(new Tile(tile.Suit, 1));
        int far = lower ? n - 3 : n + 3, p1 = lower ? n - 2 : n + 1, p2 = lower ? n - 1 : n + 2;
        var farKind = baseIndex + far - 1;
        if (seat.Genbutsu[farKind])
            return (new Side(SideStatus.DeadBySuji, 0), seat.IsRiichiTile(farKind));
        var v1 = visible[baseIndex + p1 - 1];
        var v2 = visible[baseIndex + p2 - 1];
        if (v1 >= 4 || v2 >= 4)
            return (new Side(SideStatus.DeadByKabe, 0), false);
        return (new Side(SideStatus.Live, (v1 == 3 ? 1 : 0) + (v2 == 3 ? 1 : 0)), false);
    }

    private static DealInRateTable.NumberClass ClassOf(int n, Side lower, Side upper, bool viaRiichi)
    {
        static bool Dead(Side s) => s.Status is SideStatus.DeadBySuji or SideStatus.DeadByKabe;
        if (n is >= 4 and <= 6)
        {
            var dead = (Dead(lower) ? 1 : 0) + (Dead(upper) ? 1 : 0);
            return dead switch
            {
                2 => viaRiichi ? DealInRateTable.NumberClass.NakasujiRiichi : DealInRateTable.NumberClass.Nakasuji,
                1 => viaRiichi ? DealInRateTable.NumberClass.HalfSujiRiichi : DealInRateTable.NumberClass.HalfSuji,
                _ => DealInRateTable.NumberClass.NonSuji,
            };
        }

        var side = n <= 3 ? upper : lower;
        return Dead(side)
            ? (viaRiichi ? DealInRateTable.NumberClass.RiichiSuji : DealInRateTable.NumberClass.Suji)
            : DealInRateTable.NumberClass.NonSuji;
    }

    private static string Describe(DealInRateTable.NumberClass cls) => cls switch
    {
        DealInRateTable.NumberClass.NonSuji => "non-suji",
        DealInRateTable.NumberClass.Suji => "suji",
        DealInRateTable.NumberClass.RiichiSuji => "riichi-tile suji",
        DealInRateTable.NumberClass.HalfSuji => "half-suji",
        DealInRateTable.NumberClass.HalfSujiRiichi => "half-suji (riichi tile)",
        DealInRateTable.NumberClass.Nakasuji => "nakasuji",
        DealInRateTable.NumberClass.NakasujiRiichi => "nakasuji (riichi tile)",
        _ => cls.ToString(),
    };

    private static DealInRateTable.HonorClass HonorClassFor(Tile tile, SeatThreatView seat)
    {
        if (tile.Suit == TileSuit.Dragon)
            return DealInRateTable.HonorClass.Dragon;
        var wind = (Wind)tile.Number;
        var isSeat = wind == seat.SeatWind;
        var isRound = wind == seat.RoundWind;
        return isSeat && isRound ? DealInRateTable.HonorClass.Double
            : isSeat ? DealInRateTable.HonorClass.Seat
            : isRound ? DealInRateTable.HonorClass.Round
            : DealInRateTable.HonorClass.Guest;
    }

    // An early discard of m makes tiles beyond it (toward the edge, within two) less likely
    // to sit in a live protorun: from 2 we throw the 1, not the 2.
    private static bool IsOutsideEarlyDiscard(Tile tile, SeatThreatView seat)
    {
        var baseIndex = TileHelpers.ToIndex(new Tile(tile.Suit, 1));
        var n = tile.Number;
        for (var m = 1; m <= 9; m++)
        {
            if (!seat.EarlyDiscards[baseIndex + m - 1])
                continue;
            if (m <= 4 && n < m && m - n <= 2)
                return true;
            if (m >= 6 && n > m && n - m <= 2)
                return true;
        }

        return false;
    }

    private static double Clamp(double p) => Math.Clamp(p, 0, 1);
}
