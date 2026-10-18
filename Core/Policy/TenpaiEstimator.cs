using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

public enum RiverCut { None, Dora, Yakuhai, Middle }

// What the estimate sees about one opponent. Discards stand in for the turn number
// (one per turn, calls included), so "turn" below means Discards.
public readonly record struct TenpaiFeatures(
    int Discards,
    int OpenMelds,
    bool Riichi,
    double EarlyOutside,   // share of the first six discards that are terminals/honors (0..1)
    double LateMiddle)     // middle tiles (3–7) discarded from turn 7 on, /12, capped at 1
{
    public static TenpaiFeatures From(SeatState seat)
    {
        var early = seat.Discards.Take(6).Count(t => t.IsTerminalOrHonor) / 6d;
        // Fixed denominators keep both features monotone as discards arrive.
        var late = Math.Min(1, seat.Discards.Skip(6).Count(t => !t.IsHonor && t.Number is >= 3 and <= 7) / 12d);
        return new TenpaiFeatures(seat.Discards.Count, seat.Melds.Count(m => m.IsOpen), seat.Riichi, early, late);
    }
}

// Logistic tenpai estimate. The default weights reproduce the tenpai-rate-by-turn curves
// quoted in riichi strategy literature for non-riichi hands (roughly: no calls 5 % at
// turn 6, 17 % at 10, 35 % at 14, 60 % at 18; one call ~35 % at turn 10; two calls ~70 %
// at 12; three calls ~70 % at 8), then capped: nobody is certainly tenpai without riichi.
// The previous additive formula overshot to ~90 % late in every hand. Ground truth for
// re-fitting is logged per hand end by TenpaiCalibration (tools/fit_tenpai.py).
public static class TenpaiEstimator
{
    public static double Estimate(in TenpaiFeatures f, PolicyWeights w)
    {
        if (f.Riichi)
            return 1;

        var logit = w.TenpaiLogitIntercept
                    + (w.TenpaiLogitPerDiscard * f.Discards)
                    + (w.TenpaiLogitPerMeld * f.OpenMelds)
                    + (w.TenpaiLogitEarlyOutside * f.EarlyOutside)
                    + (w.TenpaiLogitLateMiddle * f.LateMiddle);
        var p = 1 / (1 + Math.Exp(-logit));
        return Math.Clamp(p, 0, w.TenpaiMaxWithoutRiichi);
    }

    public static double Estimate(SeatState seat, PolicyWeights w) => Estimate(TenpaiFeatures.From(seat), w);

    // V3: fold what the river says into any tenpai estimate (logistic or the network's head,
    // neither of which sees it) as a likelihood ratio on the odds, from Phoenix replays
    // (resources/policy/open_danger.json):
    //  * a hand-discarded live dora, live yakuhai or late middle tile is the classic "just got
    //    tenpai" cut;
    //  * otherwise the trailing tsumogiri run (late two calls 45 % after a tedashi, 81 % after
    //    three tsumogiri; a late dama 7 % -> 23 %).
    // A cut implies a tedashi, so the two never stack.
    public static double WithRiverTells(double p, SeatState seat, StateSnapshot? state, PolicyWeights w, OpenDangerTable? table = null)
    {
        if (seat.Riichi || w.DefenseModel != DefenseModel.V3 || p <= 0 || p >= 1)
            return p;
        table ??= OpenDangerTable.Default;
        var open = seat.Melds.Count(m => m.IsOpen);
        var cut = state is null ? RiverCut.None : LastCut(seat, state);
        var ratio = cut != RiverCut.None
            ? table.TenpaiCutRatio(open, seat.Discards.Count, cut.ToString().ToLowerInvariant())
            : table.TenpaiOddsRatio(open, seat.Discards.Count, seat.TsumogiriStreak);
        var odds = p / (1 - p) * ratio;
        return Math.Clamp(odds / (1 + odds), 0, w.TenpaiMaxWithTsumogiri);
    }

    public static double WithTsumogiri(double p, SeatState seat, PolicyWeights w, OpenDangerTable? table = null) =>
        WithRiverTells(p, seat, null, w, table);

    // The seat's latest discard as the replay counter classifies it (tools/open_hand_danger.py).
    public static RiverCut LastCut(SeatState seat, StateSnapshot state)
    {
        if (seat.Discards.Count == 0 || seat.DiscardTsumogiri.Count != seat.Discards.Count || seat.DiscardTsumogiri[^1])
            return RiverCut.None;
        var tile = seat.Discards[^1];
        var kind = TileHelpers.ToIndex(tile);
        if (tile.IsRedFive || state.DoraIndicators.Any(i => TileHelpers.ToIndex(TileDangerModel.DoraOf(i)) == kind))
            return RiverCut.Dora;
        var seatWind = TileDangerModel.SeatWindOf(seat.Seat, state.DealerSeat);
        var yakuhai = tile.Suit == TileSuit.Dragon || tile.Suit == TileSuit.Wind && ((Wind)tile.Number == seatWind || (Wind)tile.Number == state.RoundWind);
        if (yakuhai)
        {
            // Public copies other than this one: discards, every meld, dora indicators.
            var copies = state.SeenForAnalyzer().Concat(state.OurMelds.SelectMany(m => m.Tiles)).Concat(state.DoraIndicators)
                .Count(t => TileHelpers.ToIndex(t) == kind) - 1;
            if (copies <= 1)
                return RiverCut.Yakuhai;
        }

        return seat.Discards.Count >= 7 && !tile.IsHonor && tile.Number is >= 3 and <= 7 ? RiverCut.Middle : RiverCut.None;
    }
}
