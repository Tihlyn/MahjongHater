namespace MahjongHater.Core;

public sealed class YakuDetector
{
    private readonly Configuration configuration;

    public YakuDetector(Configuration configuration)
    {
        this.configuration = configuration;
    }

    public List<YakuResult> Detect(Hand hand, List<Meld> decomposition, Tile pair, WaitType wait)
    {
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(decomposition);

        var tiles = hand.AllTiles;
        var results = new List<YakuResult>();
        var yakuman = new List<YakuResult>();
        var normalizedPair = TileHelpers.Normalize(pair);
        var melds = decomposition.Where(m => m.Type != MeldType.Pair).ToList();
        var triplets = melds.Where(m => m.IsTriplet).ToList();
        var sequences = melds.Where(m => m.IsSequence).ToList();

        if (hand.IsFirstDraw && hand.WinMethod == WinMethod.Tsumo && !hand.IsOpen)
        {
            yakuman.Add(new YakuResult(hand.SeatWind == Wind.East ? "Tenhou" : "Chiihou", 13, true, false));
        }

        if (!hand.IsOpen && IsKokushiMusou(tiles))
        {
            yakuman.Add(new YakuResult("Kokushi Musou", 13, true, false));
        }

        if (CountConcealedTriplets(hand, triplets) == 4)
        {
            yakuman.Add(new YakuResult("Suuankou", 13, true, false));
        }

        var dragonTriplets = triplets.Where(m => m.Tiles[0].Suit == TileSuit.Dragon).ToList();
        if (dragonTriplets.Count == 3)
        {
            yakuman.Add(new YakuResult("Daisangen", 13, true, true));
        }

        var windTriplets = triplets.Where(m => m.Tiles[0].Suit == TileSuit.Wind).ToList();
        var windPair = normalizedPair.Suit == TileSuit.Wind;
        if (windTriplets.Count == 4)
        {
            yakuman.Add(new YakuResult("Daisuushii", 13, true, true));
        }
        else if (windTriplets.Count == 3 && windPair)
        {
            yakuman.Add(new YakuResult("Shousuushii", 13, true, false));
        }

        if (tiles.All(t => t.IsHonor))
        {
            yakuman.Add(new YakuResult("Tsuuiisou", 13, true, false));
        }

        if (tiles.All(t => t.IsTerminal) && tiles.All(t => !t.IsHonor))
        {
            yakuman.Add(new YakuResult("Chinroutou", 13, true, false));
        }

        if (IsRyuuiisou(tiles))
        {
            yakuman.Add(new YakuResult("Ryuuiisou", 13, true, false));
        }

        if (!hand.IsOpen && IsChuurenPoutou(tiles))
        {
            yakuman.Add(new YakuResult("Chuuren Poutou", 13, true, false));
        }

        if (melds.Count(m => m.IsKan) == 4)
        {
            yakuman.Add(new YakuResult("Suukantsu", 13, true, false));
        }

        if (yakuman.Count > 0)
        {
            return yakuman;
        }

        if (hand.IsDoubleRiichi && !hand.IsOpen)
        {
            results.Add(new YakuResult("Double Riichi", 2, false, false));
        }
        else if (hand.IsRiichi && !hand.IsOpen)
        {
            results.Add(new YakuResult("Riichi", 1, false, false));
        }

        if (hand.WinMethod == WinMethod.Tsumo && !hand.IsOpen)
        {
            results.Add(new YakuResult("Menzen Tsumo", 1, false, false));
        }

        if (hand.IsIppatsu && (hand.IsRiichi || hand.IsDoubleRiichi) && !hand.IsOpen)
        {
            results.Add(new YakuResult("Ippatsu", 1, false, false));
        }

        if (hand.IsRinshan)
        {
            results.Add(new YakuResult("Rinshan Kaihou", 1, false, false));
        }

        if (hand.IsChankan)
        {
            results.Add(new YakuResult("Chankan", 1, false, false));
        }

        if (hand.IsHaitei)
        {
            results.Add(new YakuResult("Haitei Raoyue", 1, false, false));
        }

        if (hand.IsHoutei)
        {
            results.Add(new YakuResult("Houtei Raoyui", 1, false, false));
        }

        var chiitoitsu = IsChiitoitsu(tiles);
        if (chiitoitsu)
        {
            results.Add(new YakuResult("Chiitoitsu", 2, false, false));
        }

        if (IsPinfu(hand, melds, normalizedPair, wait))
        {
            results.Add(new YakuResult("Pinfu", 1, false, false));
        }

        var identicalSequencePairs = CountIdenticalSequencePairs(sequences);
        if (!hand.IsOpen && identicalSequencePairs >= 2)
        {
            results.Add(new YakuResult("Ryanpeikou", 3, false, false));
        }
        else if (!hand.IsOpen && identicalSequencePairs == 1)
        {
            results.Add(new YakuResult("Iipeikou", 1, false, false));
        }

        if (tiles.All(t => t.IsSimple) && (!hand.IsOpen || this.configuration.Kuitan))
        {
            results.Add(new YakuResult("Tanyao", 1, false, false));
        }

        foreach (var triplet in dragonTriplets)
        {
            results.Add(new YakuResult($"Yakuhai ({TileHelpers.GetDisplayName(triplet.Tiles[0])})", 1, false, false));
        }

        foreach (var triplet in windTriplets)
        {
            var wind = (Wind)triplet.Tiles[0].Number;
            if (wind == hand.RoundWind)
            {
                results.Add(new YakuResult("Yakuhai (Round Wind)", 1, false, false));
            }

            if (wind == hand.SeatWind)
            {
                results.Add(new YakuResult("Yakuhai (Seat Wind)", 1, false, false));
            }
        }

        if (HasSanshokuDoujun(sequences))
        {
            results.Add(new YakuResult("Sanshoku Doujun", hand.IsOpen ? 1 : 2, false, false));
        }

        if (HasIttsuu(sequences))
        {
            results.Add(new YakuResult("Ittsuu", hand.IsOpen ? 1 : 2, false, false));
        }

        if (IsJunchan(melds, normalizedPair))
        {
            results.Add(new YakuResult("Junchan", hand.IsOpen ? 2 : 3, false, false));
        }
        else if (IsChanta(melds, normalizedPair))
        {
            results.Add(new YakuResult("Chanta", hand.IsOpen ? 1 : 2, false, false));
        }

        if (tiles.All(t => t.IsTerminalOrHonor))
        {
            results.Add(new YakuResult("Honroutou", 2, false, false));
        }

        if (HasShousangen(triplets, normalizedPair))
        {
            results.Add(new YakuResult("Shousangen", 2, false, false));
        }

        if (!chiitoitsu && triplets.Count == 4)
        {
            results.Add(new YakuResult("Toitoi", 2, false, false));
        }

        if (HasSanshokuDoukou(triplets))
        {
            results.Add(new YakuResult("Sanshoku Doukou", 2, false, false));
        }

        if (CountConcealedTriplets(hand, triplets) >= 3)
        {
            results.Add(new YakuResult("Sanankou", 2, false, false));
        }

        if (melds.Count(m => m.IsKan) >= 3)
        {
            results.Add(new YakuResult("Sankantsu", 2, false, false));
        }

        var suitProfile = GetSuitProfile(tiles);
        if (suitProfile.IsChinitsu)
        {
            results.Add(new YakuResult("Chinitsu", hand.IsOpen ? 5 : 6, false, false));
        }
        else if (suitProfile.IsHonitsu)
        {
            results.Add(new YakuResult("Honitsu", hand.IsOpen ? 2 : 3, false, false));
        }

        return results;
    }

