using MahjongHater.Core.Policy;
using MahjongHater.Core.Simulation;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Learning;

// The exporter and live inference share this implementation. No replay targets,
// hidden hands, shuffle seeds, future outcomes or ura indicators are inputs.
//
// v2 ("public-tiles-v2") adds eight look-ahead planes (the information Suphx credits its
// input representation for: what each discard or claim does to our own hand) and a
// reaction action space (pass / pon / open kan / chi shapes / own-turn kans) so call
// decisions can be learned. v1 stays available for artifacts trained on it.
public static class LearningFeatures
{
    public const string Version = "public-tiles-v2";
    public const string LegacyVersion = "public-tiles-v1";
    public const int Channels = 72;
    public const int LegacyChannels = 64;
    public const int Width = 34;
    public const int Count = Channels * Width;
    public const int LegacyCount = LegacyChannels * Width;
    // 37 physical kinds (red fives separate) × {discard, riichi} + 8 reaction/kan actions.
    public const int Actions = 82;
    public const int LegacyActions = 74;
    public const int PassAction = 74, PonAction = 75, OpenKanAction = 76, ChiLowAction = 77, ChiMiddleAction = 78, ChiHighAction = 79, ClosedKanAction = 80, AddedKanAction = 81;
    // Storage form of a v2 row: planes 0-31 as they are, the 32 broadcast global planes as one
    // value each, planes 64-67 as they are, the 4 broadcast look-ahead planes as one value each.
    // 1 260 values instead of 2 448; the trainer expands on the device, the plugin never sees it.
    public const int CompactCount = 32 * Width + 32 + 4 * Width + 4;

    public static bool IsKnownVersion(string version) => version is Version or LegacyVersion;

    public static int CountFor(string version) => version == LegacyVersion ? LegacyCount : Count;

    public static int ChannelsFor(string version) => version == LegacyVersion ? LegacyChannels : Channels;

    public static int ActionsFor(string version) => version == LegacyVersion ? LegacyActions : Actions;

    public static int ActionIndex(SimAction action) => ActionIndex(action, Version);

    public static int ActionIndex(SimAction action, string version)
    {
        if (action.Tile is not null && action.Kind is SimActionKind.Discard or SimActionKind.Riichi)
            return SimTiles.Physical(Tile.Parse(action.Tile)) + (action.Kind == SimActionKind.Riichi ? 37 : 0);
        if (version == LegacyVersion)
            return -1;
        switch (action.Kind)
        {
            case SimActionKind.Pass: return PassAction;
            case SimActionKind.Pon: return PonAction;
            case SimActionKind.OpenKan: return OpenKanAction;
            case SimActionKind.ClosedKan: return ClosedKanAction;
            case SimActionKind.AddedKan: return AddedKanAction;
            case SimActionKind.Chi:
            {
                if (action.Tile is null) return -1;
                var called = Tile.Parse(action.Tile).Number;
                var consumed = action.ConsumedTiles().Select(t => t.Number).ToArray();
                if (consumed.Length != 2) return -1;
                return called < consumed.Min() ? ChiLowAction : called > consumed.Max() ? ChiHighAction : ChiMiddleAction;
            }
            default: return -1;
        }
    }

    public static float[] Encode(StateSnapshot s) => Encode(s, Version);

