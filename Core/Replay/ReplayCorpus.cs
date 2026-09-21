using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MahjongHater.Core.Simulation;

namespace MahjongHater.Core.Replay;

public sealed record ReplayCorpusEntry(string Sha256, string FileSha256, int Decisions);
public sealed record ReplayImportFailure(string File, string Reason);
public sealed record ReplayCorpusManifest(int Schema, string Importer, SimulationRules TargetRules, int MinimumRank,
    ReplayCorpusEntry[] Games, int Duplicates, ReplayImportFailure[] Rejected, Dictionary<string, int> Skipped);
public sealed record ReplayLearningTargets(SimAction HumanAction, int ObservedHandDelta, int ObservedFinalPlacement, OpponentTrainingTarget[] Opponents);
public sealed record ReplayLearningRow(int Schema, string Game, string Split, int Hand, int Event, int SourceSeat, int SourceRank,
    SimulationObservation Inputs, SimAction[] LegalActions, ReplayLearningTargets Targets);

public sealed class ReplayCorpus
{
    public const string ImporterVersion = "tenhou-decisions-v2";   // v2: claim-window reactions added
    private const int MaxXmlBytes = 32 * 1024 * 1024;
    private readonly string directory;
    public ReplayCorpusManifest Manifest { get; }
    public string Fingerprint { get; }
    public int Count => this.Manifest.Games.Length;

    public static string Split(string gameHash)
    {
        var bucket = Convert.ToUInt32(gameHash[..8], 16) % 100;
        return bucket < 80 ? "train" : bucket < 90 ? "validation" : "test";
    }