    private static bool IsPinfu(Hand hand, List<Meld> melds, Tile pair, WaitType wait)
    {
        return !hand.IsOpen
               && melds.Count == 4
               && melds.All(m => m.IsSequence)
               && !IsYakuhaiPair(hand, pair)
               && wait == WaitType.Ryanmen;
    }

    private static bool IsYakuhaiPair(Hand hand, Tile pair)
    {
        if (pair.Suit == TileSuit.Dragon)
        {
            return true;
        }

        return pair.Suit == TileSuit.Wind && ((int)hand.SeatWind == pair.Number || (int)hand.RoundWind == pair.Number);
    }

    private static int CountIdenticalSequencePairs(IEnumerable<Meld> sequences)
    {
        return sequences
            .GroupBy(m => (m.Tiles[0].Suit, m.Tiles.Min(t => t.Number)))
            .Sum(group => group.Count() / 2);
    }

    private static bool HasSanshokuDoujun(IEnumerable<Meld> sequences)
    {
        return sequences
            .GroupBy(m => m.Tiles.Min(t => t.Number))
            .Any(group => group.Select(m => m.Tiles[0].Suit).Where(s => s <= TileSuit.Sou).Distinct().Count() == 3);
    }

    private static bool HasIttsuu(IEnumerable<Meld> sequences)
    {
        return sequences
            .Where(m => m.Tiles[0].Suit <= TileSuit.Sou)
            .GroupBy(m => m.Tiles[0].Suit)
            .Any(group => new HashSet<int>(group.Select(m => m.Tiles.Min(t => t.Number))).SetEquals([1, 4, 7]));
    }

