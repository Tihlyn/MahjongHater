using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Precomputed;
using MahjongHater.Core.Replay;

namespace MahjongHater.Core.Simulation;

public sealed record GenerationConfig
{
    public int Seed { get; init; } = 20260920;
    public int Matches { get; init; } = 10000;
    public int Workers { get; init; } = 12;
    public int StatesPerMatch { get; init; } = 8;
    public int ShardIndex { get; init; }
    public int ShardCount { get; init; } = 1;
    public bool CheckEveryTransition { get; init; } = true;
    public SimulationRules Rules { get; init; } = new();
    public SearchOptions Search { get; init; } = new();
    public SimulationPolicyMode SelfPlayPolicy { get; init; } = SimulationPolicyMode.Guideline;
    public PolicyWeights Weights { get; init; } = PolicyWeights.Default;

    public void Validate()
    {
        this.Rules.Validate();
        this.Search.Validate();
        if (this.Matches is < 1 or > 100000000 || this.Workers is < 1 or > 64 || this.StatesPerMatch is < 1 or > 256
            || this.ShardCount < 1 || this.ShardIndex < 0 || this.ShardIndex >= this.ShardCount)
            throw new ArgumentException("Invalid generation count, workers or shard selection.");
    }

    // Operational changes do not invalidate completed work. The same match ID and
    // seed have identical data irrespective of worker count, scheduling or shard.
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        Version = SimulationRules.Version, Sampling = "stratified-reservoir-v1", this.Seed, this.StatesPerMatch,
        this.Rules, this.Search, this.SelfPlayPolicy, this.Weights,
    }))));
}

public sealed record RunManifest(int Schema, string Fingerprint, string Profile, GenerationConfig Config, string? CorpusSha256 = null)
{
    public static string Identity(GenerationConfig config, string? corpusSha256) => corpusSha256 is null ? config.Fingerprint()
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("replay-training-v1:" + config.Fingerprint() + ":" + corpusSha256)));
}
public sealed record RejectedSample(int Sample, string Reason);
public sealed record GenerationJob(int Schema, string Fingerprint, int MatchId, int Seed, MatchResult Match,
    SimulationRecord[] Records, RejectedSample[] Rejected);
public sealed record GenerationCheckpoint(string Fingerprint, int MatchId, int Seed, MatchResult Match,
    SimulationObservation[] Samples, int NextSample, SimulationRecord[] Records, RejectedSample[] Rejected);
public sealed record GenerationProgress(int Completed, int Resumed, int Records, int Rejected, int Total, int ActiveMatch);

public static class SimulationFiles
{
    public static JsonSerializerOptions Json { get; } = SnapshotJson.CreateOptions();

    public static T Read<T>(string path)
    {
        using var file = File.OpenRead(path);
        using var zip = new GZipStream(file, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<T>(zip, Json) ?? throw new InvalidDataException($"Empty checkpoint: {path}");
    }
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
                    JsonSerializer.Serialize(zip, value, Json);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static int Seed(int root, int id, int stream)
    {
        Span<byte> bytes = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, root);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], id);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], stream);
        return BinaryPrimitives.ReadInt32LittleEndian(SHA256.HashData(bytes)) & int.MaxValue;
    }
}

