using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

// Our hand in points and the chance of cashing it (docs/DEFENSE_PLAN.md §3.3). Tenpai hands
// are scored per wait with YakuDetector/FuCalculator/ScoringEngine (riichi assumed when the
// hand is closed and riichi is still available); hands further away get a han estimate
// (dora + aka + the yaku the hand is heading for) priced at 30 fu.
public static class HandValue
{
    private static readonly ScoringEngine Engine = new();

    // Expected ron value in points of the hand after `discard`, averaged over the live copies
    // of each wait; `min` is the cheapest wait. 0 when nothing is tenpai.
    public static (double Expected, int Min) TenpaiPoints(StateSnapshot state, Tile discard, IReadOnlyList<Tile> waits,
        int[] visible, PolicyWeights w, CancellationToken ct)
    {
        if (waits.Count == 0)
            return (0, 0);
        var closed = state.Hand.ToList();
        if (!closed.Remove(discard))
        {
            var idx = closed.FindIndex(t => TileHelpers.SameKind(t, discard));
            if (idx < 0)
                return (0, 0);
            closed.RemoveAt(idx);
        }

        var doras = state.DoraIndicators.Select(TileDangerModel.DoraOf).ToList();
        var detector = new YakuDetector(state.Ruleset);
        var isDealer = state.DealerSeat == 0;
        var riichi = state.OurRiichi || (!state.IsOpen && state.Can(LegalAction.Riichi));
        double weighted = 0, weight = 0;
        var min = int.MaxValue;
        foreach (var wait in waits)
        {
            ct.ThrowIfCancellationRequested();
            var live = Math.Max(0, 4 - visible[TileHelpers.ToIndex(wait)]);
            var points = PointsForWait(state, closed, wait, doras, detector, isDealer, riichi, w);
            if (points <= 0)
                continue;
            min = Math.Min(min, points);
            // A dead wait still counts a little so the hand is not valued at zero.
            var share = Math.Max(0.25, live);
            weighted += share * points;
            weight += share;
        }

        return weight > 0 ? (weighted / weight, min == int.MaxValue ? 0 : min) : (0, 0);
    }

    // Points for a hand that is not tenpai yet: dora/aka already held plus the yaku the shape
    // is heading for (riichi when closed, one yaku otherwise), priced at 30 fu.
    public static double EstimatePoints(StateSnapshot state, double keptDoraValue, PolicyWeights w)
    {
        var han = keptDoraValue + (state.IsOpen ? w.OpenHandAssumedHan : w.ClosedHandAssumedHan);
        return PointsFor(han, 30, state.DealerSeat == 0);
    }

    // Ron payment for a whole number of han at `fu`, interpolating fractional han.
    public static double PointsFor(double han, int fu, bool isDealer)
    {
        if (han <= 0)
            return 0;
        var lo = (int)Math.Floor(han);
        var frac = han - lo;
        var low = RonPayment(lo, fu, isDealer);
        if (frac < 1e-9)
            return low;
        return low + frac * (RonPayment(lo + 1, fu, isDealer) - low);
    }

    public static int RonPayment(int han, int fu, bool isDealer)
    {
        if (han <= 0)
            return 0;
        var hand = new Hand { WinMethod = WinMethod.Ron };
        var yaku = new List<YakuResult> { new("estimate", han, false, false) };
        return Engine.Calculate(hand, yaku, fu, isDealer).RonPayment;
    }

    // Chance of winning the hand from here (docs/DEFENSE_PLAN.md §3.3): tenpai hits scale
    // with live winning tiles per unseen tile over the turns left (ron included through k);
    // a 1-shanten first needs an accepting draw, then wins with its follow-up shape.
    public static double WinProbability(int shantenAfter, int ukeire, long ukeire2, int wallRemaining, PolicyWeights w)
    {
        var unseen = wallRemaining + 53;                 // wall + three hands + dead wall
        var turnsLeft = Math.Max(0, wallRemaining) / 4.0;
        if (shantenAfter < 0)
            return 1;
        if (shantenAfter == 0)
            return 1 - Math.Exp(-w.WinRateK * ukeire * turnsLeft / unseen);
        if (shantenAfter == 1)
        {
            var reach = 1 - Math.Exp(-ukeire * turnsLeft * w.TenpaiRateK / unseen);
            var nextUkeire = ukeire > 0 && ukeire2 > 0 ? Math.Clamp(ukeire2 / (double)ukeire, 2, 12) : 5;
            var thenWin = 1 - Math.Exp(-w.WinRateK * nextUkeire * (turnsLeft / 2) / unseen);
            return reach * thenWin;
        }

        var far = WinProbability(1, Math.Max(ukeire, 1), ukeire2, wallRemaining, w);
        return far * Math.Pow(w.ExtraShantenFactor, shantenAfter - 1);
    }