    public int Export(string destination, string split = "all", CancellationToken ct = default)
    {
        if (split is not ("all" or "train" or "validation" or "test")) throw new ArgumentException("Split must be all, train, validation or test.");
        var path = Path.GetFullPath(destination);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Export destination exists.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var count = 0;
        try
        {
            using (var writer = new StreamWriter(new FileStream(temporary, FileMode.CreateNew)))
                for (var index = 0; index < this.Count; index++)
                {
                    var entry = this.Manifest.Games[index];
                    var partition = Split(entry.Sha256);
                    if (split != "all" && split != partition) continue;
                    var game = this.Read(index);
                    foreach (var d in game.Decisions)
                    {
                        ct.ThrowIfCancellationRequested();
                        var row = new ReplayLearningRow(1, game.Sha256, partition, d.Hand, d.Event, d.Seat, d.Rank, d.Observation, d.LegalActions,
                            new ReplayLearningTargets(d.ObservedAction, d.ObservedHandDelta, game.Match.Placement[d.Seat], d.OpponentTargets));
                        writer.WriteLine(JsonSerializer.Serialize(row, SimulationFiles.Json));
                        count++;
                    }
                }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path);
            return count;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public ReplayCorpus(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        this.Manifest = SimulationFiles.Read<ReplayCorpusManifest>(Path.Combine(this.directory, "manifest.json.gz"));
        if (this.Manifest.Schema != 1 || this.Manifest.Importer != ImporterVersion
            || this.Manifest.Games.Any(g => g.Sha256.Length != 64 || !g.Sha256.All(Uri.IsHexDigit))
            || this.Manifest.Games.Select(g => g.Sha256).Distinct().Count() != this.Count)
            throw new InvalidDataException("Invalid replay corpus manifest.");
        this.Fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this.Manifest, SimulationFiles.Json)));
    }

    public ReplayGame Read(int index)
    {
        var entry = this.Manifest.Games[index];
        var path = Path.Combine(this.directory, "games", entry.Sha256 + ".json.gz");
        using (var file = File.OpenRead(path))
            if (Convert.ToHexString(SHA256.HashData(file)) != entry.FileSha256) throw new InvalidDataException("Replay corpus checksum mismatch.");
        var game = SimulationFiles.Read<ReplayGame>(path);
        if (game.Schema != 1 || game.Sha256 != entry.Sha256 || game.Decisions.Length != entry.Decisions
            || game.Decisions.Any(d => d.Observation.Rules != this.Manifest.TargetRules))
            throw new InvalidDataException("Replay game does not match its manifest.");
        return game;
    }

    // Archive entries are read and de-duplicated by content hash on the calling thread;
    // parsing (the expensive part) and per-game serialization run on `workers` threads.
    // Output is order-independent: games are sorted by hash, counters are sums.
    public static ReplayCorpusManifest Import(string input, string destination, SimulationRules rules, int minimumRank = 0,
        Action<string>? progress = null, CancellationToken ct = default, int workers = 0)
    {
        rules.Validate();
        if (workers < 1) workers = Math.Max(1, Environment.ProcessorCount - 1);
        var target = Path.GetFullPath(destination);
        if (Directory.Exists(input) && target.StartsWith(Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Corpus destination must be outside the source directory.");
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Corpus destination exists; choose a new folder.");
        var temporary = target + ".building-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporary);
        var games = new List<ReplayCorpusEntry>();
        var rejected = new List<ReplayImportFailure>();
        var seen = new HashSet<string>();
        var gameIds = new HashSet<string>();
        var skipped = new Dictionary<string, int>();
        var duplicates = 0;
        var results = new object();
        using var queue = new BlockingCollection<(string Label, byte[] Bytes)>(boundedCapacity: 4 * workers);
        void Consume(string label, byte[] bytes)
        {
            try
            {
                var game = TenhouReplay.Parse(bytes, rules, minimumRank);
                lock (results)
                {
                    if (!gameIds.Add(game.Sha256)) { duplicates++; return; }
                    foreach (var (why, count) in game.Skipped) skipped[why] = skipped.GetValueOrDefault(why) + count;
                }
                if (game.Decisions.Length == 0) throw new NotSupportedException("No eligible decisions after rank/rule filters.");
                var path = Path.Combine(temporary, "games", game.Sha256 + ".json.gz");
                SimulationFiles.Write(path, game);
                string stored;
                using (var file = File.OpenRead(path)) stored = Convert.ToHexString(SHA256.HashData(file));
                lock (results)
                {
                    games.Add(new ReplayCorpusEntry(game.Sha256, stored, game.Decisions.Length));
                    progress?.Invoke($"Imported {label}: {game.Match.Hands} hands, {game.Decisions.Length} decisions.");
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or System.Xml.XmlException
                or FormatException or OverflowException or EndOfStreamException or ArgumentException)
            {
                lock (results)
                {
                    rejected.Add(new ReplayImportFailure(label, ex.Message));
                    progress?.Invoke($"Rejected {label}: {ex.Message}");
                }
            }
        }
        void Enqueue(string label, Stream source)
        {
            ct.ThrowIfCancellationRequested();
            using var expanded = new MemoryStream();
            try
            {
                var prefix = new byte[2];
                source.ReadExactly(prefix);
                using var packed = new MemoryStream();
                packed.Write(prefix);
                CopyLimited(source, packed, ct);
                packed.Position = 0;
                if (prefix[0] == 0x1f && prefix[1] == 0x8b)
                {
                    using var gzip = new GZipStream(packed, CompressionMode.Decompress, leaveOpen: true);
                    CopyLimited(gzip, expanded, ct);
                }
                else CopyLimited(packed, expanded, ct);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                lock (results) { rejected.Add(new ReplayImportFailure(label, ex.Message)); progress?.Invoke($"Rejected {label}: {ex.Message}"); }
                return;
            }
            var bytes = expanded.ToArray();
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            if (!seen.Add(digest)) { lock (results) duplicates++; return; }
            queue.Add((label, bytes), ct);
        }
        var files = Directory.Exists(input) ? Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories)
            .Where(f => new[] { ".xml", ".mjlog", ".gz", ".zip", ".txt" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Order(StringComparer.Ordinal) : new[] { input }.AsEnumerable();
        var consumers = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            foreach (var (label, bytes) in queue.GetConsumingEnumerable(ct)) Consume(label, bytes);
        }, ct)).ToArray();
        try
        {
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                using var file = File.OpenRead(path);
                if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    // Stream entries directly: no extraction paths or archive traversal.
                    using var archive = new ZipArchive(file, ZipArchiveMode.Read);
                    foreach (var entry in archive.Entries.Where(e => e.Length > 0).OrderBy(e => e.FullName, StringComparer.Ordinal))
                    {
                        using var stream = entry.Open();
                        Enqueue(Path.GetFileName(path) + "/" + entry.FullName, stream);
                    }
                }
                else Enqueue(Path.GetFileName(path), file);
            }
        }
        finally { queue.CompleteAdding(); }
        Task.WaitAll(consumers, ct);
        var manifest = new ReplayCorpusManifest(1, ImporterVersion, rules, minimumRank, games.OrderBy(g => g.Sha256, StringComparer.Ordinal).ToArray(),
            duplicates, rejected.OrderBy(r => r.File, StringComparer.Ordinal).ToArray(), skipped);
        SimulationFiles.Write(Path.Combine(temporary, "manifest.json.gz"), manifest);
        ct.ThrowIfCancellationRequested();
        Directory.Move(temporary, target);
        return manifest;
    }

    private static void CopyLimited(Stream source, Stream target, CancellationToken ct)
    {
        var buffer = new byte[65536];
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (target.Length + read > MaxXmlBytes) throw new InvalidDataException("Replay exceeds the 32 MiB input limit.");
            target.Write(buffer, 0, read);
        }
    }

    public static SimulationObservation[] Select(ReplayGame game, int count, int seed)
    {
        // Selecting by public context only avoids preferring eventual winners or
        // particular observed actions. Split evaluation by game hash, never rows.
        var random = new Random(seed);
        var buckets = game.Decisions.GroupBy(d => $"{d.Observation.Snapshot.Us.Melds.Count > 0}:{Math.Min(2, d.Observation.Snapshot.Turn / 6)}:{d.Observation.Players.Skip(1).Any(p => p.Riichi)}")
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.ToList()).ToList();
        SimTiles.Shuffle(buckets, random);
        foreach (var bucket in buckets) SimTiles.Shuffle(bucket, random);
        var result = new List<SimulationObservation>();
        while (result.Count < count && buckets.Any(b => b.Count > 0))
            foreach (var bucket in buckets.Where(b => b.Count > 0))
            {
                result.Add(bucket[^1].Observation); bucket.RemoveAt(bucket.Count - 1);
                if (result.Count == count) break;
            }
        return result.ToArray();
    }
}
