namespace MahjongHater.Core.Simulation;

public sealed record ScoredWin(ScoreResult Score, IReadOnlyList<YakuResult> Yaku);

public static class SimScoring
{
    public static ScoredWin? Win(SimGame game, int seat, Tile tile, bool tsumo, bool chankan = false, bool includeUra = true)
    {
        var p = game.Players[seat];
        var closed = p.Hand.ToList();
        if (!tsumo)
            closed.Add(tile);
        if (closed.Concat(p.Melds.SelectMany(m => m.Shape.Tiles)).GroupBy(TileHelpers.ToIndex).Any(g => g.Count() > 4))
            return null;
        if (closed.Count + 3 * p.Melds.Count != 14 || Shanten.Calculate(closed, p.Melds.Count) >= 0)
            return null;
        var hand = new Hand
        {
            WinningTile = tile, WinMethod = tsumo ? WinMethod.Tsumo : WinMethod.Ron,
            SeatWind = (Wind)(1 + (seat - game.Dealer + 4) % 4), RoundWind = (Wind)(1 + game.RoundIndex / 4),
            IsRiichi = p.Riichi, IsDoubleRiichi = p.DoubleRiichi, IsIppatsu = p.Ippatsu,
            IsRinshan = tsumo && game.Rinshan, IsChankan = chankan,
            IsHaitei = tsumo && !game.Rinshan && game.LiveWall.Count == 0,
            IsHoutei = !tsumo && !chankan && !game.LastDiscardWasRinshan && game.LiveWall.Count == 0,
            IsFirstDraw = tsumo && p.DrawCount == 1 && p.River.Count == 0 && !game.Interrupted,
        };
        hand.ClosedTiles.AddRange(closed);
        hand.CalledMelds.AddRange(p.Melds.Select(m => m.Shape));
        var physical = closed.Concat(p.Melds.SelectMany(m => m.Shape.Tiles)).ToArray();
        hand.AkadoraCount = physical.Count(t => t.IsRedFive);
        hand.DoraCount = physical.Sum(t => game.Dora.Count(d => TileHelpers.SameKind(Policy.TileDangerModel.DoraOf(d), t)));
        if (p.Riichi && includeUra)
        {
            var ura = Enumerable.Range(0, Math.Min(5, game.KanCount + 1)).Select(i => game.DeadWall[5 + i * 2]).ToArray();
            hand.UraDoraCount = physical.Sum(t => ura.Count(d => TileHelpers.SameKind(Policy.TileDangerModel.DoraOf(d), t)));
        }
        var detector = new YakuDetector(new RulesetOptions(game.Rules.Kuitan, game.Rules.HandsInMatch));
        ScoredWin? best = null;
        foreach (var d in HandDecomposer.GetWinningDecompositions(hand))
        {
            var yaku = detector.Detect(hand, d.Melds, d.Pair, d.Wait);
            if (yaku.Count == 0)
                continue;
            var fu = FuCalculator.Calculate(hand, d.Melds, d.Pair, d.Wait, game.Rules.DoubleWindPairFu);
            var score = new ScoringEngine().Calculate(hand, yaku, fu, seat == game.Dealer);
            if (best is null || (tsumo ? score.TotalTsumoPayment : score.RonPayment)
                > (tsumo ? best.Score.TotalTsumoPayment : best.Score.RonPayment))
                best = new ScoredWin(score, yaku);
        }
        return best;
    }

    public static bool Furiten(SimPlayer player)
    {
        if (player.TemporaryFuriten || player.RiichiFuriten)
            return true;
        var waits = SimTiles.Waits(player.Hand, player.Melds.Select(m => m.Shape));
        return player.River.Any(d => (waits & (1UL << TileHelpers.ToIndex(d.Tile))) != 0);
    }

