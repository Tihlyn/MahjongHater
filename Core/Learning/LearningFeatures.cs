using MahjongHater.Core.Policy;
using MahjongHater.Core.Simulation;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Learning;

// The exporter and live inference share this implementation. No replay targets,
// hidden hands, shuffle seeds, future outcomes or ura indicators are inputs.
public static class LearningFeatures
{
    public const string Version = "public-tiles-v1";
    public const int Channels = 64;
    public const int Width = 34;
    public const int Count = Channels * Width;
    public const int Actions = 74; // 37 physical kinds (red fives separate), discard then riichi.

    public static int ActionIndex(SimAction action) => action.Tile is not null
        && action.Kind is SimActionKind.Discard or SimActionKind.Riichi
        ? SimTiles.Physical(Tile.Parse(action.Tile)) + (action.Kind == SimActionKind.Riichi ? 37 : 0) : -1;

    public static float[] Encode(StateSnapshot s)
    {
        if (s.Seats.Count != 4 || s.Seats.Where((seat, i) => seat.Seat != i).Any())
            throw new ArgumentException("Learning inputs require four relative seats.");
        var x = new float[Count];
        void Add(int c, Tile t, float v) => x[c * Width + TileHelpers.ToIndex(t)] += v;
        foreach (var t in s.Hand) { Add(0, t, .25f); if (t.IsRedFive) Add(1, t, 1); }
        if (s.DrawnTile is { } draw) Add(2, draw, 1);
        foreach (var t in s.DoraIndicators) { Add(3, t, .2f); Add(4, TileDangerModel.DoraOf(t), .2f); }
        foreach (var t in s.Hand.Concat(s.SeenForAnalyzer()).Concat(s.OurMelds.SelectMany(m => m.Tiles)).Concat(s.DoraIndicators)) Add(5, t, .25f);
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
        return x;
    }
}
