using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Precomputed;

namespace MahjongHater.Core.Simulation;

public sealed record DatabaseManifest(int Schema, string Model, string Profile, string RunFingerprint,
    SimulationRules Rules, int InputRecords, int RejectedSamples, long IndexEntries, long DataBytes, string DataSha256, string IndexSha256);

// Immutable on-disk database. Binary search reads 48-byte index entries directly;
// only the selected JSON record is loaded. The whole corpus never enters RAM.
public sealed class SimulationDatabase : IDisposable
{
    private const int Width = 48;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MHIDX001");
    private readonly FileStream data;
    private readonly FileStream index;
    public DatabaseManifest Manifest { get; }

    public SimulationDatabase(string directory, string? expectedProfile = null, bool verifyChecksums = false)
    {
        this.Manifest = SimulationFiles.Read<DatabaseManifest>(Path.Combine(directory, "manifest.json.gz"));
        if (this.Manifest.Schema != 1 || this.Manifest.Model != SimulationRules.Version
            || expectedProfile is not null && this.Manifest.Profile != expectedProfile)
            throw new InvalidDataException("Simulation database schema, model or profile mismatch.");
        this.data = File.OpenRead(Path.Combine(directory, "data.bin"));
        try
        {
            this.index = File.OpenRead(Path.Combine(directory, "index.bin"));
            var header = new byte[16];
            this.index.ReadExactly(header);
            if (!header.AsSpan(0, 8).SequenceEqual(Magic) || BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8)) != this.Manifest.IndexEntries
                || this.index.Length != checked(16 + Width * this.Manifest.IndexEntries) || this.data.Length != this.Manifest.DataBytes)
                throw new InvalidDataException("Simulation database is incomplete or corrupt.");
            if (verifyChecksums && (Digest(this.data.Name) != this.Manifest.DataSha256 || Digest(this.index.Name) != this.Manifest.IndexSha256))
                throw new InvalidDataException("Simulation database checksum mismatch.");
        }
        catch
        {
            this.data.Dispose();
            this.index?.Dispose();
            throw;
        }
    }

    public bool TryExact(StateIdentity identity, out SimulationRecord? record)
    {
        record = this.Find(IndexKey("exact", identity.Verification));
        if (record?.Exact == identity)
            return true;
        record = null;
        return false;
    }
    // For research and explicit evaluation; live play does not enable approximate
    // retrieval automatically. Matching buckets do not imply interchangeable EV.
    public bool TryAbstract(string key, out SimulationRecord? record)
    {
        record = this.Find(IndexKey("abstract", key));
        if (record?.AbstractKey == key)
            return true;
        record = null;
        return false;
    }
    public void Dispose() { this.data.Dispose(); this.index.Dispose(); }

    private SimulationRecord? Find(byte[] key)
    {
        long low = 0, high = this.Manifest.IndexEntries - 1;
        var bytes = new byte[Width];
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            ReadAt(this.index, bytes, 16 + middle * Width);
            var comparison = bytes.AsSpan(0, 32).SequenceCompareTo(key);
            if (comparison < 0) low = middle + 1;
            else if (comparison > 0) high = middle - 1;
            else
            {
                var offset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32));
                var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40));
                if (offset < 0 || length is < 1 or > 4194304 || offset > this.data.Length - length)
                    throw new InvalidDataException("Invalid database record extent.");
                var payload = new byte[length];
                ReadAt(this.data, payload, offset);
                var record = JsonSerializer.Deserialize<SimulationRecord>(payload, SimulationFiles.Json)
                    ?? throw new InvalidDataException("Null database record.");
                if (record.Profile != this.Manifest.Profile)
                    throw new InvalidDataException("Record profile mismatch.");
                return record;
            }
        }
        return null;
    }

    public static DatabaseManifest Pack(IEnumerable<string> runDirectories, string destination, CancellationToken ct = default, int sortBufferEntries = 100000)
    {
        if (sortBufferEntries < 2)
            throw new ArgumentOutOfRangeException(nameof(sortBufferEntries));
        var target = Path.GetFullPath(destination);
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("Database destination already exists. Publish to a new directory.");
        var temporary = target + ".building-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporary);
        var runs = new List<string>();
        var buffer = new List<IndexItem>(sortBufferEntries);
        RunManifest? manifest = null;
        var records = 0;
        var rejected = 0;
        var jobsSeen = new HashSet<int>();
        var dataPath = Path.Combine(temporary, "data.bin");
        using (var output = new FileStream(dataPath, FileMode.CreateNew, FileAccess.Write))
        {
            foreach (var directory in runDirectories)
            {
                var current = SimulationFiles.Read<RunManifest>(Path.Combine(directory, "manifest.json.gz"));
                manifest ??= current;
                if (current.Schema != 1 || current.Fingerprint != RunManifest.Identity(current.Config, current.CorpusSha256)
                    || current.Profile != current.Config.Rules.Profile(current.Config.Weights, current.Config.Search.PlacementWeight)
                    || current.Fingerprint != manifest.Fingerprint || current.Profile != manifest.Profile)
                    throw new InvalidDataException("Cannot pack runs with different training configurations.");
                foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, "completed"), "*.json.gz"))
                {
                    ct.ThrowIfCancellationRequested();
                    var job = SimulationFiles.Read<GenerationJob>(file);
                    if (job.Schema != 1 || job.Fingerprint != manifest.Fingerprint)
                        throw new InvalidDataException("Completed job does not match its manifest.");
                    if (!jobsSeen.Add(job.MatchId))
                        continue; // Overlapping shard copies must not multiply sample counts.
                    rejected += job.Rejected.Length;
                    foreach (var record in job.Records)
                    {
                        ValidateRecord(record, manifest);
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, SimulationFiles.Json);
                        var offset = output.Position;
                        output.Write(bytes);
                        buffer.Add(new IndexItem(IndexKey("exact", record.Exact.Verification), offset, bytes.Length, record.Iterations));
                        buffer.Add(new IndexItem(IndexKey("abstract", record.AbstractKey), offset, bytes.Length, record.Iterations));
                        records++;
                        if (buffer.Count >= sortBufferEntries)
                            FlushRun(buffer, runs, temporary);
                    }
                }
            }
            output.Flush(flushToDisk: true);
        }
        if (manifest is null || records == 0)
            throw new InvalidDataException($"No trained records to pack. Diagnostic workspace: {temporary}");
        if (buffer.Count > 0)
            FlushRun(buffer, runs, temporary);
        var indexPath = Path.Combine(temporary, "index.bin");
        var entries = MergeRuns(runs, indexPath, ct);
        foreach (var run in runs)
            File.Delete(run);
        var result = new DatabaseManifest(1, SimulationRules.Version, manifest.Profile, manifest.Fingerprint, manifest.Config.Rules,
            records, rejected, entries, new FileInfo(dataPath).Length, Digest(dataPath), Digest(indexPath));
        SimulationFiles.Write(Path.Combine(temporary, "manifest.json.gz"), result);
        ct.ThrowIfCancellationRequested();
        // Both paths are absolute siblings; only this newly-created private output
        // directory is moved, and the final target is required not to exist.
        Directory.Move(temporary, target);
        return result;
    }

    private static void ValidateRecord(SimulationRecord r, RunManifest manifest)
    {
        if (r.Profile != manifest.Profile || r.Actions.Length == 0 || r.Actions.Select(a => a.Action.Key).Distinct().Count() != r.Actions.Length
            || r.Actions.Sum(a => (long)a.Visits) != r.Iterations || r.Actions.Any(a => a.Visits < 1 || !double.IsFinite(a.MeanUtility)
                || !double.IsFinite(a.MeanScore) || !double.IsFinite(a.UtilityM2) || a.UtilityM2 < 0))
            throw new InvalidDataException("Invalid search statistics.");
        var state = SnapshotJson.Deserialize(r.Snapshot);
        var beliefs = BeliefState.Capture(state, new OpponentModel(manifest.Config.Weights));
        if (r.Exact != IncrementalStateKey.Create(state, beliefs, r.Profile)
            || r.AbstractKey != StrategicAbstraction.Key(state, beliefs, r.Actions.Select(a => a.Action).ToArray(), r.Profile))
            throw new InvalidDataException("Stored state identity does not match its public snapshot.");
    }
    private static void FlushRun(List<IndexItem> buffer, List<string> runs, string directory)
    {
        buffer.Sort(IndexComparer.Instance);
        var path = Path.Combine(directory, $"sort-{runs.Count:D6}.bin");
        using (var writer = new BinaryWriter(File.Create(path)))
            foreach (var item in buffer)
                WriteItem(writer, item);
        runs.Add(path);
        buffer.Clear();
    }
    private static long MergeRuns(List<string> runs, string destination, CancellationToken ct)
    {
        var readers = runs.Select(p => new BinaryReader(File.OpenRead(p))).ToArray();
        try
        {
            var queue = new PriorityQueue<(IndexItem Item, int Reader), IndexItem>(IndexComparer.Instance);
            for (var i = 0; i < readers.Length; i++)
                if (Next(readers[i]) is { } item)
                    queue.Enqueue((item, i), item);
            using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(0L);
            byte[]? previous = null;
            long count = 0;
            while (queue.TryDequeue(out var entry, out _))
            {
                ct.ThrowIfCancellationRequested();
                // Sort order selects the most-sampled row for duplicate keys.
                // It never pools correlated/repeated root searches as independent evidence.
                if (previous is null || !entry.Item.Key.AsSpan().SequenceEqual(previous))
                {
                    WriteItem(writer, entry.Item);
                    previous = entry.Item.Key;
                    count++;
                }
                if (Next(readers[entry.Reader]) is { } next)
                    queue.Enqueue((next, entry.Reader), next);
            }
            writer.Flush();
            stream.Position = 8;
            writer.Write(count);
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return count;
        }
        finally { foreach (var reader in readers) reader.Dispose(); }
    }
    private static IndexItem? Next(BinaryReader reader) => reader.BaseStream.Position == reader.BaseStream.Length ? null
        : new IndexItem(reader.ReadBytes(32), reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt32());
    private static void WriteItem(BinaryWriter writer, IndexItem item)
    {
        writer.Write(item.Key); writer.Write(item.Offset); writer.Write(item.Length); writer.Write(item.Visits);
    }
    private static byte[] IndexKey(string kind, string digest) => SHA256.HashData(Encoding.ASCII.GetBytes(kind + ":" + digest));
    private static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void ReadAt(FileStream stream, byte[] bytes, long offset)
    {
        var read = 0;
        while (read < bytes.Length)
        {
            var count = RandomAccess.Read(stream.SafeFileHandle, bytes.AsSpan(read), offset + read);
            if (count == 0)
                throw new EndOfStreamException("Truncated database.");
            read += count;
        }
    }
    private sealed record IndexItem(byte[] Key, long Offset, int Length, int Visits);
    private sealed class IndexComparer : IComparer<IndexItem>
    {
        public static IndexComparer Instance { get; } = new();
        public int Compare(IndexItem? x, IndexItem? y)
        {
            var compare = x!.Key.AsSpan().SequenceCompareTo(y!.Key);
            if (compare != 0) return compare;
            compare = y.Visits.CompareTo(x.Visits);
            return compare != 0 ? compare : x.Offset.CompareTo(y.Offset);
        }
    }
}
