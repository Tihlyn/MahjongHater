using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

public sealed class OpponentModel : IOpponentModel
{
    private readonly PolicyWeights weights;
    private Model model = new(new double[4], new double[4, 34], new double[4]);

    public OpponentModel(PolicyWeights? weights = null)
    {
        this.weights = weights ?? PolicyWeights.Default;
    }

    public void Update(StateSnapshot state)
    {
        var visible = new int[34];
        foreach (var tile in state.Hand.Concat(state.SeenForAnalyzer())
                     .Concat(state.OurMelds.SelectMany(m => m.Tiles)).Concat(state.DoraIndicators))
            visible[TileHelpers.ToIndex(tile)]++;

        var probabilities = new double[4];
        var dangers = new double[4, 34];
        var values = new double[4];
        foreach (var seat in state.Seats.Where(s => s.Seat is >= 1 and <= 3))
        {
            probabilities[seat.Seat] = TenpaiEstimator.Estimate(seat, this.weights);

            var dora = seat.Melds.SelectMany(m => m.Tiles).Sum(t =>
                state.DoraIndicators.Count(i => TileHelpers.SameKind(DoraFromIndicator(i), t))
                + (t.IsRedFive ? 1 : 0));
            values[seat.Seat] = this.weights.OpponentBaseValue
                + this.weights.OpponentMeldValue * seat.Melds.Count
                + (seat.Riichi ? this.weights.OpponentRiichiValue : 0)
                + this.weights.OpponentDoraValue * dora;
            for (var kind = 0; kind < 34; kind++)
                dangers[seat.Seat, kind] = this.EstimateDanger(TileHelpers.FromIndex(kind), seat, state, visible);
        }

        // Publish the complete table together; queries never see a half-updated model.
        Volatile.Write(ref this.model, new Model(probabilities, dangers, values));
    }

    public double TenpaiProbability(int seat) => Volatile.Read(ref this.model).Probabilities[CheckSeat(seat)];

    public double Danger(Tile tile, int seat) => Volatile.Read(ref this.model).Dangers[CheckSeat(seat), TileHelpers.ToIndex(tile)];

    public double ExpectedDealInCost(Tile tile)
    {
        var current = Volatile.Read(ref this.model);
        var kind = TileHelpers.ToIndex(tile);
        var cost = 0d;
        for (var seat = 1; seat <= 3; seat++)
            cost += current.Probabilities[seat] * current.Dangers[seat, kind] * current.Values[seat];
        return cost;
    }

    private double EstimateDanger(Tile tile, SeatState seat, StateSnapshot state, int[] visible)
    {
        if (seat.Discards.Any(t => TileHelpers.SameKind(t, tile)) || PassedAfterRiichi(tile, seat, state))
            return Math.Clamp(this.weights.GenbutsuDanger, 0, 1);

        if (tile.IsHonor)
            return Math.Clamp(this.weights.HonorDanger * (4 - Math.Min(4, visible[TileHelpers.ToIndex(tile)])) / 4, 0, 1);

        var danger = tile.IsTerminal ? this.weights.TerminalDanger
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
        return Math.Clamp(danger, 0, 1);
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

    private static Tile DoraFromIndicator(Tile tile)
    {
        var limit = tile.Suit == TileSuit.Wind ? 4 : tile.Suit == TileSuit.Dragon ? 3 : 9;
        return new Tile(tile.Suit, tile.Number % limit + 1);
    }

    private static int CheckSeat(int seat) => seat is >= 1 and <= 3
        ? seat : throw new ArgumentOutOfRangeException(nameof(seat));

    private sealed record Model(double[] Probabilities, double[,] Dangers, double[] Values);
}
