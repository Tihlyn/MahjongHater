using System.Text.Json;
using MahjongHater.Core.Learning;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Replay;
using MahjongHater.Core.Simulation;

internal static class LearningCommands
{
    public const string Usage = "learn-data <replay-corpus> <new-dataset> [search-run|-] [max-games] [float32|float16] [workers] [compact|dense] | learn-check <model.json> <parity.json>"
        + " | learn-eval <replay-corpus> <report.json> [model.json|-] [split=test] [max-games] [threads] [weights.json|-] [reactions-only]"
        + " | learn-fit-tenpai <replay-corpus> [split=train] [max-games]";
    public static bool Handles(string command) => command is "learn-data" or "learn-check" or "learn-eval" or "learn-fit-tenpai";
    public static int Run(string[] args, CancellationToken ct)
    {
        if (args.Length < 2) throw new ArgumentException(Usage);
        if (args[0] == "learn-fit-tenpai")
        {
            var corpus = new ReplayCorpus(args[1]);
            var result = TenpaiFit.Run(corpus, PolicyWeights.Default, args.Length > 2 ? args[2] : "train", args.Length > 3 ? int.Parse(args[3]) : int.MaxValue, ct: ct);
            Console.WriteLine($"{result.Rows} non-riichi opponent rows, {result.Positives} tenpai ({(double)result.Positives / result.Rows:P2})");
            Console.WriteLine($"shipped: log-loss {result.ShippedLogLoss:F4}  Brier {result.ShippedBrier:F4}");
            Console.WriteLine($"fitted:  log-loss {result.FittedLogLoss:F4}  Brier {result.FittedBrier:F4}");
            foreach (var (bucket, n, predicted, observed) in result.Reliability)
                Console.WriteLine($"  {bucket,-16} n={n,8}  predicted {predicted:P2}  observed {observed:P2}");
            Console.WriteLine("PolicyWeights initializers:");
            Console.WriteLine(result.Initializers);
            return 0;
        }
        if (args.Length < 3) throw new ArgumentException(Usage);
        if (args[0] == "learn-eval")
        {
            var corpus = new ReplayCorpus(args[1]);
            var model = args.Length > 3 && args[3] != "-" ? LearnedModel.Load(args[3]) : null;
            var options = new EvaluationOptions
            {
                Split = args.Length > 4 ? args[4] : "test",
                MaxGames = args.Length > 5 ? int.Parse(args[5]) : int.MaxValue,
                Threads = args.Length > 6 ? int.Parse(args[6]) : Math.Max(1, Environment.ProcessorCount - 1),
                ReactionsOnly = args.Length > 8 && args[8] == "reactions-only",
            };
            if (options.Split is not ("train" or "validation" or "test" or "all")) throw new ArgumentException("split must be train, validation, test or all");
            // Weight overrides as a JSON object of PolicyWeights properties, e.g. {"LearnedCallPassThreshold": 0.7}.
            var weights = args.Length > 7 && args[7] != "-"
                ? JsonSerializer.Deserialize<PolicyWeights>(File.ReadAllText(args[7])) ?? throw new InvalidDataException("Empty weights file.")
                : PolicyWeights.Default;
            if (args.Length > 7 && args[7] != "-") Console.Error.WriteLine($"weights: {File.ReadAllText(args[7]).Trim()}");
            var report = LearningEvaluation.Run(corpus, model, weights, options, Console.Error.WriteLine, ct);
            LearningEvaluation.Write(args[2], report);
            Console.WriteLine(LearningEvaluation.Format(report));
            return 0;
        }
        if (args[0] == "learn-data")
        {
            var dtype = args.Length > 5 ? args[5] : "float32";
            if (dtype is not ("float32" or "float16")) throw new ArgumentException("dtype must be float32 or float16");
            var form = args.Length > 7 ? args[7] : "compact";
            if (form is not ("compact" or "dense")) throw new ArgumentException("row form must be compact or dense");
            LearningDataset.Export(new ReplayCorpus(args[1]), args[2], args.Length > 3 && args[3] != "-" ? args[3] : null, ct,
                args.Length > 4 ? int.Parse(args[4]) : int.MaxValue, dtype == "float16", args.Length > 6 ? int.Parse(args[6]) : 0, form == "compact");
            Console.WriteLine(File.ReadAllText(Path.Combine(args[2], "manifest.json")));
        }
        else
        {
            var model = LearnedModel.Load(args[1]);
            var cases = JsonSerializer.Deserialize<ParityCase[]>(File.ReadAllText(args[2]), SimulationFiles.Json)
                ?? throw new InvalidDataException("Missing parity vectors.");
            if (cases.Length == 0) throw new InvalidDataException("Empty parity vectors.");
            var worst = 0d;
            foreach (var item in cases)
            {
                var actual = model.Predict(item.Input, ct);
                if (item.Output.Length != actual.Length || item.Output.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Invalid parity output.");
                worst = Math.Max(worst, actual.Zip(item.Output, (a, b) => Math.Abs((double)a - b)).Max());
            }
            if (worst > .0002) throw new InvalidDataException($"Inference parity failed: maximum error {worst}.");
            Console.WriteLine($"Verified {cases.Length} PyTorch/C# inference vectors; maximum absolute error {worst:G6}.");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            const int repetitions = 50;
            for (var i = 0; i < repetitions; i++) model.Predict(cases[i % cases.Length].Input, ct);
            Console.WriteLine($"Inference: {clock.Elapsed.TotalMilliseconds / repetitions:F2} ms per position on one thread (schema {model.Schema}, {model.FeatureVersion}).");
        }
        return 0;
    }
    private sealed record ParityCase(float[] Input, float[] Output);
}