    private static bool IsChanta(IEnumerable<Meld> melds, Tile pair)
    {
        var allParts = melds.All(ContainsTerminalOrHonor) && ContainsTerminalOrHonor(pair);
        var hasHonor = melds.SelectMany(m => m.Tiles).Any(t => t.IsHonor) || pair.IsHonor;
        return allParts && hasHonor;
    }

    private static bool IsJunchan(IEnumerable<Meld> melds, Tile pair)
    {
        return melds.All(ContainsTerminalOnly) && ContainsTerminalOnly(pair);
    }

    private static bool ContainsTerminalOrHonor(Meld meld)
    {
        return meld.Tiles.Any(t => t.IsTerminalOrHonor);
    }

    private static bool ContainsTerminalOrHonor(Tile tile)
    {
        return tile.IsTerminalOrHonor;
    }

    private static bool ContainsTerminalOnly(Meld meld)
    {
        return meld.Tiles.All(t => !t.IsHonor) && meld.Tiles.Any(t => t.IsTerminal);
    }

    private static bool ContainsTerminalOnly(Tile tile)
    {
        return tile.IsTerminal && !tile.IsHonor;
    }

    private static bool HasShousangen(IEnumerable<Meld> triplets, Tile pair)
    {
        return triplets.Count(m => m.Tiles[0].Suit == TileSuit.Dragon) == 2 && pair.Suit == TileSuit.Dragon;
    }

    private static bool HasSanshokuDoukou(IEnumerable<Meld> triplets)
    {
        return triplets
            .Where(m => m.Tiles[0].Suit <= TileSuit.Sou)
            .GroupBy(m => m.Tiles[0].Number)
            .Any(group => group.Select(m => m.Tiles[0].Suit).Distinct().Count() == 3);
    }

    private static int CountConcealedTriplets(Hand hand, IEnumerable<Meld> triplets)
    {
        var winningTile = hand.WinningTile.HasValue ? TileHelpers.Normalize(hand.WinningTile.Value) : (Tile?)null;
        var concealed = 0;

        foreach (var triplet in triplets)
        {
            if (triplet.IsOpen)
            {
                continue;
            }

            if (hand.WinMethod == WinMethod.Ron && winningTile.HasValue && triplet.Tiles.Any(t => TileHelpers.SameKind(t, winningTile.Value)))
            {
                continue;
            }

            concealed++;
        }

        return concealed;
    }

    private static bool IsChiitoitsu(IReadOnlyList<Tile> tiles)
    {
        if (tiles.Count != 14)
        {
            return false;
        }

        var counts = tiles.GroupBy(TileHelpers.ToIndex).Select(group => group.Count()).OrderBy(count => count).ToArray();
        return counts.Length == 7 && counts.All(count => count == 2);
    }

