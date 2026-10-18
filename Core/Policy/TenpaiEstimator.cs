using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

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

    // V3: fold the seat's trailing tsumogiri run into any tenpai estimate (logistic or the
    // network's head, neither of which sees it) as a likelihood ratio on the odds. In Phoenix
    // replays a late two-call hand is 45 % tenpai after a tedashi and 81 % after three
    // tsumogiri in a row; a late dama 7 % → 23 % (resources/policy/open_danger.json).
    public static double WithTsumogiri(double p, SeatState seat, PolicyWeights w, OpenDangerTable? table = null)
    {
        if (seat.Riichi || w.DefenseModel != DefenseModel.V3 || p <= 0 || p >= 1)
            return p;
        var ratio = (table ?? OpenDangerTable.Default).TenpaiOddsRatio(seat.Melds.Count(m => m.IsOpen), seat.Discards.Count, seat.TsumogiriStreak);
        var odds = p / (1 - p) * ratio;
        return Math.Clamp(odds / (1 + odds), 0, w.TenpaiMaxWithTsumogiri);
    }
}
