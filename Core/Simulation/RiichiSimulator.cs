namespace MahjongHater.Core.Simulation;

// All transitions pass through legal action validation. The engine owns full hidden
// state; decision policies receive only SimObservation, never SimGame.
public static class RiichiSimulator
{
    public static SimGame Deal(SimulationRules rules, Random random, int dealer = 0, int roundIndex = 0,
        int[]? scores = null, int honba = 0, int sticks = 0, int initialDealer = 0)
    {
        rules.Validate();
        var tiles = SimTiles.Set();
        SimTiles.Shuffle(tiles, random);
        var game = new SimGame { Rules = rules, Dealer = dealer, InitialDealer = initialDealer, RoundIndex = roundIndex,
            Honba = honba, RiichiSticks = sticks, LiveWall = tiles.Skip(52).Take(70).ToList(), DeadWall = tiles.Skip(122).ToArray(),
            StartingScores = scores?.ToArray() ?? Enumerable.Repeat(rules.StartingScore, 4).ToArray() };
        for (var seat = 0; seat < 4; seat++)
        {
            game.Players[seat].Hand = tiles.Skip(seat * 13).Take(13).Order().ToList();
            game.Players[seat].Score = game.StartingScores[seat];
        }
        Draw(game, dealer);
        return game;
    }

    public static IReadOnlyList<SimAction> Legal(SimGame game)
    {
        if (game.Phase == SimPhase.Ended)
            return [];
        var seat = game.Actor;
        var p = game.Players[seat];
        var result = new List<SimAction>();
        if (game.Phase != SimPhase.Turn)
        {
            var tile = game.PendingTile!.Value;
            result.Add(new SimAction(SimActionKind.Pass));
            if (!SimScoring.Furiten(p) && SimScoring.Win(game, seat, tile, false, game.Phase == SimPhase.KanResponses) is not null)
                result.Add(SimAction.Make(SimActionKind.Ron, tile));
            if (game.Phase == SimPhase.KanResponses || p.Riichi || game.LiveWall.Count == 0)
                return result;
            var matches = p.Hand.Where(t => TileHelpers.SameKind(t, tile)).ToArray();
            if (matches.Length >= 2)
                foreach (var consumed in Combinations(matches, 2))
                    AddCall(result, game, SimActionKind.Pon, tile, consumed);
            if (matches.Length >= 3 && CanKan(game))
                foreach (var consumed in Combinations(matches, 3))
                    result.Add(SimAction.Make(SimActionKind.OpenKan, tile, consumed));
            if ((seat - game.PendingSeat + 4) % 4 == 1 && !tile.IsHonor)
                for (var start = Math.Max(1, tile.Number - 2); start <= Math.Min(7, tile.Number); start++)
                {
                    var kinds = Enumerable.Range(start, 3).Where(n => n != tile.Number).ToArray();
                    foreach (var first in p.Hand.Where(t => t.Suit == tile.Suit && t.Number == kinds[0]).Distinct())
                        foreach (var second in p.Hand.Where(t => t.Suit == tile.Suit && t.Number == kinds[1]).Distinct())
                            AddCall(result, game, SimActionKind.Chi, tile, [first, second]);
                }
            return result.Distinct().OrderBy(a => a.Key, StringComparer.Ordinal).ToArray();
        }

        if (game.DrawnTile is { } drawn && SimScoring.Win(game, seat, drawn, true) is not null)
            result.Add(SimAction.Make(SimActionKind.Tsumo, drawn));
        foreach (var tile in p.Hand.Distinct().Order())
        {
            if (game.ForbiddenDiscards.Contains(TileHelpers.ToIndex(tile)) || p.Riichi && tile != game.DrawnTile)
                continue;
            result.Add(SimAction.Make(SimActionKind.Discard, tile));
            if (!p.Riichi && p.Melds.All(m => !m.Shape.IsOpen) && p.Score >= 1000 && game.LiveWall.Count >= 4)
            {
                var kept = p.Hand.ToList();
                kept.Remove(tile);
                if (SimTiles.Waits(kept, p.Melds.Select(m => m.Shape)) != 0)
                    result.Add(SimAction.Make(SimActionKind.Riichi, tile));
            }
        }
        // Self kans only follow a draw; a pon/chi must first be followed by a discard.
        if (game.DrawnTile is not null && CanKan(game))
        {
            foreach (var group in p.Hand.GroupBy(TileHelpers.ToIndex).Where(g => g.Count() == 4))
            {
                var tiles = group.ToArray();
                if (p.Riichi)
                {
                    if (!TileHelpers.SameKind(tiles[0], game.DrawnTile.Value))
                        continue;
                    var before = p.Hand.ToList();
                    before.Remove(game.DrawnTile.Value);
                    var after = p.Hand.Where(t => !TileHelpers.SameKind(t, tiles[0])).ToList();
                    if (SimTiles.Waits(before, p.Melds.Select(m => m.Shape)) != SimTiles.Waits(after,
                            p.Melds.Select(m => m.Shape).Append(SimTiles.Meld(MeldType.Ankan, tiles, false))))
                        continue;
                }
                result.Add(SimAction.Make(SimActionKind.ClosedKan, tiles.Order().First(), tiles));
            }
            if (!p.Riichi)
                foreach (var meld in p.Melds.Where(m => m.Shape.Type == MeldType.Pon && m.Shape.IsOpen))
                    foreach (var tile in p.Hand.Where(t => TileHelpers.SameKind(t, meld.Shape.Tiles[0])).Distinct())
                        result.Add(SimAction.Make(SimActionKind.AddedKan, tile, [tile]));
        }
        if (p.DrawCount == 1 && p.River.Count == 0 && !game.Interrupted
            && p.Hand.Where(t => t.IsTerminalOrHonor).Select(TileHelpers.ToIndex).Distinct().Count() >= 9)
            result.Add(new SimAction(SimActionKind.NineTerminals));
        return result.Distinct().OrderBy(a => a.Key, StringComparer.Ordinal).ToArray();
    }

