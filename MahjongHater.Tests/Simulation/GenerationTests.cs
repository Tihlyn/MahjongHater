using System.Text.Json;
using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Precomputed;
using MahjongHater.Core.Simulation;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.Simulation;

public sealed class GenerationTests
{
    private static GenerationConfig Small() => new()
    {
        Seed = 51, Matches = 1, Workers = 1, StatesPerMatch = 2, Rules = new SimulationRules { HandsInMatch = 4 },
        Search = new SearchOptions { Iterations = 8, MinimumVisits = 1, Particles = 1, MaximumTreeNodes = 8 },
    };

    [Fact]
    public async Task Resume_reuses_finished_work_and_worker_count_does_not_change_results()
    {
        using var temporary = new TemporaryDirectory();
        var first = Path.Combine(temporary.Path, "first");
        var other = Path.Combine(temporary.Path, "other");
        var config = Small();
        await GenerationRunner.Run(config, first);
        var file = Directory.GetFiles(Path.Combine(first, "completed"), "*.json.gz").Single();
        var before = File.ReadAllBytes(file);
        GenerationProgress? progress = null;
        await GenerationRunner.Run(config with { Workers = 2 }, first, p => progress = p);
        Assert.Equal(1, progress!.Resumed);
        Assert.Equal(before, File.ReadAllBytes(file));
        await GenerationRunner.Run(config with { Workers = 2 }, other);
        Assert.Equal(before, File.ReadAllBytes(Directory.GetFiles(Path.Combine(other, "completed"), "*.json.gz").Single()));
        await Assert.ThrowsAsync<InvalidDataException>(() => GenerationRunner.Run(config with { Seed = 99 }, first));
    }

