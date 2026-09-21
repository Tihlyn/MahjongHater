using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Precomputed;
using MahjongHater.Core.State;

// No game process, network or GPU needed. Corpus files contain public snapshots only.
if (args.Length < 2 || !SimulationCommands.Handles(args[0]) && !LearningCommands.Handles(args[0]) && args[0] is not ("example" or "train" or "probe"))
{
    Console.Error.WriteLine("example <snapshots.jsonl> | train <snapshots.jsonl> <policy.json> [iterations=2048] [horizon=3] [seed=1] | probe <snapshots.jsonl> <policy.json>");
    Console.Error.WriteLine(SimulationCommands.Usage);
    Console.Error.WriteLine(LearningCommands.Usage);
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (LearningCommands.Handles(args[0])) return LearningCommands.Run(args, cancellation.Token);
    if (SimulationCommands.Handles(args[0]))
        return await SimulationCommands.Run(args, cancellation.Token);
    if (args[0] == "example")
    {
        var hand = new[] { "1m", "2m", "3m", "4m", "5m", "6m", "4p", "5p", "7p", "8p", "4s", "4s", "7s", "1z" }.Select(Tile.Parse).ToArray();
        var state = StateSnapshot.Empty with { Phase = GamePhase.OurTurn, Legal = LegalAction.Discard,
            Hand = hand, DrawnTile = hand[^1], SeatWind = Wind.South, HandNumber = 1,
            Seats = Enumerable.Range(0, 4).Select(i => SeatState.Empty(i) with { Score = 25000 }).ToArray() };
        using var output = new StreamWriter(new FileStream(args[1], FileMode.CreateNew));
        output.WriteLine(SnapshotJson.Serialize(state));
        Console.WriteLine($"Wrote an illustrative snapshot to {args[1]}.");
        return 0;
    }
    if (args.Length < 3)
        throw new ArgumentException("The corpus and policy paths are required.");
    if (Path.GetFullPath(args[1]).Equals(Path.GetFullPath(args[2]), StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("The corpus and policy paths must differ.");
    var snapshots = File.ReadLines(args[1]).Where(line => !string.IsNullOrWhiteSpace(line)).Select(SnapshotJson.Deserialize);
    var weights = PolicyWeights.Default;
    if (args[0] == "train")
    {
        var options = new TrainingOptions(args.Length > 3 ? int.Parse(args[3]) : 2048,
            args.Length > 4 ? int.Parse(args[4]) : 3, args.Length > 5 ? int.Parse(args[5]) : 1);
        var artifact = new OfflineDiscardTrainer(weights).Train(snapshots, options, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        PolicyTable.Save(args[2], artifact);
        Console.WriteLine($"Wrote {artifact.Entries.Length} states to {args[2]} ({artifact.Model}, {options.Iterations} iterations/state).");
    }
    else
    {
        var table = PolicyTable.Load(args[2], BeliefState.Profile(weights));
        var policy = new PrecomputedPolicy(new DecisionPolicy(weights: weights), table, weights);
        foreach (var snapshot in snapshots)
        {
            var choice = policy.Choose(snapshot, cancellation.Token);
            Console.WriteLine(choice.Summary);
            Console.WriteLine(choice.Steps[0].Display);
        }
    }
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Canceled. Completed simulation work and per-state checkpoints are retained; rerun the same sim-run command to resume.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.ToString());
    return 1;
}