    public static float[] Encode(StateSnapshot s, string version)
    {
        if (!IsKnownVersion(version))
            throw new ArgumentException($"Unknown feature version '{version}'.");
        if (s.Seats.Count != 4 || s.Seats.Where((seat, i) => seat.Seat != i).Any())
            throw new ArgumentException("Learning inputs require four relative seats.");
        var x = new float[CountFor(version)];
        void Add(int c, Tile t, float v) => x[c * Width + TileHelpers.ToIndex(t)] += v;
        foreach (var t in s.Hand) { Add(0, t, .25f); if (t.IsRedFive) Add(1, t, 1); }
        if (s.DrawnTile is { } draw) Add(2, draw, 1);
        foreach (var t in s.DoraIndicators) { Add(3, t, .2f); Add(4, TileDangerModel.DoraOf(t), .2f); }
        var visible = new int[34];
        foreach (var t in s.Hand.Concat(s.SeenForAnalyzer()).Concat(s.OurMelds.SelectMany(m => m.Tiles)).Concat(s.DoraIndicators))
        {
            Add(5, t, .25f);
            visible[TileHelpers.ToIndex(t)]++;
        }
        foreach (var t in s.OurMelds.SelectMany(m => m.Tiles)) Add(6, t, .25f);
        if (s.CallTile is { } call) Add(7, call, 1);
        foreach (var seat in s.Seats)
        {
            var c = 8 + seat.Seat * 6;
            for (var i = 0; i < seat.Discards.Count; i++)
            {
                var t = seat.Discards[i];
                Add(c, t, .25f);
                x[(c + 1) * Width + TileHelpers.ToIndex(t)] = (i + 1) / 24f;
            }
            foreach (var t in seat.Melds.SelectMany(m => m.Tiles)) { Add(c + 2, t, .25f); if (t.IsRedFive) Add(c + 3, t, 1); }
            if (seat.Riichi && seat.RiichiDiscardIndex >= 0 && seat.RiichiDiscardIndex < seat.Discards.Count)
                Add(c + 4, seat.Discards[seat.RiichiDiscardIndex], 1);
            // Known safe after riichi, using global chronology when available.
            if (seat.RiichiDiscardOrder >= 0)
                foreach (var other in s.Seats.Where(p => p.Seat != seat.Seat))
                    for (var i = 0; i < Math.Min(other.Discards.Count, other.DiscardOrder.Count); i++)
                        if (other.DiscardOrder[i] > seat.RiichiDiscardOrder)
                            x[(c + 5) * Width + TileHelpers.ToIndex(other.Discards[i])] = 1;
        }
        var globals = new List<float>();
        globals.AddRange(s.Seats.Select(p => p.Score / 50000f));
        globals.AddRange(s.Seats.Select(p => p.Riichi ? 1f : 0));
        globals.AddRange(s.Seats.Select(p => p.Melds.Count / 4f));
        globals.AddRange(s.Seats.Select(p => Math.Max(p.Discards.Count, p.DiscardCount) / 24f));
        globals.AddRange(s.Seats.Select(p => p.Seat == s.DealerSeat ? 1f : 0));
        globals.AddRange([(float)s.RoundWind / 4, (float)s.SeatWind / 4, s.HandNumber / 4f, s.WallRemaining / 70f,
            s.Honba / 10f, s.RiichiSticks / 10f, s.IsOpen ? 1f : 0, s.OurRiichi ? 1f : 0,
            s.Ruleset.Kuitan ? 1f : 0, s.Ruleset.HandsInMatch / 8f, s.IsAllLast ? 1f : 0, s.Turn / 24f]);
        for (var g = 0; g < globals.Count; g++) Array.Fill(x, globals[g], (32 + g) * Width, Width);
        if (version == Version)
            LookAhead(s, visible, x);
        return x;
    }