    public static void Apply(SimGame game, SimAction action, bool validate = true)
    {
        if (game.Phase == SimPhase.Ended || validate && !Legal(game).Contains(action))
            throw new ArgumentException($"Illegal simulation action: {action.Key}");
        if (++game.DecisionCount > 1024)
            throw new InvalidOperationException("Hand exceeded 1024 decisions; refusing to label a truncated hand as a result.");
        var seat = game.Actor;
        var p = game.Players[seat];
        if (game.Phase != SimPhase.Turn)
        {
            // Shape-completing passes cause furiten even if this tile supplies no yaku.
            var waits = SimTiles.Waits(p.Hand, p.Melds.Select(m => m.Shape));
            if (action.Kind != SimActionKind.Ron && (waits & (1UL << TileHelpers.ToIndex(game.PendingTile!.Value))) != 0)
            {
                p.TemporaryFuriten = true;
                if (p.Riichi)
                    p.RiichiFuriten = true;
            }
            game.Responses[seat] = action;
            if (++game.ResponseIndex == game.ResponseOrder.Length)
                ResolveResponses(game);
            return;
        }
        switch (action.Kind)
        {
            case SimActionKind.Tsumo:
                SimScoring.SettleWins(game, [seat], -1, true);
                break;
            case SimActionKind.NineTerminals:
                SimScoring.Abort(game, HandEnd.NineTerminals);
                break;
            case SimActionKind.Discard:
            case SimActionKind.Riichi:
            {
                var tile = Tile.Parse(action.Tile!);
                var declaration = action.Kind == SimActionKind.Riichi;
                if (p.RiichiPaid)
                    p.Ippatsu = false;
                if (declaration)
                {
                    p.Riichi = true;
                    p.DoubleRiichi = p.River.Count == 0 && !game.Interrupted;
                    p.Ippatsu = true;
                }
                p.Hand.Remove(tile);
                p.River.Add(new SimDiscard(tile, game.DiscardCounter++, declaration, false, tile == game.DrawnTile));
                game.ForbiddenDiscards.Clear();
                game.LastDiscardWasRinshan = game.Rinshan;
                game.PendingRiichi = declaration;
                OpenResponses(game, seat, tile, SimPhase.DiscardResponses);
                break;
            }
            case SimActionKind.ClosedKan:
            {
                if (game.KanCount == 4)
                {
                    SimScoring.Abort(game, HandEnd.FourKans);
                    break;
                }
                var consumed = action.ConsumedTiles();
                foreach (var tile in consumed)
                    p.Hand.Remove(tile);
                p.Melds.Add(new SimMeld(SimTiles.Meld(MeldType.Ankan, consumed, false), -1));
                CompleteKan(game, seat);
                break;
            }
            case SimActionKind.AddedKan:
            {
                if (game.KanCount == 4)
                {
                    SimScoring.Abort(game, HandEnd.FourKans);
                    break;
                }
                var tile = Tile.Parse(action.Tile!);
                p.Hand.Remove(tile);
                game.PendingMeldIndex = p.Melds.FindIndex(m => m.Shape.Type == MeldType.Pon && TileHelpers.SameKind(m.Shape.Tiles[0], tile));
                OpenResponses(game, seat, tile, SimPhase.KanResponses);
                break;
            }
            default: throw new InvalidOperationException("Unexpected turn action.");
        }
    }

