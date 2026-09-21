using MahjongHater.Core.Policy;
using MahjongHater.Core.Replay;

namespace MahjongHater.Core.Learning;

// Re-fits the logistic tenpai estimate (TenpaiEstimator) on replay ground truth: every
// non-riichi opponent seat at every recorded decision is a labelled row with the same
// features the plugin computes live. Same form and regularisation as tools/fit_tenpai.py,
// but the replay corpus has thousands of times more rows than the live CSV.
public sealed record TenpaiFitResult(int Rows, int Positives, double ShippedLogLoss, double ShippedBrier, double FittedLogLoss, double FittedBrier,
    double Intercept, double PerDiscard, double PerMeld, double EarlyOutside, double LateMiddle, IReadOnlyList<(string Bucket, int N, double Predicted, double Observed)> Reliability)
{
    public string Initializers => string.Join(Environment.NewLine,
        $"TenpaiLogitIntercept = {this.Intercept:F3},",
        $"TenpaiLogitPerDiscard = {this.PerDiscard:F3},",
        $"TenpaiLogitPerMeld = {this.PerMeld:F3},",
        $"TenpaiLogitEarlyOutside = {this.EarlyOutside:F3},",
        $"TenpaiLogitLateMiddle = {this.LateMiddle:F3},");
}

public static class TenpaiFit
{
    public static TenpaiFitResult Run(ReplayCorpus corpus, PolicyWeights shipped, string split = "train", int maxGames = int.MaxValue,
        double l2 = 0.01, int iterations = 4000, double learningRate = 0.05, CancellationToken ct = default)
    {
        var rows = new List<(TenpaiFeatures F, bool Y)>();
        foreach (var index in Enumerable.Range(0, corpus.Count).Where(i => split == "all" || ReplayCorpus.Split(corpus.Manifest.Games[i].Sha256) == split).Take(maxGames))
        {
            ct.ThrowIfCancellationRequested();
            var game = corpus.Read(index);
            foreach (var decision in game.Decisions)
            {
                var state = decision.Observation.Snapshot;
                foreach (var target in decision.OpponentTargets)
                {
                    if (target.Seat is < 1 or > 3) continue;
                    var seat = state.Seats[target.Seat];
                    if (seat.Riichi) continue;
                    rows.Add((TenpaiFeatures.From(seat), target.Tenpai));
                }
            }
        }

        if (rows.Count == 0) throw new InvalidDataException("No non-riichi opponent rows in the selected games.");
        var w = new[] { shipped.TenpaiLogitIntercept, shipped.TenpaiLogitPerDiscard, shipped.TenpaiLogitPerMeld, shipped.TenpaiLogitEarlyOutside, shipped.TenpaiLogitLateMiddle };
        var (shippedLl, shippedBrier) = Score(rows, w, shipped.TenpaiMaxWithoutRiichi);

        // Plain full-batch gradient descent on the (uncapped) logistic log-loss with L2 on
        // the slopes; the features are already on comparable scales.
        var fitted = (double[])w.Clone();
        var n = rows.Count;
        for (var it = 0; it < iterations; it++)
        {
            ct.ThrowIfCancellationRequested();
            var grad = new double[5];
            foreach (var (f, y) in rows)
            {
                var x = X(f);
                var p = Sigmoid(Dot(fitted, x));
                var err = p - (y ? 1 : 0);
                for (var k = 0; k < 5; k++) grad[k] += err * x[k];
            }
            for (var k = 0; k < 5; k++)
            {
                grad[k] /= n;
                if (k > 0) grad[k] += l2 * fitted[k];
                fitted[k] -= learningRate * grad[k];
            }
        }

        var (fittedLl, fittedBrier) = Score(rows, fitted, shipped.TenpaiMaxWithoutRiichi);
        var reliability = new List<(string, int, double, double)>();
        foreach (var (lo, hi) in new[] { (0, 6), (7, 10), (11, 14), (15, 18), (19, 30) })
        {
            var bucket = rows.Where(r => r.F.Discards >= lo && r.F.Discards <= hi).ToList();
            if (bucket.Count == 0) continue;
            reliability.Add(($"discards {lo}-{hi}", bucket.Count, bucket.Average(r => Math.Min(Sigmoid(Dot(fitted, X(r.F))), shipped.TenpaiMaxWithoutRiichi)), bucket.Average(r => r.Y ? 1.0 : 0)));
        }

        return new TenpaiFitResult(rows.Count, rows.Count(r => r.Y), shippedLl, shippedBrier, fittedLl, fittedBrier,
            fitted[0], fitted[1], fitted[2], fitted[3], fitted[4], reliability);
    }

    private static double[] X(TenpaiFeatures f) => [1, f.Discards, f.OpenMelds, f.EarlyOutside, f.LateMiddle];

    private static double Dot(double[] w, double[] x) => w[0] * x[0] + w[1] * x[1] + w[2] * x[2] + w[3] * x[3] + w[4] * x[4];

    private static double Sigmoid(double z) => 1 / (1 + Math.Exp(-z));

    private static (double LogLoss, double Brier) Score(List<(TenpaiFeatures F, bool Y)> rows, double[] w, double cap)
    {
        double ll = 0, brier = 0;
        foreach (var (f, y) in rows)
        {
            var p = Math.Clamp(Math.Min(Sigmoid(Dot(w, X(f))), cap), 1e-4, 1 - 1e-4);
            var t = y ? 1 : 0;
            ll -= t * Math.Log(p) + (1 - t) * Math.Log(1 - p);
            brier += (p - t) * (p - t);
        }
        return (ll / rows.Count, brier / rows.Count);
    }
}