    // Planes 64-71: what each discard does to the hand (64 tenpai, 65 shanten, 66 ukeire,
    // 67 wait count), the hand as it stands (68 shanten, 69 ukeire, broadcast), and for a
    // claim window the best call outcome (70 shanten after the best claim, 71 it is tenpai).
    private static void LookAhead(StateSnapshot s, int[] visible, float[] x)
    {
        var availability = new int[34];
        for (var k = 0; k < 34; k++) availability[k] = Math.Max(0, 4 - visible[k]);
        var counts = new int[34];
        foreach (var t in s.Hand) counts[TileHelpers.ToIndex(t)]++;
        var melds = s.OurMelds.Count;
        var closed = s.Hand.Count + 3 * melds;
        if (closed == 14)
        {
            var best = 9;
            var bestUkeire = 0;
            foreach (var e in Shanten.EvaluateDiscards(counts, melds, availability))
            {
                var k = TileHelpers.ToIndex(e.Discard);
                x[64 * Width + k] = e.ShantenAfter == 0 ? 1 : 0;
                x[65 * Width + k] = Math.Clamp(e.ShantenAfter + 1, 0, 7) / 7f;
                x[66 * Width + k] = Math.Min(e.Ukeire, 40) / 40f;
                x[67 * Width + k] = e.ShantenAfter == 0 ? Math.Min(System.Numerics.BitOperations.PopCount(e.UsefulKindsMask), 13) / 13f : 0;
                if (e.ShantenAfter < best || (e.ShantenAfter == best && e.Ukeire > bestUkeire)) { best = e.ShantenAfter; bestUkeire = e.Ukeire; }
            }
            Array.Fill(x, Math.Clamp(best + 1, 0, 7) / 7f, 68 * Width, Width);
            Array.Fill(x, Math.Min(bestUkeire, 40) / 40f, 69 * Width, Width);
        }
        else if (closed == 13)
        {
            var hand = s.Hand.ToList();
            var shanten = Shanten.Calculate(hand, melds);
            var ukeire = shanten >= 0 ? Shanten.GetUsefulTiles(hand, melds).Sum(t => availability[TileHelpers.ToIndex(t)]) : 0;
            Array.Fill(x, Math.Clamp(shanten + 1, 0, 7) / 7f, 68 * Width, Width);
            Array.Fill(x, Math.Min(ukeire, 40) / 40f, 69 * Width, Width);
            if (s.CallTile is { } tile && (s.Legal & (LegalAction.Pon | LegalAction.Chi | LegalAction.MinKan)) != 0)
            {
                var bestAfterClaim = 9;
                foreach (var consumed in ClaimOptions(s, tile))
                {
                    var kept = (int[])counts.Clone();
                    foreach (var t in consumed) kept[TileHelpers.ToIndex(t)]--;
                    var after = Shanten.EvaluateDiscards(kept, melds + 1, availability);
                    if (after.Count > 0) bestAfterClaim = Math.Min(bestAfterClaim, after.Min(e => e.ShantenAfter));
                }
                if (bestAfterClaim < 9)
                {
                    Array.Fill(x, Math.Clamp(bestAfterClaim + 1, 0, 7) / 7f, 70 * Width, Width);
                    Array.Fill(x, bestAfterClaim == 0 ? 1f : 0f, 71 * Width, Width);
                }
            }
        }
    }

    public static float[] Compact(ReadOnlySpan<float> dense)
    {
        if (dense.Length < Count) throw new ArgumentException("A v2 feature vector is required.");
        var compact = new float[CompactCount];
        dense[..(32 * Width)].CopyTo(compact);
        for (var g = 0; g < 32; g++) compact[32 * Width + g] = dense[(32 + g) * Width];
        dense.Slice(64 * Width, 4 * Width).CopyTo(compact.AsSpan(32 * Width + 32));
        for (var g = 0; g < 4; g++) compact[32 * Width + 32 + 4 * Width + g] = dense[(68 + g) * Width];
        return compact;
    }

    public static float[] Expand(ReadOnlySpan<float> compact)
    {
        if (compact.Length < CompactCount) throw new ArgumentException("A compact v2 feature vector is required.");
        var dense = new float[Count];
        compact[..(32 * Width)].CopyTo(dense);
        for (var g = 0; g < 32; g++) Array.Fill(dense, compact[32 * Width + g], (32 + g) * Width, Width);
        compact.Slice(32 * Width + 32, 4 * Width).CopyTo(dense.AsSpan(64 * Width));
        for (var g = 0; g < 4; g++) Array.Fill(dense, compact[32 * Width + 32 + 4 * Width + g], (68 + g) * Width, Width);
        return dense;
    }

    // Tiles we would give up for each legal claim of `tile` (pon / open kan / chi shapes);
    // physical copies (red or plain) are irrelevant to the look-ahead.
    public static IEnumerable<Tile[]> ClaimOptions(StateSnapshot s, Tile tile)
    {
        var matches = s.Hand.Where(t => TileHelpers.SameKind(t, tile)).ToArray();
        if (s.Can(LegalAction.Pon) && matches.Length >= 2) yield return matches.Take(2).ToArray();
        if (s.Can(LegalAction.MinKan) && matches.Length >= 3) yield return matches.Take(3).ToArray();
        if (s.Can(LegalAction.Chi) && !tile.IsHonor)
            for (var start = Math.Max(1, tile.Number - 2); start <= Math.Min(7, tile.Number); start++)
            {
                var kinds = Enumerable.Range(start, 3).Where(n => n != tile.Number).ToArray();
                var first = s.Hand.FirstOrDefault(t => t.Suit == tile.Suit && t.Number == kinds[0]);
                var second = s.Hand.FirstOrDefault(t => t.Suit == tile.Suit && t.Number == kinds[1]);
                if (first != default && second != default) yield return [first, second];
            }
    }
}