    private static bool IsKokushiMusou(IEnumerable<Tile> tiles)
    {
        var tileList = tiles.Select(TileHelpers.Normalize).ToList();
        var required = TileHelpers.AllTileTypes.Where(t => t.IsTerminalOrHonor).Select(TileHelpers.ToIndex).ToHashSet();
        var indices = tileList.Select(TileHelpers.ToIndex).ToList();
        return tileList.Count == 14
               && required.All(indices.Contains)
               && indices.Count(index => required.Contains(index)) == 14
               && required.Any(index => indices.Count(i => i == index) == 2);
    }

    private static bool IsRyuuiisou(IEnumerable<Tile> tiles)
    {
        return tiles.All(tile => tile switch
        {
            { Suit: TileSuit.Sou, Number: 2 or 3 or 4 or 6 or 8 } => true,
            { Suit: TileSuit.Dragon, Number: 2 } => true,
            _ => false,
        });
    }

    private static bool IsChuurenPoutou(IEnumerable<Tile> tiles)
    {
        var tileList = tiles.Select(TileHelpers.Normalize).ToList();
        if (tileList.Count != 14 || tileList.Any(t => t.IsHonor))
        {
            return false;
        }

        var suits = tileList.Select(t => t.Suit).Distinct().ToList();
        if (suits.Count != 1)
        {
            return false;
        }

        var counts = new int[9];
        foreach (var tile in tileList)
        {
            counts[tile.Number - 1]++;
        }

        var template = new[] { 3, 1, 1, 1, 1, 1, 1, 1, 3 };
        var surplusFound = false;
        for (var i = 0; i < 9; i++)
        {
            if (counts[i] < template[i])
            {
                return false;
            }

            if (counts[i] > template[i])
            {
                if (surplusFound || counts[i] != template[i] + 1)
                {
                    return false;
                }

                surplusFound = true;
            }
        }

        return surplusFound;
    }

    private static SuitProfile GetSuitProfile(IEnumerable<Tile> tiles)
    {
        var normalized = tiles.Select(TileHelpers.Normalize).ToList();
        var numberSuits = normalized.Where(t => t.Suit <= TileSuit.Sou).Select(t => t.Suit).Distinct().ToList();
        var hasHonors = normalized.Any(t => t.IsHonor);
        return new SuitProfile(
            IsHonitsu: numberSuits.Count == 1 && hasHonors,
            IsChinitsu: numberSuits.Count == 1 && !hasHonors);
    }

    private readonly record struct SuitProfile(bool IsHonitsu, bool IsChinitsu);
}

public sealed record YakuResult(string Name, int Han, bool IsYakuman, bool PaoApplies);

internal sealed record HandDecomposition(List<Meld> Melds, Tile Pair, WaitType Wait);

internal static class HandDecomposer
{
    public static HandDecomposition? GetBestDecomposition(Hand hand)
    {
        return GetWinningDecompositions(hand)
            .OrderByDescending(d => d.Melds.Count(m => m.IsTriplet))
            .ThenBy(d => d.Wait)
            .FirstOrDefault();
    }

