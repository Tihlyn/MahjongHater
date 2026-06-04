namespace MahjongHater.Core;

public enum WaitType
{
    Ryanmen,
    Shanpon,
    Kanchan,
    Penchan,
    Tanki,
}

public static class FuCalculator
{
    public static int Calculate(Hand hand, List<Meld> decomposition, Tile pair, WaitType wait, int doubleWindPairFu)
    {
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(decomposition);

        var normalizedPair = TileHelpers.Normalize(pair);
        if (IsChiitoitsu(decomposition, normalizedPair))
        {
            return 25;
        }

        var melds = decomposition.Where(m => m.Type != MeldType.Pair).ToList();
        var pairFu = CalculatePairFu(hand, normalizedPair, doubleWindPairFu);
        var meldFu = melds.Sum(meld => meld.FuValue(meld.IsOpen));
        var waitFu = wait is WaitType.Kanchan or WaitType.Penchan or WaitType.Tanki ? 2 : 0;
        var isPinfuTsumo = hand.WinMethod == WinMethod.Tsumo
            && !hand.IsOpen
            && melds.Count == 4
            && melds.All(m => m.IsSequence)
            && pairFu == 0
            && wait == WaitType.Ryanmen;

        var fu = 20;
        if (hand.WinMethod == WinMethod.Ron && !hand.IsOpen)
        {
            fu += 10;
        }
        else if (hand.WinMethod == WinMethod.Tsumo && !isPinfuTsumo)
        {
            fu += 2;
        }

        fu += meldFu;
        fu += pairFu;
        fu += waitFu;

        if (hand.IsOpen && fu == 20)
        {
            fu = 30;
        }

        if (isPinfuTsumo)
        {
            return 20;
        }

        return RoundUpToTen(fu);
    }

    private static int CalculatePairFu(Hand hand, Tile pair, int doubleWindPairFu)
    {
        if (pair.Suit == TileSuit.Dragon)
        {
            return 2;
        }

        if (pair.Suit != TileSuit.Wind)
        {
            return 0;
        }

        var seatMatch = (int)hand.SeatWind == pair.Number;
        var roundMatch = (int)hand.RoundWind == pair.Number;

        return (seatMatch, roundMatch) switch
        {
            (true, true) => doubleWindPairFu,
            (true, false) or (false, true) => 2,
            _ => 0,
        };
    }

    private static bool IsChiitoitsu(List<Meld> decomposition, Tile pair)
    {
        if (decomposition.Count != 7)
        {
            return false;
        }

        return decomposition.All(m => m.Type == MeldType.Pair)
               && decomposition.Select(m => TileHelpers.ToIndex(m.Tiles[0])).Distinct().Count() == 7;
    }

    private static int RoundUpToTen(int fu)
    {
        return ((fu + 9) / 10) * 10;
    }
}
