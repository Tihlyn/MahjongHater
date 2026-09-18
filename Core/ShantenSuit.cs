using System.Collections.Concurrent;

namespace MahjongHater.Core;

// Enumerates every reachable (melds, partials) decomposition of a single tile group
// (one number suit, or the honors block). Groups are independent, so full-hand shanten
// combines per-group feasibility tables — exact by construction, no global search.
internal static class SuitSplitter
{
    // Feasibility tables repeat heavily across hands; cache keyed on packed counts.
    // Key: 3 bits per kind (counts 0-4) | bit 30 = allowRuns.
    private static readonly ConcurrentDictionary<uint, ushort[]> Cache = new();

    // feas: length 5; bit p of feas[m] set ⇔ the group can yield m melds and p partials.
    // Partials are pairs and two-tile proto-runs (honors: pairs only). Both axes are
    // downward closed: dropping a block's tiles is always a legal decomposition too.
    public static void Decompose(ReadOnlySpan<int> counts, bool allowRuns, Span<ushort> feas)
    {
        var key = Pack(counts, allowRuns);
        if (Cache.TryGetValue(key, out var cached))
        {
            cached.CopyTo(feas);
            return;
        }

        feas.Clear();
        Span<int> work = stackalloc int[counts.Length];
        counts.CopyTo(work);
        Dfs(work, 0, 0, 0, allowRuns, feas);
        Cache.TryAdd(key, feas.ToArray());
    }

    private static uint Pack(ReadOnlySpan<int> counts, bool allowRuns)
    {
        var key = allowRuns ? 1u << 30 : 0u;
        for (var i = 0; i < counts.Length; i++)
            key |= (uint)counts[i] << (i * 3);
        return key;
    }

    private static void Dfs(Span<int> c, int i, int melds, int partials, bool allowRuns, Span<ushort> feas)
    {
        while (i < c.Length && c[i] == 0)
            i++;

        if (i >= c.Length)
        {
            feas[Math.Min(melds, 4)] |= (ushort)(1 << Math.Min(partials, 4));
            return;
        }

        if (c[i] >= 3)
        {
            c[i] -= 3;
            Dfs(c, i, melds + 1, partials, allowRuns, feas);
            c[i] += 3;
        }

        if (allowRuns && (i % 9) <= 6 && c[i + 1] > 0 && c[i + 2] > 0)
        {
            c[i]--; c[i + 1]--; c[i + 2]--;
            Dfs(c, i, melds + 1, partials, allowRuns, feas);
            c[i]++; c[i + 1]++; c[i + 2]++;
        }

        if (c[i] >= 2)
        {
            c[i] -= 2;
            Dfs(c, i, melds, partials + 1, allowRuns, feas);
            c[i] += 2;
        }

        if (allowRuns && (i % 9) <= 7 && c[i + 1] > 0)
        {
            c[i]--; c[i + 1]--;
            Dfs(c, i, melds, partials + 1, allowRuns, feas);
            c[i]++; c[i + 1]++;
        }

        if (allowRuns && (i % 9) <= 6 && c[i + 2] > 0)
        {
            c[i]--; c[i + 2]--;
            Dfs(c, i, melds, partials + 1, allowRuns, feas);
            c[i]++; c[i + 2]++;
        }

        c[i]--;
        Dfs(c, i, melds, partials, allowRuns, feas);
        c[i]++;
    }
}
