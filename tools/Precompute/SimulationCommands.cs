using System.Diagnostics;
using System.Text.Json;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Precomputed;
using MahjongHater.Core.Simulation;
using MahjongHater.Core.Replay;

internal static class SimulationCommands
{
    public const string Usage = "sim-config <config.json> | sim-check <matches> [seed] | sim-run <config.json> <run-dir> [workers] [matches] | sim-pack <db-dir> <run-dir>... | sim-export <run-dir> <snapshots.jsonl> | sim-probe <db-dir> <snapshots.jsonl> | sim-inspect <db-dir> | replay-import <config.json> <xml/mjlog/zip/folder> <new-corpus> [min-rank=16] | replay-run <config.json> <corpus> <run-dir> [workers] [matches] | replay-inspect <corpus> | replay-export <corpus> <new-jsonl> [all|train|validation|test]";
    public static bool Handles(string command) => command is "sim-config" or "sim-check" or "sim-run" or "sim-pack" or "sim-export" or "sim-probe" or "sim-inspect"
        or "replay-import" or "replay-run" or "replay-inspect" or "replay-export";
    public static async Task<int> Run(string[] args, CancellationToken ct)
    {
        if (args[0] == "replay-export")
        {
            if (args.Length < 3) throw new ArgumentException("replay-export <corpus> <new-jsonl> [all|train|validation|test]");
            var count = new ReplayCorpus(args[1]).Export(args[2], args.Length > 3 ? args[3] : "all", ct);
            Console.WriteLine($"Exported {count} supervised rows; use Inputs as features and Targets only as labels. Splits are assigned by whole-game hash.");
            return 0;
        }
        if (args[0] == "replay-import")
        {
            if (args.Length < 4) throw new ArgumentException(Usage);
            var config = JsonSerializer.Deserialize<GenerationConfig>(File.ReadAllText(args[1]), SimulationFiles.Json)
                ?? throw new InvalidDataException("Empty config.");
            config.Validate();
            var result = ReplayCorpus.Import(args[2], args[3], config.Rules, args.Length > 4 ? int.Parse(args[4]) : 16, Console.WriteLine, ct);
            Console.WriteLine($"Imported {result.Games.Length} unique games, {result.Games.Sum(g => g.Decisions)} decisions; {result.Duplicates} duplicates, {result.Rejected.Length} rejected files. Rejection reasons are in the corpus manifest.");
            return result.Games.Length > 0 ? 0 : 1;
        }
        if (args[0] == "replay-inspect")
        {
            var corpus = new ReplayCorpus(args[1]);
            for (var i = 0; i < corpus.Count; i++) { ct.ThrowIfCancellationRequested(); corpus.Read(i); }
            Console.WriteLine(JsonSerializer.Serialize(corpus.Manifest, new JsonSerializerOptions(SimulationFiles.Json) { WriteIndented = true }));
            Console.WriteLine($"Verified {corpus.Count} game checksums; corpus identity {corpus.Fingerprint}.");
            return 0;
        }
        if (args[0] == "replay-run")
        {
            if (args.Length < 4) throw new ArgumentException(Usage);
            var config = JsonSerializer.Deserialize<GenerationConfig>(File.ReadAllText(args[1]), SimulationFiles.Json)
                ?? throw new InvalidDataException("Empty config.");
            if (args.Length > 4) config = config with { Workers = int.Parse(args[4]) };
            if (args.Length > 5) config = config with { Matches = int.Parse(args[5]) };
            var corpus = new ReplayCorpus(args[2]);
            Console.WriteLine($"Training public decision states from {corpus.Count} replay games; observed outcomes are not search EV labels.");
            var timer = Stopwatch.StartNew();
            var gate = new object();
            var last = -5d;
            await GenerationRunner.Run(config, args[3], p =>
            {
                lock (gate)
                {
                    if (timer.Elapsed.TotalSeconds - last < 5 && p.Completed != p.Total) return;
                    last = timer.Elapsed.TotalSeconds;
                    Console.WriteLine($"{timer.Elapsed:hh\\:mm\\:ss} completed={p.Completed}/{p.Total} resumed={p.Resumed} records={p.Records} rejected={p.Rejected} active-game={p.ActiveMatch}");
                }
            }, ct, corpus);
            return 0;
        }
        if (args[0] == "sim-config")
        {
            using var file = new FileStream(args[1], FileMode.CreateNew);
            JsonSerializer.Serialize(file, new GenerationConfig(), new JsonSerializerOptions(SimulationFiles.Json) { WriteIndented = true });
            Console.WriteLine($"Wrote {args[1]}: 12 workers, 8 states/match, 256 iterations, 16 belief particles. Adjust matches/budget before a long run.");
            return 0;
        }
        if (args[0] == "sim-check")
        {
            var count = int.Parse(args[1]);
            if (count < 1) throw new ArgumentException("Match count must be positive.");
            var seed = args.Length > 2 ? int.Parse(args[2]) : 1;
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                var result = MatchRunner.Play(new SimulationRules(), SimulationFiles.Seed(seed, i, 0), new SimulationPolicy(), ct);
                Console.WriteLine($"match={i} hands={result.Hands} decisions={result.Decisions} scores=[{string.Join(",", result.Scores)}] ends={JsonSerializer.Serialize(result.Endings)}");
            }
            Console.WriteLine($"Checked {count} complete matches in {timer.Elapsed.TotalSeconds:0.00}s; tile/point invariants checked on every transition.");
            return 0;
        }
        if (args[0] == "sim-inspect")
        {
            using var database = new SimulationDatabase(args[1], verifyChecksums: true);
            Console.WriteLine(JsonSerializer.Serialize(database.Manifest, new JsonSerializerOptions(SimulationFiles.Json) { WriteIndented = true }));
            return 0;
        }
        if (args.Length < 3)
            throw new ArgumentException(Usage);
        if (args[0] == "sim-run")
        {
            var config = JsonSerializer.Deserialize<GenerationConfig>(File.ReadAllText(args[1]), SimulationFiles.Json)
                ?? throw new InvalidDataException("Empty config.");
            if (args.Length > 3) config = config with { Workers = int.Parse(args[3]) };
            if (args.Length > 4) config = config with { Matches = int.Parse(args[4]) };
            var timer = Stopwatch.StartNew();
            var gate = new object();
            var last = -5d;
            Console.WriteLine($"Starting {config.Matches} matches, shard {config.ShardIndex}/{config.ShardCount}, {config.Workers} workers. Checkpoints: {Path.GetFullPath(args[2])}");
            await GenerationRunner.Run(config, args[2], p =>
            {
                lock (gate)
                {
                    if (timer.Elapsed.TotalSeconds - last < 5 && p.Completed != p.Total) return;
                    last = timer.Elapsed.TotalSeconds;
                    Console.WriteLine($"{timer.Elapsed:hh\\:mm\\:ss} completed={p.Completed}/{p.Total} resumed={p.Resumed} records={p.Records} rejected={p.Rejected} active-match={p.ActiveMatch}");
                }
            }, ct);
            return 0;
        }
        if (args[0] == "sim-pack")
        {
            var result = SimulationDatabase.Pack(args.Skip(2), args[1], ct);
            Console.WriteLine($"Packed {result.InputRecords} records and {result.IndexEntries} exact/abstract index entries into {args[1]}; {result.DataBytes:N0} data bytes.");
            return 0;
        }
        if (args[0] == "sim-export")
        {
            using var writer = new StreamWriter(new FileStream(args[2], FileMode.CreateNew));
            var count = 0;
            foreach (var path in Directory.EnumerateFiles(Path.Combine(args[1], "completed"), "*.json.gz").Order(StringComparer.Ordinal))
                foreach (var record in SimulationFiles.Read<GenerationJob>(path).Records)
                {
                    ct.ThrowIfCancellationRequested();
                    writer.WriteLine(record.Snapshot);
                    count++;
                }
            Console.WriteLine($"Exported {count} public snapshots.");
            return 0;
        }
        using (var database = new SimulationDatabase(args[1]))
        {
            foreach (var line in File.ReadLines(args[2]).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                ct.ThrowIfCancellationRequested();
                var state = SnapshotJson.Deserialize(line);
                var beliefs = BeliefState.Capture(state, new OpponentModel());
                var key = IncrementalStateKey.Create(state, beliefs, database.Manifest.Profile);
                if (!database.TryExact(key, out var record))
                    Console.WriteLine("Exact miss.");
                else
                {
                    var best = record!.Actions.OrderByDescending(a => a.MeanUtility).First();
                    Console.WriteLine($"Exact hit: {best.Action.Key}; utility={best.MeanUtility:0.0}, points={best.MeanScore:0.0}, visits={best.Visits}, win={best.Wins/(double)best.Visits:P1}.");
                }
            }
        }
        return 0;
    }
}