    [Fact]
    public async Task Cancellation_checkpoints_each_completed_state_and_resumes_identically()
    {
        using var temporary = new TemporaryDirectory();
        var resumed = Path.Combine(temporary.Path, "resumed");
        var reference = Path.Combine(temporary.Path, "reference");
        using var cancel = new CancellationTokenSource();
        var config = Small();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GenerationRunner.Run(config, resumed,
            p => { if (p.Completed == 0) cancel.Cancel(); }, cancel.Token));
        var pending = Directory.GetFiles(Path.Combine(resumed, "pending"), "*.json.gz").Single();
        Assert.Equal(1, SimulationFiles.Read<GenerationCheckpoint>(pending).NextSample);
        await GenerationRunner.Run(config, resumed);
        await GenerationRunner.Run(config, reference);
        var a = SimulationFiles.Read<GenerationJob>(Directory.GetFiles(Path.Combine(resumed, "completed"), "*.json.gz").Single());
        var b = SimulationFiles.Read<GenerationJob>(Directory.GetFiles(Path.Combine(reference, "completed"), "*.json.gz").Single());
        Assert.Equal(JsonSerializer.Serialize(b, SimulationFiles.Json), JsonSerializer.Serialize(a, SimulationFiles.Json));
        Assert.False(File.Exists(pending));
    }

    [Fact]
    public async Task Packed_database_round_trips_with_external_sort_and_ignores_duplicate_shards()
    {
        using var temporary = new TemporaryDirectory();
        var run = Path.Combine(temporary.Path, "run");
        var packed = Path.Combine(temporary.Path, "database");
        var config = Small();
        await GenerationRunner.Run(config, run);
        var job = SimulationFiles.Read<GenerationJob>(Directory.GetFiles(Path.Combine(run, "completed"), "*.json.gz").Single());
        Assert.Empty(job.Rejected);
        Assert.Equal(config.StatesPerMatch, job.Records.Length);
        var manifest = SimulationDatabase.Pack([run, run], packed, sortBufferEntries: 2);
        Assert.Equal(job.Records.Length, manifest.InputRecords);
        using (var database = new SimulationDatabase(packed, manifest.Profile, verifyChecksums: true))
        {
            foreach (var record in job.Records)
            {
                Assert.True(database.TryExact(record.Exact, out var loaded));
                Assert.Equal(JsonSerializer.Serialize(record), JsonSerializer.Serialize(loaded));
                Assert.True(database.TryAbstract(record.AbstractKey, out loaded));
                Assert.Equal(record.AbstractKey, loaded!.AbstractKey);
                Assert.False(database.TryExact(record.Exact with { Hash = new string('0', 64) }, out _));
            }
            await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() =>
                Assert.True(database.TryExact(job.Records[i % job.Records.Length].Exact, out _)))));
        }
        Assert.Throws<InvalidDataException>(() => new SimulationDatabase(packed, "wrong-profile"));
        Assert.Throws<IOException>(() => SimulationDatabase.Pack([run], packed));
        var data = Path.Combine(packed, "data.bin");
        using (var file = new FileStream(data, FileMode.Open, FileAccess.Write)) file.WriteByte(0);
        Assert.Throws<InvalidDataException>(() => new SimulationDatabase(packed, verifyChecksums: true));
    }

    [Fact]
    public void Run_identity_excludes_operational_limits_but_includes_rules_and_training_parameters()
    {
        var config = Small();
        Assert.Equal(config.Fingerprint(), (config with { Workers = 12, Matches = 10000, ShardCount = 4, ShardIndex = 2 }).Fingerprint());
        Assert.NotEqual(config.Fingerprint(), (config with { Search = config.Search with { Iterations = 1024 } }).Fingerprint());
        Assert.NotEqual(config.Fingerprint(), (config with { Rules = config.Rules with { Kuitan = false } }).Fingerprint());
        Assert.NotEqual(config.Fingerprint(), (config with { Weights = config.Weights with { RiichiMinUkeire = 4 } }).Fingerprint());
    }

    [Fact]
    public async Task Live_lookup_falls_back_when_context_changes()
    {
        // A real generated row can be probed, while changed context must fall back.
        using var temporary = new TemporaryDirectory();
        var run = Path.Combine(temporary.Path, "run");
        var packed = Path.Combine(temporary.Path, "database");
        await GenerationRunner.Run(Small(), run);
        var job = SimulationFiles.Read<GenerationJob>(Directory.GetFiles(Path.Combine(run, "completed"), "*.json.gz").Single());
        SimulationDatabase.Pack([run], packed);
        using var database = new SimulationDatabase(packed);
        var fallback = new SentinelPolicy();
        var policy = new SimulationLookupPolicy(fallback, database, minimumVisits: 1);
        foreach (var record in job.Records)
        {
            var state = SnapshotJson.Deserialize(record.Snapshot);
            var result = policy.Choose(state with { Honba = state.Honba + 100 }, default);
            Assert.Equal(ActionKind.Pass, result.Kind);
        }
        Assert.Equal(job.Records.Length, fallback.Calls);
    }

    [Fact]
    public void Live_lookup_uses_full_coverage_rows_and_rejects_insufficient_samples()
    {
        using var temporary = new TemporaryDirectory();
        var config = Small() with { Rules = new SimulationRules() };
        var game = RiichiSimulator.Deal(config.Rules, new Random(17));
        var view = SimulationObservation.Observe(game);
        Assert.True(DiscardActionSpace.Supports(view.Snapshot));
        var record = new InformationSetSearch().Train(view, config.Search, 71);
        var run = System.IO.Path.Combine(temporary.Path, "run");
        SimulationFiles.Write(System.IO.Path.Combine(run, "manifest.json.gz"), new RunManifest(1, config.Fingerprint(), record.Profile, config));
        var match = new MatchResult(1, 1, [25000, 25000, 25000, 25000], [1, 2, 3, 4], 0, []);
        SimulationFiles.Write(System.IO.Path.Combine(run, "completed", "0000000000.json.gz"), new GenerationJob(1, config.Fingerprint(), 0, 1, match, [record], []));
        var packed = System.IO.Path.Combine(temporary.Path, "db");
        SimulationDatabase.Pack([run], packed);
        using var database = new SimulationDatabase(packed);
        var fallback = new SentinelPolicy();
        var choice = new SimulationLookupPolicy(fallback, database, minimumVisits: 1).Choose(view.Snapshot, default);
        Assert.Equal(ActionKind.Discard, choice.Kind);
        Assert.Equal(record.Actions.Max(a => a.MeanUtility), choice.Candidates[0].Score);
        Assert.Equal(0, fallback.Calls);
        new SimulationLookupPolicy(fallback, database, minimumVisits: 8).Choose(view.Snapshot, default);
        Assert.Equal(1, fallback.Calls);
    }

    private sealed class SentinelPolicy : IPolicy
    {
        public int Calls;
        public ActionChoice Choose(StateSnapshot state, CancellationToken ct) { this.Calls++; return ActionChoice.Pass("sentinel"); }
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string parent = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MahjongHater-SimulationTests"));
        public string Path { get; }
        public TemporaryDirectory()
        {
            this.Path = System.IO.Path.Combine(this.parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this.Path);
        }
        public void Dispose()
        {
            var target = System.IO.Path.GetFullPath(this.Path);
            if (!target.StartsWith(this.parent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped its temporary directory.");
            Directory.Delete(target, recursive: true);
        }
    }
}