    private static void ResolveResponses(SimGame game)
    {
        var wins = Enumerable.Range(0, 4).Where(s => game.Responses[s]?.Kind == SimActionKind.Ron).ToArray();
        if (wins.Length == 3)
        {
            SimScoring.Abort(game, HandEnd.TripleRon);
            return;
        }
        if (wins.Length > 0)
        {
            SimScoring.SettleWins(game, wins, game.PendingSeat, false, game.Phase == SimPhase.KanResponses);
            return;
        }
        var from = game.PendingSeat;
        if (game.Phase == SimPhase.KanResponses)
        {
            var p = game.Players[from];
            var pon = p.Melds[game.PendingMeldIndex];
            p.Melds[game.PendingMeldIndex] = new SimMeld(SimTiles.Meld(MeldType.Shouminkan,
                pon.Shape.Tiles.Append(game.PendingTile!.Value), true), pon.FromSeat);
            CompleteKan(game, from);
            return;
        }
        if (game.PendingRiichi)
        {
            game.Players[from].Score -= 1000;
            game.Players[from].RiichiPaid = true;
            game.RiichiSticks++;
            game.PendingRiichi = false;
        }
        if (game.AbortAfterDiscard)
        {
            SimScoring.Abort(game, HandEnd.FourKans);
            return;
        }
        var call = Enumerable.Range(0, 4).Where(s => game.Responses[s]?.Kind is SimActionKind.Pon or SimActionKind.Chi or SimActionKind.OpenKan)
            .OrderBy(s => game.Responses[s]!.Kind == SimActionKind.Chi ? 1 : 0).ThenBy(s => (s - from + 4) % 4).DefaultIfEmpty(-1).First();
        if (call >= 0)
        {
            var action = game.Responses[call]!;
            if (action.Kind == SimActionKind.OpenKan && game.KanCount == 4)
            {
                SimScoring.Abort(game, HandEnd.FourKans);
                return;
            }
            var p = game.Players[call];
            var consumed = action.ConsumedTiles();
            var claimed = game.PendingTile!.Value;
            var river = game.Players[from].River;
            river[^1] = river[^1] with { Claimed = true };
            foreach (var tile in consumed)
                p.Hand.Remove(tile);
            var type = action.Kind == SimActionKind.Pon ? MeldType.Pon : action.Kind == SimActionKind.Chi ? MeldType.Chi : MeldType.Daiminkan;
            p.Melds.Add(new SimMeld(SimTiles.Meld(type, consumed.Append(claimed), true), from));
            if (type != MeldType.Chi)
            {
                if (claimed.Suit == TileSuit.Dragon && p.Melds.Count(m => m.Shape.IsOpen && m.Shape.IsTriplet && m.Shape.Tiles[0].Suit == TileSuit.Dragon) == 3)
                    p.DragonLiability = from;
                if (claimed.Suit == TileSuit.Wind && p.Melds.Count(m => m.Shape.IsOpen && m.Shape.IsTriplet && m.Shape.Tiles[0].Suit == TileSuit.Wind) == 4)
                    p.WindLiability = from;
            }
            Interrupt(game);
            game.TurnSeat = call;
            game.DrawnTile = null;
            game.Rinshan = false;
            game.Phase = SimPhase.Turn;
            game.ForbiddenDiscards = Forbidden(action);
            if (type == MeldType.Daiminkan)
                CompleteKan(game, call);
            return;
        }
        if (!game.Interrupted && game.Players.All(p => p.River.Count == 1)
            && game.Players.All(p => p.River[0].Tile.Suit == TileSuit.Wind)
            && game.Players.Select(p => TileHelpers.ToIndex(p.River[0].Tile)).Distinct().Count() == 1)
        {
            SimScoring.Abort(game, HandEnd.FourWinds);
            return;
        }
        Draw(game, (from + 1) % 4);
    }

