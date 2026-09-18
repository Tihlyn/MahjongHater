using System.Text.Json;

namespace MahjongHater.Core;

// Self-learning map from a tile-face render key (texture path / uld asset / part id of the
// face image inside a tile node) to tile identity. Observations come from trusted sources
// (hover names, Layout-2 AtkValues hands, discard events); once a key is confidently
// resolved, hand tiles can be read straight from the node tree with no hover needed.
//
// Conflict-aware: every (key → tile) observation is counted; a key resolves only when one
// tile dominates (≥ MinObservations and ≥ DominanceRatio of all observations for that key).
// A mis-attributed observation therefore scatters and never resolves instead of poisoning
// the map. Not thread-safe — confine to the framework thread.
public sealed class TileFaceMap
{
    private const int MinObservations = 2;
    private const double DominanceRatio = 0.8;

    // key → (tile notation e.g. "5m"/"0m"/"3z" → observation count)
    private readonly Dictionary<string, Dictionary<string, int>> observations = [];
    private bool dirty;

    // Immutable snapshot of confidently-resolved keys, safe to read from any thread
    // (the render thread resolves faces for the highlight while learning happens on
    // the framework thread). Rebuilt whenever observations change.
    private volatile Dictionary<string, Tile> resolvedSnapshot = [];

    public IReadOnlyDictionary<string, Tile> Resolved => this.resolvedSnapshot;

    public int KeyCount => this.observations.Count;

    public int ResolvedCount => this.resolvedSnapshot.Count;

    public void Observe(string faceKey, Tile tile)
    {
        if (string.IsNullOrEmpty(faceKey))
            return;

        if (!this.observations.TryGetValue(faceKey, out var counts))
        {
            counts = [];
            this.observations[faceKey] = counts;
        }

        // Red fives share a kind with plain fives but may render with a distinct face;
        // sources disagree on redness (icon ids never encode it), so learn kinds only.
        var notation = TileHelpers.Normalize(tile).ToString();
        counts[notation] = counts.GetValueOrDefault(notation) + 1;
        this.dirty = true;
        this.RebuildSnapshot();
    }

    public bool TryResolve(string faceKey, out Tile tile)
    {
        tile = default;
        if (string.IsNullOrEmpty(faceKey) || !this.observations.TryGetValue(faceKey, out var counts) || counts.Count == 0)
            return false;

        var total = counts.Values.Sum();
        var (notation, count) = counts.MaxBy(kv => kv.Value);
        if (count < MinObservations || count < total * DominanceRatio)
            return false;

        try
        {
            tile = Tile.Parse(notation);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(path));
            if (loaded is null)
                return;

            this.observations.Clear();
            foreach (var (key, counts) in loaded)
                this.observations[key] = new Dictionary<string, int>(counts);
            this.dirty = false;
            this.RebuildSnapshot();
        }
        catch (Exception)
        {
            // A corrupt map is not worth crashing over — it re-learns.
        }
    }

    private void RebuildSnapshot()
    {
        var snapshot = new Dictionary<string, Tile>(this.observations.Count);
        foreach (var key in this.observations.Keys)
        {
            if (this.TryResolve(key, out var tile))
                snapshot[key] = tile;
        }

        this.resolvedSnapshot = snapshot;
    }

    public void Save(string path)
    {
        if (!this.dirty)
            return;

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this.observations, new JsonSerializerOptions { WriteIndented = true }));
            this.dirty = false;
        }
        catch (Exception)
        {
            // Retried on the next save point.
        }
    }

    public IEnumerable<string> DescribeEntries()
    {
        foreach (var (key, counts) in this.observations.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var resolved = this.TryResolve(key, out var tile) ? tile.ToString() : "?";
            var detail = string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}"));
            yield return $"{key} → {resolved}  [{detail}]";
        }
    }
}