    private static int PointsForWait(StateSnapshot state, List<Tile> closed, Tile wait, List<Tile> doras,
        YakuDetector detector, bool isDealer, bool riichi, PolicyWeights w)
    {
        var winning = new Hand
        {
            WinningTile = wait,
            WinMethod = WinMethod.Ron,
            SeatWind = state.SeatWind,
            RoundWind = state.RoundWind,
            IsRiichi = riichi,
        };
        winning.ClosedTiles.AddRange(closed);
        winning.ClosedTiles.Add(wait);
        winning.CalledMelds.AddRange(state.OurMelds);
        var all = winning.ClosedTiles.Concat(winning.CalledMelds.SelectMany(m => m.Tiles)).ToList();
        winning.DoraCount = all.Count(t => doras.Any(d => TileHelpers.SameKind(d, t)));
        winning.AkadoraCount = all.Count(t => t.IsRedFive);

        var decomposition = HandDecomposer.GetBestDecomposition(winning);
        if (decomposition is null)
            return 0;
        var yaku = detector.Detect(winning, decomposition.Melds, decomposition.Pair, decomposition.Wait);
        if (yaku.Count == 0)
            return 0;   // no yaku on this wait: it cannot be won by ron
        var fu = FuCalculator.Calculate(winning, decomposition.Melds, decomposition.Pair, decomposition.Wait, 4);
        var score = Engine.Calculate(winning, yaku, fu, isDealer);
        var points = score.RonPayment;
        if (riichi && !state.OurRiichi)
        {
            // Ura dora / ippatsu / tsumo expectation on top of a fresh riichi.
            var bonus = PointsFor(score.Han + w.RiichiBonusHan, fu, isDealer) - PointsFor(score.Han, fu, isDealer);
            points += (int)Math.Round(bonus);
        }

        return points;
    }
}

// Opponents in points (docs/DEFENSE_PLAN.md §3.2): a riichi is worth the statistical average,
// an open hand what its melds show, a quiet closed hand a modest dama.
public static class ThreatValue
{
    public static double Points(SeatState seat, StateSnapshot state, PolicyWeights w)
    {
        var isDealer = seat.Seat == state.DealerSeat;
        var doras = state.DoraIndicators.Select(TileDangerModel.DoraOf).ToList();
        var meldTiles = seat.Melds.SelectMany(m => m.Tiles).ToList();
        var doraHan = meldTiles.Count(t => doras.Any(d => TileHelpers.SameKind(d, t))) + meldTiles.Count(t => t.IsRedFive);

        if (seat.Riichi)
            return (isDealer ? w.DealerRiichiValue : w.RiichiValue) + w.OpponentDoraValue * doraHan;

        var open = seat.Melds.Where(m => m.IsOpen).ToList();
        if (open.Count == 0)
            return isDealer ? w.DamaDealerValue : w.DamaValue;

        var seatWind = TileDangerModel.SeatWindOf(seat.Seat, state.DealerSeat);
        var han = 0.0;
        foreach (var meld in open)
        {
            var t = meld.Tiles[0];
            if (meld.Type == MeldType.Chi)
                continue;
            if (t.Suit == TileSuit.Dragon)
                han += 1;
            else if (t.Suit == TileSuit.Wind)
                han += ((Wind)t.Number == seatWind ? 1 : 0) + ((Wind)t.Number == state.RoundWind ? 1 : 0);
        }

        var suits = open.Select(m => m.Tiles[0].Suit).Where(s => s is TileSuit.Man or TileSuit.Pin or TileSuit.Sou).Distinct().Count();
        var honorsOnly = open.All(m => m.Tiles[0].IsHonor);
        if (open.Count >= 2 && suits <= 1 && !honorsOnly)
            han += 2;                                        // half flush read
        if (open.Count >= 3 && open.All(m => m.Type != MeldType.Chi))
            han += 2;                                        // toitoi read
        if (han == 0)
            han = open.All(m => m.Tiles.All(t => t.IsSimple)) ? 1 : w.OpenHandAssumedHan; // tanyao or a hidden yakuhai
        han += doraHan;
        var fu = open.Any(m => m.Type == MeldType.Chi) ? 30 : 40;
        var points = HandValue.PointsFor(han, fu, isDealer);
        return Math.Max(points, isDealer ? w.OpenFloorDealerValue : w.OpenFloorValue);
    }
}
