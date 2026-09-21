using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MahjongHater.Core.Precomputed;
using MahjongHater.Core.Replay;
using MahjongHater.Core.Simulation;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Learning;

// Fixed little-endian float32 rows, memory-mappable on the training box.
// features | legal mask | human action (-1 absent) | opponent targets | Q labels.
// Missing opponent/Q targets are -1 / NaN respectively, never manufactured zeros.
public static class LearningDataset
{
    // Not SimulationFiles.Json: its IgnoreReadOnlyProperties (needed for StateSnapshot's
    // computed members) also drops every scalar of the anonymous manifest object, which
    // left train.py without Schema/Features/RowFloats.
    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    public const int Opponents = 207;
    public const int RowFloats = LearningFeatures.Count + 74 + 1 + Opponents + 74;

    public static void Export(ReplayCorpus corpus, string destination, string? searchRun = null, CancellationToken ct = default)
    {
        var path = Path.GetFullPath(destination);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Learning dataset destination exists.");
        var temp = path + ".building-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temp);
        var counts = new Dictionary<string, int> { ["train"] = 0, ["validation"] = 0, ["test"] = 0 };
        var humans = new Dictionary<string, int>(counts);
        var skipped = 0;
        var searchRows = 0;
        var writers = counts.Keys.ToDictionary(k => k, k => new BinaryWriter(File.Create(Path.Combine(temp, k + ".f32"))));
        using var provenance = new StreamWriter(Path.Combine(temp, "rows.jsonl"));
        void Write(string split, string game, StateSnapshot state, SimAction[] legal, int human, OpponentTrainingTarget[] targets, ActionStatistics[]? search)
        {
            var actionIds = legal.Select(LearningFeatures.ActionIndex).ToArray();
            // Do not pretend a policy over a subset is a policy over all legal actions.
            if (actionIds.Length < 2 || actionIds.Any(a => a < 0) || actionIds.Distinct().Count() != actionIds.Length
                || human >= 0 && !actionIds.Contains(human)) { skipped++; return; }
            var row = new float[RowFloats];
            LearningFeatures.Encode(state).CopyTo(row, 0);
            foreach (var a in actionIds) row[LearningFeatures.Count + a] = 1;
            var offset = LearningFeatures.Count + 74;
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
            Array.Fill(row, float.NaN, offset, 74);
            if (search is not null)
                foreach (var a in search.Where(a => a.Visits >= 8)) row[offset + LearningFeatures.ActionIndex(a.Action)] = (float)(a.MeanScore / 32000);
            foreach (var value in row) writers[split].Write(value);
            provenance.WriteLine(JsonSerializer.Serialize(new { Split = split, Row = counts[split], Game = game, Human = human >= 0 }, SimulationFiles.Json));
            counts[split]++;
            if (human >= 0) humans[split]++; else searchRows++;
        }
        try
        {
            foreach (var i in Enumerable.Range(0, corpus.Count))
            {
                ct.ThrowIfCancellationRequested();
                var game = corpus.Read(i);
                foreach (var d in game.Decisions)
                {
                    ct.ThrowIfCancellationRequested();
                    Write(ReplayCorpus.Split(game.Sha256), game.Sha256, d.Observation.Snapshot, d.LegalActions,
                        LearningFeatures.ActionIndex(d.ObservedAction), d.OpponentTargets, null);
                }
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
                        Write("train", game, SnapshotJson.Deserialize(r.Snapshot), r.Actions.Select(a => a.Action).ToArray(), -1, [], r.Actions);
                }
            }
        }
        finally { foreach (var writer in writers.Values) writer.Dispose(); }
        provenance.Dispose();
        var hashes = counts.Keys.ToDictionary(k => k, k =>
        {
            using var file = File.OpenRead(Path.Combine(temp, k + ".f32"));
            return Convert.ToHexString(SHA256.HashData(file));
        });
        File.WriteAllText(Path.Combine(temp, "manifest.json"), JsonSerializer.Serialize(new
        {
            Schema = 1, Features = LearningFeatures.Version, Channels = LearningFeatures.Channels, Width = 34, Actions = 74,
            RowFloats, Corpus = corpus.Fingerprint, Rules = corpus.Manifest.TargetRules, Rows = counts, HumanRows = humans,
            SearchRows = searchRows, Skipped = skipped, Sha256 = hashes,
            SearchRunFingerprint = searchRun is null ? null : SimulationFiles.Read<RunManifest>(Path.Combine(searchRun, "manifest.json.gz")).Fingerprint,
        }, ManifestJson));
        ct.ThrowIfCancellationRequested();
        Directory.Move(temp, path);
    }
}
