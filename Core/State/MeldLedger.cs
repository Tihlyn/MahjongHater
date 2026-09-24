namespace MahjongHater.Core.State;

internal sealed record MeldObservation(Meld Meld, int? Slot, int? FromDirection);

// Events supply composition; the struct supplies count and slot identity. Keep both:
// identical chis in different slots are distinct, while a repeated refresh is not.
internal sealed class MeldLedger
{
    private readonly List<MeldObservation> entries = [];
    private readonly Dictionary<int, Tile> upgrades = [];
    public IReadOnlyList<MeldObservation> Entries => this.entries;
    public IReadOnlyList<Meld> Melds => this.entries.Select(e => e.Meld).ToArray();
    public int Count => this.entries.Count;

    public bool Record(Meld meld, int? slot = null, int? from = null)
    {
        if (meld.Type == MeldType.Pon && this.upgrades.TryGetValue(TileHelpers.ToIndex(meld.Tiles[0]), out var fourth))
            meld = new Meld(MeldType.Shouminkan, [.. meld.Tiles, fourth], true);
        var signature = MeldInference.Signature(meld);
        var index = slot is { } s ? this.entries.FindIndex(e => e.Slot == s) : -1;
        if (index < 0)
            index = this.entries.FindIndex(e => e.Slot == null && MeldInference.Signature(e.Meld) == signature);
        if (index >= 0)
        {
            var old = this.entries[index];
            // A duplicate old pon event must not undo its later added-kan refresh.
            if (old.Meld.Type == MeldType.Shouminkan && meld.Type == MeldType.Pon
                && TileHelpers.SameKind(old.Meld.Tiles[0], meld.Tiles[0]))
                return false;
            var replacement = new MeldObservation(meld, slot ?? old.Slot, from ?? old.FromDirection);
            if (MeldInference.Signature(old.Meld) == signature && old.Slot == replacement.Slot
                && old.FromDirection == replacement.FromDirection)
                return false;
            this.entries[index] = replacement;
        }
        else
        {
            if (this.entries.Count == 4) return false;
            this.entries.Add(new MeldObservation(meld, slot, from));
        }
        this.entries.Sort((a, b) => (a.Slot ?? 4).CompareTo(b.Slot ?? 4));
        return true;
    }

    public bool Upgrade(Tile tile)
    {
        this.upgrades[TileHelpers.ToIndex(tile)] = tile;
        var index = this.entries.FindIndex(e => e.Meld.Type is MeldType.Pon or MeldType.Shouminkan
            && TileHelpers.SameKind(e.Meld.Tiles[0], tile));
        // Do not invent the original pon's composition (especially red fives).
        // Retain the upgrade so a late original event can complete it.
        if (index < 0) return false;
        var old = this.entries[index];
        if (old.Meld.Type == MeldType.Shouminkan) return false;
        this.entries[index] = old with { Meld = new Meld(MeldType.Shouminkan, [.. old.Meld.Tiles, tile], true) };
        return true;
    }

    public void Trim(int count)
    {
        count = Math.Clamp(count, 0, 4);
        if (count == 0) this.upgrades.Clear();
        this.entries.RemoveAll(e => e.Slot >= count);
        if (this.entries.Count > count) this.entries.RemoveRange(count, this.entries.Count - count);
    }

    public void Clear()
    {
        this.entries.Clear();
        this.upgrades.Clear();
    }
}

internal static class MeldReconciler
{
    public static List<Meld> Resolve(int seat, SeatPanel panel, IReadOnlyList<MeldObservation> observed,
        List<string> notes, out bool complete)
    {
        if (panel.MeldCount == null)
        {
            complete = true;
            return observed.Select(e => e.Meld).ToList();
        }

        var result = new List<Meld>(4);
        var used = new HashSet<MeldObservation>();
        var count = Math.Clamp(panel.MeldCount.Value, 0, 4);
        complete = count == panel.MeldCount;
        for (var slot = 0; slot < count; slot++)
        {
            var record = slot < panel.Melds.Count ? panel.Melds[slot] : null;
            bool Matches(MeldObservation e)
            {
                if (used.Contains(e) || (e.Slot is { } s && s != slot)) return false;
                if (record == null || record.IsEmpty) return e.Slot == slot;
                if (e.FromDirection is { } from && from != record.FromDirection) return false;
                if (record.Tile is { } tile)
                    return !e.Meld.IsSequence && TileHelpers.SameKind(e.Meld.Tiles[0], tile);
                // 255 carries no single tile identity: both chi and concealed kan use it.
                return (record.NeedsComposition || e.Slot == slot) && (record.FromDirection == 0
                    ? e.Meld.Type == MeldType.Ankan : e.Meld.IsOpen);
            }

            var candidates = observed.Where(Matches).ToArray();
            var exact = candidates.FirstOrDefault(e => e.Slot == slot);
            var chosen = exact ?? (candidates.Select(e => MeldInference.Signature(e.Meld)).Distinct().Count() == 1
                ? candidates.FirstOrDefault() : null);
            if (chosen != null)
            {
                result.Add(chosen.Meld);
                used.Add(chosen);
            }
            else
            {
                complete = false;
                notes.Add($"seat {seat} meld {slot}: unresolved composition (index={record?.TileIndex}, from={record?.FromDirection})");
            }
        }
        if (count != panel.MeldCount) notes.Add($"seat {seat}: invalid meld count {panel.MeldCount}");
        return result;
    }
}