public static class GenerationRunner
{
    public static async Task Run(GenerationConfig config, string directory, Action<GenerationProgress>? progress = null, CancellationToken ct = default,
        ReplayCorpus? corpus = null)
    {
        config.Validate();
        if (corpus is not null && (corpus.Count == 0 || corpus.Manifest.TargetRules != config.Rules))
            throw new InvalidDataException("Replay corpus is empty or target rules differ from the training configuration.");
        Directory.CreateDirectory(directory);
        // One process per output directory; use distinct directories for shards.
        using var runLock = new FileStream(Path.Combine(directory, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var fingerprint = RunManifest.Identity(config, corpus?.Fingerprint);
        var manifestPath = Path.Combine(directory, "manifest.json.gz");
        if (File.Exists(manifestPath) && SimulationFiles.Read<RunManifest>(manifestPath).Fingerprint != fingerprint)
            throw new InvalidDataException("Run settings differ from this directory's manifest. Use a new output directory.");
        SimulationFiles.Write(manifestPath, new RunManifest(1, fingerprint, config.Rules.Profile(config.Weights, config.Search.PlacementWeight), config, corpus?.Fingerprint));
        var matchCount = corpus is null ? config.Matches : Math.Min(config.Matches, corpus.Count);
        var jobs = Enumerable.Range(0, matchCount).Where(id => id % config.ShardCount == config.ShardIndex);
        var total = matchCount / config.ShardCount + (config.ShardIndex < matchCount % config.ShardCount ? 1 : 0);
        var completed = 0;
        var resumed = 0;
        var records = 0;
        var rejected = 0;
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = config.Workers, CancellationToken = ct }, (id, token) =>
        {
            var name = id.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + ".json.gz";
            var finishedPath = Path.Combine(directory, "completed", name);
            var pendingPath = Path.Combine(directory, "pending", name);
            GenerationJob result;
            if (File.Exists(finishedPath))
            {
                result = SimulationFiles.Read<GenerationJob>(finishedPath);
                if (result.Schema != 1 || result.Fingerprint != fingerprint || result.MatchId != id)
                    throw new InvalidDataException($"Incompatible completed job {id}.");
                Interlocked.Increment(ref resumed);
            }
            else
            {
                GenerationCheckpoint checkpoint;
                if (File.Exists(pendingPath))
                {
                    checkpoint = SimulationFiles.Read<GenerationCheckpoint>(pendingPath);
                    if (checkpoint.Fingerprint != fingerprint || checkpoint.MatchId != id)
                        throw new InvalidDataException($"Incompatible pending job {id}.");
                }
                else
                {
                    var seed = SimulationFiles.Seed(config.Seed, id, 0);
                    if (corpus is null)
                    {
                        var reservoir = new StratifiedReservoir(config.StatesPerMatch, new Random(SimulationFiles.Seed(config.Seed, id, 1)));
                        var match = MatchRunner.Play(config.Rules, seed, new SimulationPolicy(config.SelfPlayPolicy, config.Weights), token,
                            (view, legal) => reservoir.Add(view, legal), config.CheckEveryTransition);
                        checkpoint = new GenerationCheckpoint(fingerprint, id, seed, match, reservoir.Select(), 0, [], []);
                    }
                    else
                    {
                        var replay = corpus.Read(id);
                        checkpoint = new GenerationCheckpoint(fingerprint, id, seed, replay.Match,
                            ReplayCorpus.Select(replay, config.StatesPerMatch, SimulationFiles.Seed(config.Seed, id, 1)), 0, [], []);
                    }
                    SimulationFiles.Write(pendingPath, checkpoint);
                }
                var trained = checkpoint.Records.ToList();
                var failures = checkpoint.Rejected.ToList();
                for (var sample = checkpoint.NextSample; sample < checkpoint.Samples.Length; sample++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        trained.Add(new InformationSetSearch(config.Weights).Train(checkpoint.Samples[sample], config.Search,
                            SimulationFiles.Seed(config.Seed, id, sample + 2), token));
                    }
                    catch (BeliefSamplingException ex)
                    {
                        // Rejections are explicit and never written as zero-return labels.
                        failures.Add(new RejectedSample(sample, ex.Message));
                    }
                    checkpoint = checkpoint with { NextSample = sample + 1, Records = trained.ToArray(), Rejected = failures.ToArray() };
                    SimulationFiles.Write(pendingPath, checkpoint);
                    progress?.Invoke(new GenerationProgress(Volatile.Read(ref completed), Volatile.Read(ref resumed),
                        Volatile.Read(ref records), Volatile.Read(ref rejected), total, id));
                }
                result = new GenerationJob(1, fingerprint, id, checkpoint.Seed, checkpoint.Match, trained.ToArray(), failures.ToArray());
                SimulationFiles.Write(finishedPath, result);
                File.Delete(pendingPath);
            }
            Interlocked.Add(ref records, result.Records.Length);
            Interlocked.Add(ref rejected, result.Rejected.Length);
            var done = Interlocked.Increment(ref completed);
            progress?.Invoke(new GenerationProgress(done, Volatile.Read(ref resumed), Volatile.Read(ref records), Volatile.Read(ref rejected), total, id));
            return ValueTask.CompletedTask;
        });
    }

    private sealed class StratifiedReservoir(int capacity, Random random)
    {
        private readonly Dictionary<string, (int Seen, List<SimulationObservation> Samples)> buckets = new();
        public void Add(SimulationObservation view, IReadOnlyList<SimAction> legal)
        {
            if (legal.Count < 2 || legal.Any(a => a.Kind is SimActionKind.Tsumo or SimActionKind.Ron))
                return; // Deterministic forced moves/wins are not useful search targets.
            var s = view.Snapshot;
            var key = $"{view.Phase}:{Math.Min(2, s.Turn / 6)}:{s.Seats.Skip(1).Any(p => p.Riichi)}";
            var (seen, samples) = this.buckets.GetValueOrDefault(key, (0, new List<SimulationObservation>()));
            seen++;
            if (samples.Count < capacity)
                samples.Add(view);
            else
            {
                var index = random.Next(seen);
                if (index < capacity)
                    samples[index] = view;
            }
            this.buckets[key] = (seen, samples);
        }
        public SimulationObservation[] Select()
        {
            var queues = this.buckets.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value.Samples.ToList()).ToList();
            SimTiles.Shuffle(queues, random);
            foreach (var queue in queues)
                SimTiles.Shuffle(queue, random);
            var result = new List<SimulationObservation>();
            while (result.Count < capacity && queues.Any(q => q.Count > 0))
                foreach (var queue in queues.Where(q => q.Count > 0))
                {
                    result.Add(queue[^1]);
                    queue.RemoveAt(queue.Count - 1);
                    if (result.Count == capacity)
                        break;
                }
            return result.ToArray();
        }
    }
}