    public static IEnumerable<HandDecomposition> GetWinningDecompositions(Hand hand)
    {
        ArgumentNullException.ThrowIfNull(hand);

        var tiles = hand.ClosedTiles.Select(TileHelpers.Normalize).OrderBy(t => t).ToList();
        var totalTiles = tiles.Count + hand.CalledMelds.Sum(m => m.Tiles.Length);
        if (totalTiles != 14)
        {
            yield break;
        }

        if (!hand.IsOpen && IsChiitoitsu(tiles))
        {
            var pairMelds = tiles
                .GroupBy(TileHelpers.ToIndex)
                .Select(group => Meld.MakePair(group.First()))
                .ToList();
            yield return new HandDecomposition(pairMelds, pairMelds[0].Tiles[0], WaitType.Tanki);
        }

        if (!hand.IsOpen && IsKokushi(tiles))
        {
            var pair = tiles.GroupBy(TileHelpers.ToIndex).First(group => group.Count() == 2).First();
            yield return new HandDecomposition([], pair, WaitType.Tanki);
        }

        var counts = ToCounts(tiles);
        var neededClosedMelds = 4 - hand.CalledMelds.Count;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] < 2)
            {
                continue;
            }

            counts[i] -= 2;
            var pair = TileHelpers.FromIndex(i);
            foreach (var closedMelds in SearchMelds(counts, neededClosedMelds, []))
            {
                var allMelds = closedMelds.Concat(hand.CalledMelds).ToList();
                yield return new HandDecomposition(allMelds, pair, DetermineWaitType(hand, closedMelds, pair));
            }

            counts[i] += 2;
        }
    }

    private static IEnumerable<List<Meld>> SearchMelds(int[] counts, int target, List<Meld> current)
    {
        if (current.Count == target)
        {
            if (counts.All(count => count == 0))
            {
                yield return current.Select(CloneMeld).ToList();
            }

            yield break;
        }

        var index = Array.FindIndex(counts, count => count > 0);
        if (index < 0)
        {
            yield break;
        }

        if (counts[index] >= 3)
        {
            counts[index] -= 3;
            current.Add(Meld.MakePon(TileHelpers.FromIndex(index), false));
            foreach (var result in SearchMelds(counts, target, current))
            {
                yield return result;
            }

            current.RemoveAt(current.Count - 1);
            counts[index] += 3;
        }

        if (index < 27 && index % 9 <= 6 && counts[index + 1] > 0 && counts[index + 2] > 0)
        {
            counts[index]--;
            counts[index + 1]--;
            counts[index + 2]--;
            current.Add(Meld.MakeChi(TileHelpers.FromIndex(index), TileHelpers.FromIndex(index + 1), TileHelpers.FromIndex(index + 2)));
            foreach (var result in SearchMelds(counts, target, current))
            {
                yield return result;
            }

            current.RemoveAt(current.Count - 1);
            counts[index]++;
            counts[index + 1]++;
            counts[index + 2]++;
        }
    }

    private static WaitType DetermineWaitType(Hand hand, IReadOnlyList<Meld> melds, Tile pair)
    {
        if (!hand.WinningTile.HasValue)
        {
            return WaitType.Ryanmen;
        }

        var winningTile = TileHelpers.Normalize(hand.WinningTile.Value);
        if (TileHelpers.SameKind(pair, winningTile))
        {
            return WaitType.Tanki;
        }

        foreach (var meld in melds)
        {
            if (!meld.Tiles.Any(t => TileHelpers.SameKind(t, winningTile)))
            {
                continue;
            }

            if (meld.IsTriplet)
            {
                return WaitType.Shanpon;
            }

            if (meld.IsSequence)
            {
                var numbers = meld.Tiles.Select(t => t.Number).OrderBy(n => n).ToArray();
                if (numbers[1] == winningTile.Number)
                {
                    return WaitType.Kanchan;
                }

                if (numbers[0] == 1 && winningTile.Number == 3)
                {
                    return WaitType.Penchan;
                }

                if (numbers[2] == 9 && winningTile.Number == 7)
                {
                    return WaitType.Penchan;
                }

                return WaitType.Ryanmen;
            }
        }

        return WaitType.Ryanmen;
    }

    private static Meld CloneMeld(Meld meld)
    {
        return new Meld(meld.Type, meld.Tiles.ToArray(), meld.IsOpen);
    }

    private static int[] ToCounts(IEnumerable<Tile> tiles)
    {
        var counts = new int[34];
        foreach (var tile in tiles)
        {
            counts[TileHelpers.ToIndex(tile)]++;
        }

        return counts;
    }

    private static bool IsChiitoitsu(List<Tile> tiles)
    {
        return tiles.Count == 14 && tiles.GroupBy(TileHelpers.ToIndex).Count() == 7 && tiles.GroupBy(TileHelpers.ToIndex).All(group => group.Count() == 2);
    }

    private static bool IsKokushi(List<Tile> tiles)
    {
        return tiles.Count == 14
               && tiles.All(t => t.IsTerminalOrHonor)
               && tiles.GroupBy(TileHelpers.ToIndex).Count() == 13
               && tiles.GroupBy(TileHelpers.ToIndex).Any(group => group.Count() == 2);
    }
}
