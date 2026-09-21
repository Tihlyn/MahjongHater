using System.Text.Json;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Replay;
using MahjongHater.Core.Simulation;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Learning;

// Held-out evaluation of the runtime policies and opponent models against human replays
// (docs/research/EXPERIMENTAL_ASSESSMENT.md, step 1). For every recorded decision the
// policies see exactly the public observation the human saw; the hidden targets are used
// only to score them. Two families of numbers:
//   * agreement: did the policy choose the human's action (by decision category), and how
//     often did the policy's own discard actually deal into a tenpai opponent versus the
//     human's — the defensive number that matters;
//   * calibration: tenpai probability and conditional ron probability of the heuristic
//     estimators versus the learned heads on the same rows (Brier, log-loss, ECE).
public sealed record EvaluationOptions
{
    public string Split { get; init; } = "test";       // train | validation | test | all
    public int MaxGames { get; init; } = int.MaxValue;
    public int Threads { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);
    public bool IncludeLegacy { get; init; } = true;
}

public sealed record AgreementCell(int Decisions, int Agree, int Unmapped, int HumanDealIns, int PolicyDealIns,
    double HumanDealInPoints, double PolicyDealInPoints, int SafeChoices, int HumanSafeChoices)
{
    public double Agreement => this.Decisions == 0 ? 0 : (double)this.Agree / this.Decisions;
    public double HumanDealInRate => this.Decisions == 0 ? 0 : (double)this.HumanDealIns / this.Decisions;
    public double PolicyDealInRate => this.Decisions == 0 ? 0 : (double)this.PolicyDealIns / this.Decisions;
}

public sealed record CalibrationCell(int Count, int Positives, double Brier, double LogLoss, double Ece, double MeanPredicted)
{
    public double BaseRate => this.Count == 0 ? 0 : (double)this.Positives / this.Count;
}

public sealed record EvaluationReport(
    string Corpus, string Split, int Games, int Decisions, int Skipped, string? Model,
    Dictionary<string, Dictionary<string, AgreementCell>> Agreement,     // policy -> category -> cell
    Dictionary<string, Dictionary<string, CalibrationCell>> Calibration, // estimator -> view -> cell
    Dictionary<string, double> Seconds);

public static class LearningEvaluation
{
    public const string Heuristic = "heuristic";
    public const string HeuristicEv = "heuristic-ev";
    public const string Legacy = "legacy";
    public const string Learned = "learned";
    public const string Hybrid = "hybrid";
    public const string HybridTenpai = "hybrid-tenpai";   // learned tenpai head, table danger
    public const string Guarded = "learned-guarded";       // imitation ordering under the danger budget