    public static void SettleWins(SimGame game, int[] winners, int fromSeat, bool tsumo, bool chankan = false)
    {
        var tile = tsumo ? game.DrawnTile!.Value : game.PendingTile!.Value;
        foreach (var winner in winners)
        {
            var scored = Win(game, winner, tile, tsumo, chankan) ?? throw new InvalidOperationException("Winning hand has no yaku.");
            var p = game.Players[winner];
            var liabilities = scored.Yaku.Where(y => y.IsYakuman).Take(4).Select(y => y.Name switch
            {
                "Daisangen" => p.DragonLiability,
                "Daisuushii" => p.WindLiability,
                _ => -1,
            }).ToArray();
            if (liabilities.Any(s => s >= 0))
            {
                // Settle each yakuman component so pao does not absorb unrelated yakuman.
                foreach (var liable in liabilities)
                {
                    var points = new ScoringEngine().Calculate(new Hand { WinMethod = tsumo ? WinMethod.Tsumo : WinMethod.Ron },
                        [new YakuResult("component", 13, true, false)], 0, winner == game.Dealer);
                    if (liable < 0)
                        Pay(game, winner, fromSeat, tsumo, points);
                    else if (tsumo || liable == fromSeat)
                        Transfer(game, liable, winner, tsumo ? points.TotalTsumoPayment : points.RonPayment);
                    else
                    {
                        Transfer(game, liable, winner, points.RonPayment / 2);
                        Transfer(game, fromSeat, winner, points.RonPayment / 2);
                    }
                }
            }
            else
                Pay(game, winner, fromSeat, tsumo, scored.Score);

            if (!tsumo)
                Transfer(game, fromSeat, winner, 300 * game.Honba);
            else if (liabilities.Any(s => s >= 0))
                Transfer(game, liabilities.First(s => s >= 0), winner, 300 * game.Honba);
            else
                foreach (var seat in Enumerable.Range(0, 4).Where(s => s != winner))
                    Transfer(game, seat, winner, 100 * game.Honba);
        }
        var recipient = tsumo ? winners[0] : winners.OrderBy(s => (s - fromSeat + 4) % 4).First();
        game.Players[recipient].Score += 1000 * game.RiichiSticks;
        game.RiichiSticks = 0;
        Finish(game, tsumo ? HandEnd.Tsumo : HandEnd.Ron, winners, winners.Contains(game.Dealer), new bool[4]);
    }

    public static void Exhaustive(SimGame game)
    {
        var tenpai = game.Players.Select(p => SimTiles.Waits(p.Hand, p.Melds.Select(m => m.Shape)) != 0).ToArray();
        var nagashi = Enumerable.Range(0, 4).Where(s => game.Players[s].River.Count > 0
            && game.Players[s].River.All(d => !d.Claimed && d.Tile.IsTerminalOrHonor)).ToArray();
        if (nagashi.Length > 0)
        {
            foreach (var winner in nagashi)
            {
                var score = new ScoringEngine().Calculate(new Hand { WinMethod = WinMethod.Tsumo },
                    [new YakuResult("Nagashi Mangan", 5, false, false)], 30, winner == game.Dealer);
                Pay(game, winner, -1, true, score);
                foreach (var seat in Enumerable.Range(0, 4).Where(s => s != winner))
                    Transfer(game, seat, winner, 100 * game.Honba);
            }
        }
        else
        {
            var ready = tenpai.Count(t => t);
            if (ready is > 0 and < 4)
                for (var seat = 0; seat < 4; seat++)
                    game.Players[seat].Score += tenpai[seat] ? 3000 / ready : -3000 / (4 - ready);
        }
        Finish(game, nagashi.Length > 0 ? HandEnd.NagashiMangan : HandEnd.ExhaustiveDraw, nagashi, tenpai[game.Dealer], tenpai);
    }

    public static void Abort(SimGame game, HandEnd end) => Finish(game, end, [], true, new bool[4]);

    private static void Pay(SimGame game, int winner, int from, bool tsumo, ScoreResult score)
    {
        if (!tsumo)
            Transfer(game, from, winner, score.RonPayment);
        else
            for (var seat = 0; seat < 4; seat++)
                if (seat != winner)
                    Transfer(game, seat, winner, winner == game.Dealer || seat == game.Dealer ? score.TsumoPaymentDealer : score.TsumoPaymentNonDealer);
    }
    private static void Transfer(SimGame game, int from, int to, int amount)
    {
        game.Players[from].Score -= amount;
        game.Players[to].Score += amount;
    }
    private static void Finish(SimGame game, HandEnd end, int[] winners, bool repeats, bool[] tenpai)
    {
        game.Result = new SimHandResult(end, winners, game.Players.Select((p, i) => p.Score - game.StartingScores[i]).ToArray(), repeats, tenpai);
        game.Phase = SimPhase.Ended;
    }
}
