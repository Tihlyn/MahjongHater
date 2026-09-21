using System.Collections.Frozen;
using System.Text.Json;

namespace MahjongHater.Core.Precomputed;

public sealed record StoredDiscard(string Tile, int Visits, double MeanUtility, double Variance,
    int Shanten, int Ukeire, string[] Waits);

public sealed record PolicyEntry(StateIdentity State, StoredDiscard[] Actions);

public sealed record PolicyArtifact(int Schema, string Model, string Profile, int Seed, int Iterations, int Horizon, PolicyEntry[] Entries)
{
    public double Exploration { get; init; } = 1.4;
}

// Portable JSON on disk for the first corpus; immutable hash index in memory.
// Both identity digests must match. Unknown schema/model/profile is a load error.
public sealed class PolicyTable
{
    public const int Schema = 1;
    private readonly FrozenDictionary<StateIdentity, IReadOnlyList<StoredDiscard>> entries;

    public PolicyTable(PolicyArtifact artifact, string expectedProfile)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Schema != Schema || artifact.Model != BeliefState.ModelVersion || artifact.Profile != expectedProfile)
            throw new InvalidDataException("Precomputed policy schema, simulator or policy profile does not match.");
        if (artifact.Entries is null || artifact.Iterations < 1 || artifact.Horizon is < 1 or > 16
            || !double.IsFinite(artifact.Exploration) || artifact.Exploration < 0)
            throw new InvalidDataException("Invalid training metadata.");
        var rows = new Dictionary<StateIdentity, IReadOnlyList<StoredDiscard>>();
        foreach (var entry in artifact.Entries)
        {
            if (entry is null || entry.State is null || !Digest(entry.State.Hash) || !Digest(entry.State.Verification)
                || entry.Actions is null || entry.Actions.Length is < 1 or > 14)
                throw new InvalidDataException("Invalid precomputed state entry.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in entry.Actions)
            {
                if (action is null || !TileCode(action.Tile) || !seen.Add(action.Tile) || action.Visits < 1
                    || !double.IsFinite(action.MeanUtility) || Math.Abs(action.MeanUtility) > 48000
                    || !double.IsFinite(action.Variance) || action.Variance < 0
                    || action.Shanten is < 0 or > 6 || action.Ukeire is < 0 or > 136
                    || action.Waits is null || action.Waits.Length > 34 || action.Waits.Any(t => !TileCode(t)))
                    throw new InvalidDataException("Invalid precomputed action statistics.");
            }
            // Deep copy the externally supplied arrays before exposing an immutable index.
            if (!rows.TryAdd(entry.State, Array.AsReadOnly(entry.Actions.Select(a => a with { Waits = a.Waits.ToArray() }).ToArray())))
                throw new InvalidDataException("Duplicate precomputed state.");
        }
        this.entries = rows.ToFrozenDictionary();
    }

    public int Count => this.entries.Count;

    public bool TryGet(StateIdentity state, out IReadOnlyList<StoredDiscard> actions)
    {
        if (!this.entries.TryGetValue(state, out var found))
        {
            actions = [];
            return false;
        }
        actions = found.Select(a => a with { Waits = a.Waits.ToArray() }).ToArray();
        return true;
    }

    public static PolicyTable Load(string path, string profile)
    {
        using var stream = File.OpenRead(path);
        var artifact = JsonSerializer.Deserialize<PolicyArtifact>(stream)
            ?? throw new InvalidDataException("Empty precomputed policy artifact.");
        return new PolicyTable(artifact, profile);
    }

    public static void Save(string path, PolicyArtifact artifact)
    {
        _ = new PolicyTable(artifact, artifact.Profile);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
                JsonSerializer.Serialize(stream, artifact, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static bool Digest(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static bool TileCode(string? s) => s is { Length: 2 } &&
        (s[1] is 'm' or 'p' or 's' && s[0] is >= '0' and <= '9' || s[1] == 'z' && s[0] is >= '1' and <= '7');
}
