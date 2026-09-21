using System.Text.Json;
using MahjongHater.Core.Learning;
using MahjongHater.Core.Replay;
using MahjongHater.Core.Simulation;

internal static class LearningCommands
{
    public const string Usage = "learn-data <replay-corpus> <new-dataset> [search-run] | learn-check <model.json> <parity.json>";
    public static bool Handles(string command) => command is "learn-data" or "learn-check";
    public static int Run(string[] args, CancellationToken ct)
    {
        if (args.Length < 3) throw new ArgumentException(Usage);
        if (args[0] == "learn-data")
        {
            LearningDataset.Export(new ReplayCorpus(args[1]), args[2], args.Length > 3 ? args[3] : null, ct);
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
        }
        return 0;
    }
    private sealed record ParityCase(float[] Input, float[] Output);
}
