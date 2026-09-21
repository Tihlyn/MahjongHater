using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MahjongHater.Core.Precomputed;
using MahjongHater.Core.Replay;
using MahjongHater.Core.Simulation;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Learning;

// Fixed little-endian rows (float32, or float16 for large exports: every input value is
// a small fraction or a count and the targets are 0/1/-1, small ratios or NaN).
// features | legal mask | human action (-1 absent) | opponent targets | Q labels | final placement.
// Missing opponent/Q/placement targets are -1 / NaN / -1 respectively, never manufactured zeros.
public static class LearningDataset
{
    // Not SimulationFiles.Json: its IgnoreReadOnlyProperties (needed for StateSnapshot's
    // computed members) also drops every scalar of the anonymous manifest object, which
    // left train.py without Schema/Features/RowFloats.
    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    public const int Opponents = 207;
    public const int Schema = 3;
    // features | legal mask | human action | opponent targets | search Q | placement (1-4), all v2 sizes.
    public const int RowFloats = LearningFeatures.Count + LearningFeatures.Actions + 1 + Opponents + LearningFeatures.Actions + 1;

    // `maxGames` takes a uniform, split-independent subset (ordered by a hash slice the
    // split function does not use) so a large corpus can be exported at dense size.
    // Rows of one game are encoded on `workers` threads and written in corpus order.
    public static void Export(ReplayCorpus corpus, string destination, string? searchRun = null, CancellationToken ct = default,
        int maxGames = int.MaxValue, bool half = false, int workers = 0)
    {
        if (workers < 1) workers = Math.Max(1, Environment.ProcessorCount - 1);
        var path = Path.GetFullPath(destination);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Learning dataset destination exists.");
        var temp = path + ".building-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temp);
        var extension = half ? ".f16" : ".f32";
        var counts = new Dictionary<string, int> { ["train"] = 0, ["validation"] = 0, ["test"] = 0 };
        var humans = new Dictionary<string, int>(counts);
        var skipped = 0;
        var searchRows = 0;
        var writers = counts.Keys.ToDictionary(k => k, k => new BinaryWriter(new BufferedStream(File.Create(Path.Combine(temp, k + extension)), 1 << 20)));
        using var provenance = new StreamWriter(Path.Combine(temp, "rows.jsonl"));
        // null = skipped (the legal set has an action outside the space or the human action is not in it).
        static float[]? Encode(StateSnapshot state, SimAction[] legal, int human, OpponentTrainingTarget[] targets, ActionStatistics[]? search, int placement)
        {
            // Physical variants of one action (red vs plain five in a chi) share an index.
            var actionIds = legal.Select(LearningFeatures.ActionIndex).Distinct().ToArray();
            // Do not pretend a policy over a subset is a policy over all legal actions.
            if (actionIds.Length < 2 || actionIds.Any(a => a < 0) || human >= 0 && !actionIds.Contains(human)) return null;
            var row = new float[RowFloats];
            LearningFeatures.Encode(state).CopyTo(row, 0);
            foreach (var a in actionIds) row[LearningFeatures.Count + a] = 1;
            var offset = LearningFeatures.Count + LearningFeatures.Actions;
            row[offset++] = human;
            Array.Fill(row, -1, offset, Opponents);
            foreach (var target in targets)
            {
                if (target.Seat is < 1 or > 3 || target.RonPointsExcludingUra.Length != 34) throw new InvalidDataException("Invalid opponent labels.");
                var seat = target.Seat - 1;
                row[offset + seat] = target.Tenpai ? 1 : 0;
                for (var kind = 0; kind < 34; kind++)
                {
                    var points = target.RonPointsExcludingUra[kind];
                    // Winning-tile probability is conditional on tenpai. Furiten
                    // and yakuless waits remain negative legal-ron examples.
                    row[offset + 3 + seat * 34 + kind] = target.Tenpai ? (points > 0 ? 1 : 0) : -1;
                    row[offset + 105 + seat * 34 + kind] = points > 0 ? points / 32000f : -1;
                }
            }
            offset += Opponents;
            Array.Fill(row, float.NaN, offset, LearningFeatures.Actions);
            if (search is not null)
                foreach (var a in search.Where(a => a.Visits >= 8 && LearningFeatures.ActionIndex(a.Action) >= 0)) row[offset + LearningFeatures.ActionIndex(a.Action)] = (float)(a.MeanScore / 32000);
            // The acting seat's final placement in the source match (source rules), -1 unknown.
            row[^1] = placement is >= 1 and <= 4 ? placement : -1;
            return row;
        }
        void Write(string split, string game, float[]? row, bool human)
        {
            if (row is null) { skipped++; return; }
            var writer = writers[split];
            if (half) foreach (var value in row) writer.Write((Half)value);
            else foreach (var value in row) writer.Write(value);
            provenance.WriteLine(JsonSerializer.Serialize(new { Split = split, Row = counts[split], Game = game, Human = human }, SimulationFiles.Json));
            counts[split]++;
            if (human) humans[split]++; else searchRows++;
        }
        var options = new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct };
        try
        {
            var selected = Enumerable.Range(0, corpus.Count);
            if (maxGames < corpus.Count)
                selected = selected.OrderBy(i => corpus.Manifest.Games[i].Sha256.Substring(8, 8), StringComparer.Ordinal).Take(maxGames).Order();
            // Read + encode game i+1 while game i is written: one game of rows in flight.
            Task<(ReplayGame Game, float[]?[] Rows)>? next = null;
            Task<(ReplayGame Game, float[]?[] Rows)> Prepare(int index) => Task.Run(() =>
            {
                var game = corpus.Read(index);
                var rows = new float[]?[game.Decisions.Length];
                Parallel.For(0, rows.Length, options, j =>
                {
                    var d = game.Decisions[j];
                    rows[j] = Encode(d.Observation.Snapshot, d.LegalActions, LearningFeatures.ActionIndex(d.ObservedAction), d.OpponentTargets, null, game.Match.Placement[d.Seat]);
                });
                return (game, rows);
            }, ct);
            var order = selected.ToArray();
            for (var n = 0; n < order.Length; n++)
            {
                ct.ThrowIfCancellationRequested();
                var current = next ?? Prepare(order[n]);
                next = n + 1 < order.Length ? Prepare(order[n + 1]) : null;
                var (game, rows) = current.GetAwaiter().GetResult();
                foreach (var row in rows)
                    Write(ReplayCorpus.Split(game.Sha256), game.Sha256, row, true);
            }
            if (searchRun is not null)
            {
                var manifest = SimulationFiles.Read<RunManifest>(Path.Combine(searchRun, "manifest.json.gz"));
                if (manifest.Config.Rules != corpus.Manifest.TargetRules || manifest.Fingerprint != RunManifest.Identity(manifest.Config, manifest.CorpusSha256)
                    || manifest.CorpusSha256 is not null && manifest.CorpusSha256 != corpus.Fingerprint)
                    throw new InvalidDataException("Search labels have incompatible rules or replay corpus identity.");
                foreach (var file in Directory.EnumerateFiles(Path.Combine(searchRun, "completed"), "*.json.gz").Order(StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    var job = SimulationFiles.Read<GenerationJob>(file);
                    if (job.Fingerprint != manifest.Fingerprint) throw new InvalidDataException("Mismatched search job.");
                    var game = manifest.CorpusSha256 is null
                        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.Fingerprint + ":" + job.MatchId)))
                        : corpus.Manifest.Games[job.MatchId].Sha256;
                    // Search must not expose held-out replay outcomes to training.
                    if (manifest.CorpusSha256 is not null && ReplayCorpus.Split(game) != "train") continue;
                    foreach (var r in job.Records)
                        Write("train", game, Encode(SnapshotJson.Deserialize(r.Snapshot), r.Actions.Select(a => a.Action).ToArray(), -1, [], r.Actions, -1), false);
                }
            }
        }
        finally { foreach (var writer in writers.Values) writer.Dispose(); }
        provenance.Dispose();
        var hashes = counts.Keys.ToDictionary(k => k, k =>
        {
            using var file = File.OpenRead(Path.Combine(temp, k + extension));
            return Convert.ToHexString(SHA256.HashData(file));
        });
        File.WriteAllText(Path.Combine(temp, "manifest.json"), JsonSerializer.Serialize(new
        {
            Schema, Features = LearningFeatures.Version, Channels = LearningFeatures.Channels, Width = 34, Actions = LearningFeatures.Actions,
            RowFloats, Dtype = half ? "float16" : "float32", Extension = extension, Corpus = corpus.Fingerprint, Rules = corpus.Manifest.TargetRules, Rows = counts, HumanRows = humans,
            GamesUsed = Math.Min(maxGames, corpus.Count), GamesInCorpus = corpus.Count,
            SearchRows = searchRows, Skipped = skipped, Sha256 = hashes,
            SearchRunFingerprint = searchRun is null ? null : SimulationFiles.Read<RunManifest>(Path.Combine(searchRun, "manifest.json.gz")).Fingerprint,
        }, ManifestJson));
        ct.ThrowIfCancellationRequested();
        Directory.Move(temp, path);
    }
}
