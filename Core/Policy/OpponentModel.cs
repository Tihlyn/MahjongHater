using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

public sealed class OpponentModel : IOpponentModel
{
    private readonly PolicyWeights weights;
    private readonly TileDangerModel danger;
    private Model model = new(new double[4], new DangerEstimate[4, 34], new double[4], new bool[4], -1, [-1, -1, -1, -1]);

    public OpponentModel(PolicyWeights? weights = null, TileDangerModel? danger = null)
    {
        this.weights = weights ?? PolicyWeights.Default;
        this.danger = danger ?? new TileDangerModel(weights: this.weights);
    }

    public void Update(StateSnapshot state)
    {
        var visible = new int[34];
        foreach (var tile in state.Hand.Concat(state.SeenForAnalyzer())
                     .Concat(state.OurMelds.SelectMany(m => m.Tiles)).Concat(state.DoraIndicators))
            visible[TileHelpers.ToIndex(tile)]++;

        var probabilities = new double[4];
        var dangers = new DangerEstimate[4, 34];
        var values = new double[4];
        var riichi = new bool[4];
        var views = this.weights.DefenseModel == DefenseModel.V2 ? TileDangerModel.BuildViews(state, visible) : null;
        foreach (var seat in state.Seats.Where(s => s.Seat is >= 1 and <= 3))
        {
            probabilities[seat.Seat] = TenpaiEstimator.Estimate(seat, this.weights);
            riichi[seat.Seat] = seat.Riichi;

            if (views is not null)
            {
                values[seat.Seat] = ThreatValue.Points(seat, state, this.weights);
            }
            else
            {
                var dora = seat.Melds.SelectMany(m => m.Tiles).Sum(t =>
                    state.DoraIndicators.Count(i => TileHelpers.SameKind(TileDangerModel.DoraOf(i), t))
                    + (t.IsRedFive ? 1 : 0));
                values[seat.Seat] = this.weights.OpponentBaseValue
                    + this.weights.OpponentMeldValue * seat.Melds.Count
                    + (seat.Riichi ? this.weights.OpponentRiichiValue : 0)
                    + this.weights.OpponentDoraValue * dora;
            }
            for (var kind = 0; kind < 34; kind++)
            {
                var tile = TileHelpers.FromIndex(kind);
                dangers[seat.Seat, kind] = views is not null
                    ? this.danger.Estimate(tile, views[seat.Seat], visible, state.DoraIndicators)
                    : this.LegacyDanger(tile, seat, state, visible);
            }
        }

        // The seat to defend against first: a riichi (the dealer's first), else the
        // highest tenpai-weighted value once it clears the threat floor.
        var primary = -1;
        var best = 0d;
        for (var seat = 1; seat <= 3; seat++)
        {
            var weight = (riichi[seat] ? 10.0 : probabilities[seat] >= this.weights.ThreatTenpaiFloor ? probabilities[seat] : 0)
                         * values[seat] * (seat == state.DealerSeat ? 1.4 : 1);
            if (weight > best)
            {
                best = weight;
                primary = seat;
            }
        }

        var liveSuji = new[] { -1, views?[1]?.LiveSuji ?? -1, views?[2]?.LiveSuji ?? -1, views?[3]?.LiveSuji ?? -1 };
        // Publish the complete table together; queries never see a half-updated model.
        Volatile.Write(ref this.model, new Model(probabilities, dangers, values, riichi, primary, liveSuji));
    }

    public double TenpaiProbability(int seat) => Volatile.Read(ref this.model).Probabilities[CheckSeat(seat)];

    public double Danger(Tile tile, int seat) => Volatile.Read(ref this.model).Dangers[CheckSeat(seat), TileHelpers.ToIndex(tile)].Probability;

    public DangerEstimate Explain(Tile tile, int seat) => Volatile.Read(ref this.model).Dangers[CheckSeat(seat), TileHelpers.ToIndex(tile)];