    private static void OpenResponses(SimGame game, int seat, Tile tile, SimPhase phase)
    {
        game.Phase = phase;
        if (phase != SimPhase.KanResponses)
            game.PendingMeldIndex = -1;
        game.PendingSeat = seat;
        game.PendingTile = tile;
        game.DrawnTile = null;
        game.ResponseOrder = Enumerable.Range(1, 3).Select(i => (seat + i) % 4).ToArray();
        game.ResponseIndex = 0;
        game.Responses = new SimAction?[4];
    }
    private static void Draw(SimGame game, int seat)
    {
        if (game.LiveWall.Count == 0)
        {
            SimScoring.Exhaustive(game);
            return;
        }
        var tile = game.LiveWall[0];
        game.LiveWall.RemoveAt(0);
        game.TurnSeat = seat;
        game.DrawnTile = tile;
        game.Rinshan = false;
        game.Phase = SimPhase.Turn;
        game.ForbiddenDiscards.Clear();
        game.Players[seat].Hand.Add(tile);
        game.Players[seat].Hand.Sort();
        game.Players[seat].DrawCount++;
        game.Players[seat].TemporaryFuriten = false;
    }
    private static void CompleteKan(SimGame game, int seat)
    {
        game.PendingMeldIndex = -1;
        Interrupt(game);
        var tile = game.DeadWall[game.KanCount];
        game.DeadWall[game.KanCount] = game.LiveWall[^1];
        game.LiveWall.RemoveAt(game.LiveWall.Count - 1);
        game.KanCount++;
        game.AbortAfterDiscard = game.KanCount == 4 && game.Players.Count(p => p.Melds.Any(m => m.Shape.IsKan)) > 1;
        game.TurnSeat = seat;
        game.DrawnTile = tile;
        game.Rinshan = true;
        game.Phase = SimPhase.Turn;
        game.Players[seat].Hand.Add(tile);
        game.Players[seat].Hand.Sort();
        game.Players[seat].DrawCount++;
        game.Players[seat].TemporaryFuriten = false;
        // Kuikae from an open kan lasts through its replacement draw.
        if (game.ForbiddenDiscards.Count == 0)
            game.ForbiddenDiscards = [];
    }
    private static void Interrupt(SimGame game)
    {
        game.Interrupted = true;
        foreach (var p in game.Players)
            p.Ippatsu = false;
    }
    private static bool CanKan(SimGame game) => game.LiveWall.Count > 0 && (game.KanCount < 4
        || game.KanCount == 4 && game.Players.Any(p => p.Melds.Count(m => m.Shape.IsKan) == 4));
    public static HashSet<int> Forbidden(SimAction action)
    {
        var tile = Tile.Parse(action.Tile!);
        var blocked = new HashSet<int> { TileHelpers.ToIndex(tile) };
        if (action.Kind == SimActionKind.Chi)
        {
            var consumed = action.ConsumedTiles().Order().ToArray();
            if (consumed[1].Number == consumed[0].Number + 1)
            {
                var other = tile.Number < consumed[0].Number ? consumed[1].Number + 1 : consumed[0].Number - 1;
                if (other is >= 1 and <= 9)
                    blocked.Add(TileHelpers.ToIndex(new Tile(tile.Suit, other)));
            }
        }
        return blocked;
    }
    private static void AddCall(List<SimAction> result, SimGame game, SimActionKind kind, Tile tile, Tile[] consumed)
    {
        var action = SimAction.Make(kind, tile, consumed);
        var remaining = game.Players[game.Actor].Hand.ToList();
        foreach (var copy in consumed)
            remaining.Remove(copy);
        var forbidden = Forbidden(action);
        if (remaining.Any(t => !forbidden.Contains(TileHelpers.ToIndex(t))))
            result.Add(action);
    }
    private static IEnumerable<Tile[]> Combinations(Tile[] tiles, int count)
    {
        var seen = new HashSet<string>();
        for (var mask = 0; mask < 1 << tiles.Length; mask++)
        {
            var chosen = tiles.Where((_, i) => (mask & (1 << i)) != 0).Order().ToArray();
            if (chosen.Length == count && seen.Add(string.Join(",", chosen)))
                yield return chosen;
        }
    }

    public static void ValidateConservation(SimGame game, int expectedPoints)
    {
        var tiles = game.LiveWall.Concat(game.DeadWall).Concat(game.Players.SelectMany(p => p.Hand
            .Concat(p.Melds.SelectMany(m => m.Shape.Tiles)).Concat(p.River.Where(d => !d.Claimed).Select(d => d.Tile)))).ToList();
        if (game.Phase == SimPhase.KanResponses || game.Phase == SimPhase.Ended && game.PendingMeldIndex >= 0
            && game.Players[game.PendingSeat].Melds[game.PendingMeldIndex].Shape.Type == MeldType.Pon
            && game.Result?.End is HandEnd.Ron or HandEnd.TripleRon)
            tiles.Add(game.PendingTile!.Value);
        if (!tiles.Select(SimTiles.Physical).Order().SequenceEqual(SimTiles.Set().Select(SimTiles.Physical).Order()))
            throw new InvalidOperationException("Physical tile conservation failed.");
        if (game.DeadWall.Length != 14 || game.Players.Sum(p => p.Score) + 1000 * game.RiichiSticks != expectedPoints)
            throw new InvalidOperationException("Dead-wall or point conservation failed.");
        if (game.Phase != SimPhase.Ended)
            for (var seat = 0; seat < 4; seat++)
            {
                var expected = game.Phase == SimPhase.Turn && seat == game.TurnSeat ? 14 : 13;
                if (game.Players[seat].Hand.Count + 3 * game.Players[seat].Melds.Count != expected)
                    throw new InvalidOperationException($"Hand arithmetic failed for seat {seat}.");
            }
    }
}