    public static EvaluationReport Run(ReplayCorpus corpus, LearnedModel? model, PolicyWeights weights, EvaluationOptions options,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var games = Enumerable.Range(0, corpus.Count)
            .Where(i => options.Split == "all" || ReplayCorpus.Split(corpus.Manifest.Games[i].Sha256) == options.Split)
            .Take(options.MaxGames).ToArray();
        var policies = new List<string> { Heuristic, HeuristicEv };
        if (options.IncludeLegacy) policies.Add(Legacy);
        if (model is not null) { policies.Add(Learned); policies.Add(Hybrid); policies.Add(HybridTenpai); policies.Add(Guarded); }
        var totals = new Totals(policies, model is not null);
        var started = DateTime.UtcNow;
        var done = 0;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Threads), CancellationToken = ct };
        Parallel.ForEach(games, parallel, () => new Worker(policies, model, weights), (index, _, worker) =>
        {
            var game = corpus.Read(index);
            foreach (var decision in game.Decisions)
            {
                ct.ThrowIfCancellationRequested();
                worker.Evaluate(decision);
            }
            var n = Interlocked.Increment(ref done);
            if (progress is not null && (n % 25 == 0 || n == games.Length))
                progress($"{n}/{games.Length} games, {worker.Local.Decisions} decisions on this worker, {(DateTime.UtcNow - started).TotalSeconds:F0} s");
            return worker;
        }, worker => totals.Merge(worker.Local));
        return totals.Report(corpus.Fingerprint, options.Split, games.Length, model?.Status, (DateTime.UtcNow - started).TotalSeconds);
    }

    public static void Write(string path, EvaluationReport report) =>
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

    public static string Format(EvaluationReport report)
    {
        var lines = new List<string>
        {
            $"corpus {report.Corpus[..12]} split={report.Split} games={report.Games} decisions={report.Decisions} skipped={report.Skipped} model={report.Model ?? "-"}",
            string.Empty,
            $"{"agreement with the human action",-34}" + string.Join(string.Empty, report.Agreement.Keys.Select(p => $"{p,12}")),
        };
        foreach (var category in report.Agreement.Values.First().Keys)
        {
            var n = report.Agreement.Values.First()[category].Decisions;
            lines.Add($"{category + " (n=" + n + ")",-34}" + string.Join(string.Empty, report.Agreement.Keys.Select(p =>
                $"{report.Agreement[p][category].Agreement,11:P1} ")));
        }

        lines.Add(string.Empty);
        lines.Add($"{"deal-in rate of the chosen tile",-34}{"human",12}" + string.Join(string.Empty, report.Agreement.Keys.Select(p => $"{p,12}")));
        foreach (var category in new[] { "all", "vs-riichi", "vs-open", "late" })
        {
            if (!report.Agreement.Values.First().ContainsKey(category)) continue;
            var first = report.Agreement.Values.First()[category];
            lines.Add($"{category,-34}{first.HumanDealInRate,11:P2} " + string.Join(string.Empty, report.Agreement.Keys.Select(p =>
                $"{report.Agreement[p][category].PolicyDealInRate,11:P2} ")));
        }

        lines.Add(string.Empty);
        lines.Add($"{"calibration (Brier / log-loss / ECE / n / base)",-46}");
        foreach (var (estimator, views) in report.Calibration)
            foreach (var (view, cell) in views)
                lines.Add($"{estimator + " " + view,-46}{cell.Brier,8:F4}{cell.LogLoss,9:F4}{cell.Ece,8:F4}{cell.Count,9}{cell.BaseRate,9:P2}  mean p {cell.MeanPredicted:P2}");
        lines.Add(string.Empty);
        lines.Add("elapsed " + string.Join(", ", report.Seconds.Select(kv => $"{kv.Key} {kv.Value:F0} s")));
        return string.Join(Environment.NewLine, lines);
    }

    // Categories a decision can belong to (a decision counts in every matching one).
    private static IEnumerable<string> Categories(ReplayDecision d, int bestShanten)
    {
        var s = d.Observation.Snapshot;
        yield return "all";
        yield return d.ObservedAction.Kind == SimActionKind.Riichi ? "human-riichi" : "human-discard";
        if (d.LegalActions.Any(a => a.Kind == SimActionKind.Riichi)) yield return "riichi-available";
        var riichi = s.Seats.Any(x => x.Seat != 0 && x.Riichi);
        if (riichi) yield return "vs-riichi";
        else if (s.Seats.Any(x => x.Seat != 0 && x.Melds.Count(m => m.IsOpen) >= 2)) yield return "vs-open";
        else yield return "quiet";
        yield return bestShanten switch { <= 0 => "tenpai", 1 => "1-shanten", _ => "2+-shanten" };
        yield return s.Turn <= 6 ? "early" : s.Turn <= 11 ? "mid" : "late";
        if (s.IsAllLast) yield return "all-last";
    }

    private sealed class Worker
    {
        private readonly Dictionary<string, IPolicy> policies = new();
        private readonly OpponentModel heuristicOpponents;
        private readonly LearnedOpponentModel? learnedOpponents;
        private readonly LearnedModel? model;
        private readonly PolicyWeights weights;
        public Totals Local { get; }

        public Worker(List<string> names, LearnedModel? model, PolicyWeights weights)
        {
            this.model = model;
            this.weights = weights;
            this.heuristicOpponents = new OpponentModel(weights);
            this.learnedOpponents = model is null ? null : new LearnedOpponentModel(model, weights);
            foreach (var name in names)
                this.policies[name] = name switch
                {
                    Heuristic => new DecisionPolicy(weights: weights),
                    HeuristicEv => new DecisionPolicy(weights: weights with { RankByPointEv = true }),
                    Legacy => new DecisionPolicy(weights: weights with { DefenseModel = DefenseModel.Legacy }),
                    Learned => new LearnedPolicy(new DecisionPolicy(opponents: new LearnedOpponentModel(model!, weights), weights: weights), model!, weights),
                    Hybrid => new DecisionPolicy(opponents: new LearnedOpponentModel(model!, weights), weights: weights),
                    HybridTenpai => new DecisionPolicy(opponents: new LearnedOpponentModel(model!, weights, useLearnedDanger: false), weights: weights),
                    Guarded => new DecisionPolicy(opponents: new LearnedOpponentModel(model!, weights, useLearnedDanger: false),
                        discards: new LearnedDiscardPolicy(model!, weights), weights: weights),
                    _ => throw new ArgumentException(name),
                };
            this.Local = new Totals(names, model is not null);
        }

        public void Evaluate(ReplayDecision d)
        {
            var state = d.Observation.Snapshot;
            // Only the turn decisions both policy families model: a closed or open hand with a
            // discard legal (riichi optional). Wins/calls are never recorded as replay decisions.
            if (!state.Can(LegalAction.Discard) || state.Hand.Count + 3 * state.OurMelds.Count != 14
                || d.ObservedAction.Kind is not (SimActionKind.Discard or SimActionKind.Riichi) || d.ObservedAction.Tile is null)
            {
                this.Local.Skipped++;
                return;
            }

            var human = d.ObservedAction;
            var humanTile = Tile.Parse(human.Tile!);
            var targets = d.OpponentTargets;
            int bestShanten;
            var choices = new Dictionary<string, ActionChoice>();
            foreach (var (name, policy) in this.policies)
                choices[name] = policy.Choose(state, CancellationToken.None);
            var reference = choices[Heuristic];
            bestShanten = reference.Candidates.Count > 0 ? reference.Candidates.Min(c => c.ShantenAfter) : 9;
            var categories = Categories(d, bestShanten).ToArray();
            var humanDealIn = DealIn(humanTile, targets);
            var humanSafe = reference.Candidates.FirstOrDefault(c => TileHelpers.SameKind(c.Tile, humanTile)) is { DangerSeat: >= 1 } hc && hc.DangerRank <= DangerRank.B;

            this.Local.Decisions++;
            foreach (var (name, choice) in choices)
            {
                var mapped = choice.Kind is ActionKind.Discard or ActionKind.Riichi && choice.Tile is { } t;
                var tile = mapped ? choice.Tile!.Value : (Tile?)null;
                var agree = mapped && TileHelpers.SameKind(tile!.Value, humanTile)
                            && (choice.Kind == ActionKind.Riichi) == (human.Kind == SimActionKind.Riichi);
                var dealIn = tile is { } pt ? DealIn(pt, targets) : (false, 0);
                var safe = tile is { } st && reference.Candidates.FirstOrDefault(c => TileHelpers.SameKind(c.Tile, st)) is { DangerSeat: >= 1 } sc && sc.DangerRank <= DangerRank.B;
                foreach (var category in categories)
                    this.Local.Add(name, category, agree, !mapped, humanDealIn, dealIn, safe, humanSafe);
                // Riichi choice alone (declare or not), independent of the tile.
                if (d.LegalActions.Any(a => a.Kind == SimActionKind.Riichi))
                    this.Local.Add(name, "riichi-choice", mapped && (choice.Kind == ActionKind.Riichi) == (human.Kind == SimActionKind.Riichi), !mapped, humanDealIn, dealIn, safe, humanSafe);
            }

            this.Calibrate(state, targets);
        }

        private static (bool DealtIn, int Points) DealIn(Tile tile, OpponentTrainingTarget[] targets)
        {
            var kind = TileHelpers.ToIndex(tile);
            var points = 0;
            foreach (var target in targets)
                if (target.Tenpai && target.RonPointsExcludingUra.Length == 34 && target.RonPointsExcludingUra[kind] > 0)
                    points = Math.Max(points, target.RonPointsExcludingUra[kind]);
            return (points > 0, points);
        }

        private void Calibrate(StateSnapshot state, OpponentTrainingTarget[] targets)
        {
            if (targets.Length == 0) return;
            this.heuristicOpponents.Update(state);
            this.learnedOpponents?.Update(state);
            var visible = new int[34];
            foreach (var t in state.Hand.Concat(state.SeenForAnalyzer()).Concat(state.OurMelds.SelectMany(m => m.Tiles)).Concat(state.DoraIndicators))
                visible[TileHelpers.ToIndex(t)]++;
            var inHand = new bool[34];
            foreach (var t in state.Hand) inHand[TileHelpers.ToIndex(t)] = true;
            foreach (var target in targets)
            {
                if (target.Seat is < 1 or > 3 || target.RonPointsExcludingUra.Length != 34) continue;
                var seat = state.Seats[target.Seat];
                if (!seat.Riichi)
                {
                    // Tenpai: riichi seats are 1 by rule in every model, so only quiet/open seats count.
                    this.Local.Calibrate("tenpai/heuristic", seat.Melds.Count > 0 ? "open" : "closed", this.heuristicOpponents.TenpaiProbability(target.Seat), target.Tenpai);
                    if (this.learnedOpponents is not null)
                        this.Local.Calibrate("tenpai/learned", seat.Melds.Count > 0 ? "open" : "closed", this.learnedOpponents.TenpaiProbability(target.Seat), target.Tenpai);
                }

                if (!target.Tenpai) continue;
                // Conditional ron probability: every kind, and only the kinds we could discard.
                for (var kind = 0; kind < 34; kind++)
                {
                    var tile = TileHelpers.FromIndex(kind);
                    var truth = target.RonPointsExcludingUra[kind] > 0;
                    var h = this.heuristicOpponents.Danger(tile, target.Seat);
                    var view = seat.Riichi ? "vs-riichi" : "vs-tenpai-no-riichi";
                    this.Local.Calibrate("danger/heuristic", view + "/all-kinds", h, truth);
                    if (inHand[kind]) this.Local.Calibrate("danger/heuristic", view + "/in-hand", h, truth);
                    if (this.learnedOpponents is not null)
                    {
                        var l = this.learnedOpponents.Danger(tile, target.Seat);
                        this.Local.Calibrate("danger/learned", view + "/all-kinds", l, truth);
                        if (inHand[kind]) this.Local.Calibrate("danger/learned", view + "/in-hand", l, truth);
                    }
                }
            }
        }
    }

    // Thread-local accumulators, merged once per game.
    private sealed class Totals
    {
        private readonly Dictionary<string, Dictionary<string, Cell>> agreement = new();
        private readonly Dictionary<string, Dictionary<string, Calib>> calibration = new();
        private readonly object gate = new();
        public int Decisions;
        public int Skipped;

        public Totals(List<string> policies, bool hasModel)
        {
            foreach (var p in policies) this.agreement[p] = new Dictionary<string, Cell>();
        }

        public void Add(string policy, string category, bool agree, bool unmapped, (bool DealtIn, int Points) human, (bool DealtIn, int Points) chosen, bool safe, bool humanSafe)
        {
            if (!this.agreement[policy].TryGetValue(category, out var cell)) this.agreement[policy][category] = cell = new Cell();
            cell.Decisions++;
            if (agree) cell.Agree++;
            if (unmapped) cell.Unmapped++;
            if (human.DealtIn) { cell.HumanDealIns++; cell.HumanPoints += human.Points; }
            if (chosen.DealtIn) { cell.PolicyDealIns++; cell.PolicyPoints += chosen.Points; }
            if (safe) cell.Safe++;
            if (humanSafe) cell.HumanSafe++;
        }

        public void Calibrate(string estimator, string view, double p, bool truth)
        {
            if (!this.calibration.TryGetValue(estimator, out var views)) this.calibration[estimator] = views = new Dictionary<string, Calib>();
            if (!views.TryGetValue(view, out var c)) views[view] = c = new Calib();
            c.Add(p, truth);
        }

        public void Merge(Totals other)
        {
            lock (this.gate)
            {
                this.Decisions += other.Decisions;
                this.Skipped += other.Skipped;
                foreach (var (policy, cells) in other.agreement)
                    foreach (var (category, cell) in cells)
                    {
                        if (!this.agreement[policy].TryGetValue(category, out var mine)) this.agreement[policy][category] = mine = new Cell();
                        mine.Merge(cell);
                    }
                foreach (var (estimator, views) in other.calibration)
                    foreach (var (view, calib) in views)
                    {
                        if (!this.calibration.TryGetValue(estimator, out var mine)) this.calibration[estimator] = mine = new Dictionary<string, Calib>();
                        if (!mine.TryGetValue(view, out var c)) mine[view] = c = new Calib();
                        c.Merge(calib);
                    }
            }
        }

        public EvaluationReport Report(string corpus, string split, int games, string? model, double seconds)
        {
            var order = new[] { "all", "human-discard", "human-riichi", "riichi-available", "riichi-choice", "vs-riichi", "vs-open", "quiet", "tenpai", "1-shanten", "2+-shanten", "early", "mid", "late", "all-last" };
            var agreement = this.agreement.ToDictionary(kv => kv.Key, kv => order.Where(kv.Value.ContainsKey)
                .ToDictionary(c => c, c => kv.Value[c].ToRecord()));
            var calibration = this.calibration.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(v => v.Key, StringComparer.Ordinal)
                .ToDictionary(v => v.Key, v => v.Value.ToRecord()));
            return new EvaluationReport(corpus, split, games, this.Decisions, this.Skipped, model, agreement, calibration, new Dictionary<string, double> { ["total"] = seconds });
        }

        private sealed class Cell
        {
            public int Decisions, Agree, Unmapped, HumanDealIns, PolicyDealIns, Safe, HumanSafe;
            public double HumanPoints, PolicyPoints;
            public void Merge(Cell o)
            {
                this.Decisions += o.Decisions; this.Agree += o.Agree; this.Unmapped += o.Unmapped; this.HumanDealIns += o.HumanDealIns;
                this.PolicyDealIns += o.PolicyDealIns; this.Safe += o.Safe; this.HumanSafe += o.HumanSafe; this.HumanPoints += o.HumanPoints; this.PolicyPoints += o.PolicyPoints;
            }
            public AgreementCell ToRecord() => new(this.Decisions, this.Agree, this.Unmapped, this.HumanDealIns, this.PolicyDealIns,
                this.Decisions == 0 ? 0 : this.HumanPoints / this.Decisions, this.Decisions == 0 ? 0 : this.PolicyPoints / this.Decisions, this.Safe, this.HumanSafe);
        }

        private sealed class Calib
        {
            private const int Bins = 10;
            private readonly int[] binCount = new int[Bins];
            private readonly double[] binPredicted = new double[Bins];
            private readonly int[] binPositives = new int[Bins];
            private int count, positives;
            private double brier, logLoss, predicted;
            public void Add(double p, bool truth)
            {
                p = Math.Clamp(p, 0, 1);
                var y = truth ? 1 : 0;
                var q = Math.Clamp(p, 1e-4, 1 - 1e-4);
                this.count++; this.positives += y; this.brier += (p - y) * (p - y); this.predicted += p;
                this.logLoss -= y * Math.Log(q) + (1 - y) * Math.Log(1 - q);
                var bin = Math.Min(Bins - 1, (int)(p * Bins));
                this.binCount[bin]++; this.binPredicted[bin] += p; this.binPositives[bin] += y;
            }
            public void Merge(Calib o)
            {
                this.count += o.count; this.positives += o.positives; this.brier += o.brier; this.logLoss += o.logLoss; this.predicted += o.predicted;
                for (var b = 0; b < Bins; b++) { this.binCount[b] += o.binCount[b]; this.binPredicted[b] += o.binPredicted[b]; this.binPositives[b] += o.binPositives[b]; }
            }
            public CalibrationCell ToRecord()
            {
                if (this.count == 0) return new CalibrationCell(0, 0, 0, 0, 0, 0);
                var ece = 0d;
                for (var b = 0; b < Bins; b++)
                    if (this.binCount[b] > 0)
                        ece += (double)this.binCount[b] / this.count * Math.Abs(this.binPredicted[b] / this.binCount[b] - (double)this.binPositives[b] / this.binCount[b]);
                return new CalibrationCell(this.count, this.positives, this.brier / this.count, this.logLoss / this.count, ece, this.predicted / this.count);
            }
        }
    }
}