    public int PrimaryThreat() => Volatile.Read(ref this.model).PrimaryThreat;

    public double Value(int seat) => Volatile.Read(ref this.model).Values[CheckSeat(seat)];

    public int LiveSuji(int seat) => Volatile.Read(ref this.model).LiveSuji[CheckSeat(seat)];

    public double ExpectedDealInCost(Tile tile)
    {
        var current = Volatile.Read(ref this.model);
        var kind = TileHelpers.ToIndex(tile);
        var cost = 0d;
        for (var seat = 1; seat <= 3; seat++)
            cost += current.Probabilities[seat] * current.Dangers[seat, kind].Probability * current.Values[seat];
        return cost;
    }

    // v1.3 constants (kept for A/B runs behind DefenseModel.Legacy): flat class rates,
    // a single suji discount, kabe only as a full blockade.
    private DangerEstimate LegacyDanger(Tile tile, SeatState seat, StateSnapshot state, int[] visible)
    {
        if (seat.Discards.Any(t => TileHelpers.SameKind(t, tile)) || PassedAfterRiichi(tile, seat, state))
            return new DangerEstimate(Math.Clamp(this.weights.GenbutsuDanger, 0, 1), DangerRank.S, "genbutsu", "legacy");

        double danger;
        if (tile.IsHonor)
        {
            danger = this.weights.HonorDanger * (4 - Math.Min(4, visible[TileHelpers.ToIndex(tile)])) / 4;
            return new DangerEstimate(Math.Clamp(danger, 0, 1), this.danger.RankOf(danger), "honor", "legacy");
        }

        danger = tile.IsTerminal ? this.weights.TerminalDanger
            : tile.Number is 2 or 8 ? this.weights.EdgeDanger : this.weights.MiddleDanger;
        bool Discarded(int number) => seat.Discards.Any(t => t.Suit == tile.Suit && t.Number == number);
        var suji = tile.Number <= 3 ? Discarded(tile.Number + 3)
            : tile.Number >= 7 ? Discarded(tile.Number - 3)
            : Discarded(tile.Number - 3) && Discarded(tile.Number + 3);
        if (suji)
            danger *= this.weights.SujiDiscount;
        if ((tile.Number > 1 && visible[TileHelpers.ToIndex(new Tile(tile.Suit, tile.Number - 1))] >= 4)
            || (tile.Number < 9 && visible[TileHelpers.ToIndex(new Tile(tile.Suit, tile.Number + 1))] >= 4))
            danger *= this.weights.KabeDiscount;
        return new DangerEstimate(Math.Clamp(danger, 0, 1), this.danger.RankOf(danger), suji ? "suji" : "non-suji", "legacy");
    }

    private static bool PassedAfterRiichi(Tile tile, SeatState seat, StateSnapshot state)
    {
        if (!seat.Riichi)
            return false;
        if (state.CallFromSeat is >= 0 and <= 3 && state.CallFromSeat != seat.Seat
            && state.CallTile is { } latest && TileHelpers.SameKind(latest, tile))
            return true;
        // No global discard timestamps are available. Round indices work only before calls skip turns.
        if (seat.RiichiDiscardIndex < 0 || state.Seats.Any(s => s.Melds.Any(m => m.IsOpen)) || state.IsOpen)
            return false;
        var declaration = seat.RiichiDiscardIndex * 4 + (seat.Seat - state.DealerSeat + 4) % 4;
        return state.Seats.Any(s => s.Discards.Where((_, index) =>
                index * 4 + (s.Seat - state.DealerSeat + 4) % 4 > declaration)
            .Any(t => TileHelpers.SameKind(t, tile)));
    }

    private static int CheckSeat(int seat) => seat is >= 1 and <= 3
        ? seat : throw new ArgumentOutOfRangeException(nameof(seat));

    private sealed record Model(double[] Probabilities, DangerEstimate[,] Dangers, double[] Values, bool[] Riichi, int PrimaryThreat, int[] LiveSuji);
}
