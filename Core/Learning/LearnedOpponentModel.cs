using MahjongHater.Core.Policy;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Learning;

// `useLearnedDanger = false` keeps the measured danger tables and takes only the tenpai head
// from the network: on Phoenix replays the tables discriminate ron tiles slightly better
// while the learned tenpai head is better calibrated (docs/research/EVALUATION_RUNS.md).
public sealed class LearnedOpponentModel(LearnedModel network, PolicyWeights? weights = null, bool useLearnedDanger = true) : IOpponentModel
{
    private readonly OpponentModel fallback = new(weights);
    private readonly TileDangerModel ranks = new(weights: weights);
    private View? view;
    private sealed record View(double[] Tenpai, double[,] Danger, double[,] Points, double[] Value, int Primary);
    public void Update(StateSnapshot state)
    {
        this.fallback.Update(state);
        if (!network.Supports(state) || !state.LayoutHealthy || state.Seats.Any(s => !s.DiscardsVerified))
        { Volatile.Write(ref this.view, null); return; }
        var features = LearningFeatures.Encode(state);
        var output = network.Predict(features);
        var tenpai = new double[4];
        var danger = new double[4, 34];
        var points = new double[4, 34];
        var values = new double[4];
        for (var seat = 1; seat <= 3; seat++)
        {
            tenpai[seat] = state.Seats[seat].Riichi ? 1 : network.Tenpai(output[74 + seat - 1]);
            var probability = 0d;
            for (var kind = 0; kind < 34; kind++)
            {
                var index = (seat - 1) * 34 + kind;
                var c = 8 + seat * 6;
                var safe = features[c * 34 + kind] > 0 || features[(c + 5) * 34 + kind] > 0;
                danger[seat, kind] = safe ? 0 : useLearnedDanger ? network.Wait(output[77 + index]) : this.fallback.Danger(TileHelpers.FromIndex(kind), seat);
                points[seat, kind] = network.Points(output[179 + index]);
                probability += danger[seat, kind];
                values[seat] += danger[seat, kind] * points[seat, kind];
            }
            values[seat] = probability > 0 ? values[seat] / probability : 0;
        }
        var primary = Enumerable.Range(1, 3).OrderByDescending(s => tenpai[s] * values[s]).First();
        Volatile.Write(ref this.view, new View(tenpai, danger, points, values, tenpai[primary] * values[primary] > 0 ? primary : -1));
    }
    private static int Seat(int seat) => seat is >= 1 and <= 3 ? seat : throw new ArgumentOutOfRangeException(nameof(seat));
    public double TenpaiProbability(int seat) => Volatile.Read(ref this.view)?.Tenpai[Seat(seat)] ?? this.fallback.TenpaiProbability(seat);
    public double Danger(Tile tile, int seat) => Volatile.Read(ref this.view)?.Danger[Seat(seat), TileHelpers.ToIndex(tile)] ?? this.fallback.Danger(tile, seat);
    public double Value(int seat) => Volatile.Read(ref this.view)?.Value[Seat(seat)] ?? this.fallback.Value(seat);
    public int PrimaryThreat() => Volatile.Read(ref this.view)?.Primary ?? this.fallback.PrimaryThreat();
    public int LiveSuji(int seat) => this.fallback.LiveSuji(seat);
    public DangerEstimate Explain(Tile tile, int seat)
    {
        if (Volatile.Read(ref this.view) is not { } current) return this.fallback.Explain(tile, seat);
        if (!useLearnedDanger) return this.fallback.Explain(tile, seat);
        var probability = current.Danger[Seat(seat), TileHelpers.ToIndex(tile)];
        return new DangerEstimate(probability, this.ranks.RankOf(probability), probability == 0 ? "known safe" : "learned conditional ron probability", "learned");
    }
    public double ExpectedDealInCost(Tile tile)
    {
        if (Volatile.Read(ref this.view) is not { } current) return this.fallback.ExpectedDealInCost(tile);
        var kind = TileHelpers.ToIndex(tile);
        return Enumerable.Range(1, 3).Sum(s => current.Tenpai[s] * current.Danger[s, kind] * current.Points[s, kind]);
    }
}
