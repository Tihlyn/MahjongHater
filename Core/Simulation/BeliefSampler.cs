using MahjongHater.Core.Policy;

namespace MahjongHater.Core.Simulation;

public sealed class BeliefSamplingException(string message) : Exception(message);

// Approximate conditioned proposals, not an exact Bayesian posterior. Uniform
// unseen allocations are improved by annealed tile swaps to satisfy declared
// riichi and sampled tenpai targets. Every proposal conserves all physical tiles.
public sealed class BeliefSampler(PolicyWeights? weights = null)
{
    private readonly PolicyWeights weights = weights ?? PolicyWeights.Default;

    public SimGame Sample(SimulationObservation observation, Random random, CancellationToken ct = default, int maxSwaps = 12000)
    {
        ct.ThrowIfCancellationRequested();
        var s = observation.Snapshot;
        var game = new SimGame
        {
            Rules = observation.Rules, Dealer = s.DealerSeat, InitialDealer = observation.InitialDealer,
            RoundIndex = observation.RoundIndex, Honba = s.Honba, RiichiSticks = s.RiichiSticks,
            TurnSeat = observation.TurnSeat, DrawnTile = s.DrawnTile, Rinshan = observation.Rinshan,
            Interrupted = observation.Interrupted, AbortAfterDiscard = observation.AbortAfterDiscard,
            ForbiddenDiscards = observation.ForbiddenDiscards.ToHashSet(), Phase = observation.Phase,
            PendingSeat = observation.PendingSeat, PendingTile = observation.PendingTile, PendingMeldIndex = observation.PendingMeldIndex,
            PendingRiichi = observation.PendingRiichi, LastDiscardWasRinshan = observation.LastDiscardWasRinshan,
            DiscardCounter = observation.DiscardCounter, KanCount = observation.KanCount,
            StartingScores = observation.Players.Select(p => p.Score).ToArray(),
        };
        var unseen = SimTiles.Set();
        void Remove(Tile tile)
        {
            if (!unseen.Remove(tile))
                throw new ArgumentException("Observation has inconsistent visible physical tiles.");
        }
        for (var seat = 0; seat < 4; seat++)
        {
            var p = observation.Players[seat];
            game.Players[seat] = new SimPlayer { Score = p.Score, Melds = p.Melds.ToList(), River = p.River.ToList(), Riichi = p.Riichi,
                RiichiPaid = p.RiichiPaid, DoubleRiichi = p.DoubleRiichi, Ippatsu = p.Ippatsu, DrawCount = p.DrawCount,
                DragonLiability = p.DragonLiability, WindLiability = p.WindLiability };
            foreach (var tile in p.Melds.SelectMany(m => m.Shape.Tiles).Concat(p.River.Where(d => !d.Claimed).Select(d => d.Tile)))
                Remove(tile);
        }
        game.Players[0].Hand = s.Hand.ToList();
        game.Players[0].TemporaryFuriten = observation.OurTemporaryFuriten;
        game.Players[0].RiichiFuriten = observation.OurRiichiFuriten;
        foreach (var tile in s.Hand.Concat(s.DoraIndicators))
            Remove(tile);
        if (observation.Phase == SimPhase.KanResponses)
            Remove(observation.PendingTile!.Value);
        var expected = observation.Players.Skip(1).Sum(p => p.ConcealedCount) + s.WallRemaining + 14 - s.DoraIndicators.Count;
        if (unseen.Count != expected || s.DoraIndicators.Count != observation.KanCount + 1)
            throw new ArgumentException("Observation wall, meld and concealed counts do not reconcile.");
        SimTiles.Shuffle(unseen, random);
        var cursor = 0;
        for (var seat = 1; seat < 4; seat++)
        {
            game.Players[seat].Hand = unseen.Skip(cursor).Take(observation.Players[seat].ConcealedCount).ToList();
            cursor += observation.Players[seat].ConcealedCount;
        }
        var reservoir = unseen.Skip(cursor).ToList();
        var model = new OpponentModel(this.weights);
        model.Update(s);
        var targets = new bool[4];
        for (var seat = 1; seat < 4; seat++)
            targets[seat] = game.Players[seat].Riichi || random.NextDouble() < model.TenpaiProbability(seat);
        Condition(game, reservoir, targets, random, maxSwaps, ct);
        // A passed shape-completing discard after riichi implies lasting furiten.
        // Otherwise a missed current turn is unknown and sampled as not furiten.
        for (var seat = 1; seat < 4; seat++)
        {
            var p = game.Players[seat];
            if (!p.Riichi)
                continue;
            var declaration = p.River.FirstOrDefault(d => d.Riichi)?.Order ?? int.MaxValue;
            var waits = SimTiles.Waits(p.Hand, p.Melds.Select(m => m.Shape));
            p.RiichiFuriten = game.Players.Where((_, i) => i != seat).SelectMany(o => o.River)
                .Any(d => d.Order > declaration && d.Order < observation.DiscardCounter - 1 && (waits & (1UL << TileHelpers.ToIndex(d.Tile))) != 0);
        }
        SimTiles.Shuffle(reservoir, random);
        game.LiveWall = reservoir.Take(s.WallRemaining).ToList();
        cursor = s.WallRemaining;
        game.DeadWall = new Tile[14];
        for (var i = 0; i < 14; i++)
            game.DeadWall[i] = i >= 4 && i % 2 == 0 && (i - 4) / 2 < s.DoraIndicators.Count
                ? s.DoraIndicators[(i - 4) / 2] : reservoir[cursor++];
        if (observation.Phase != SimPhase.Turn)
        {
            // Earlier response submissions are private. Resample them after our
            // response; resolution priority uses seat order, not submission order.
            game.ResponseOrder = new[] { 0 }.Concat(Enumerable.Range(1, 3).Where(seat => seat != observation.PendingSeat)).ToArray();
            game.ResponseIndex = 0;
        }
        RiichiSimulator.ValidateConservation(game, game.Players.Sum(p => p.Score) + 1000 * game.RiichiSticks);
        return game;
    }

    private static void Condition(SimGame game, List<Tile> reservoir, bool[] targets, Random random, int maxSwaps, CancellationToken ct)
    {
        int ShantenAt(int seat)
        {
            var p = game.Players[seat];
            var value = Shanten.Calculate(p.Hand, p.Melds.Count);
            return value == 0 && SimTiles.Waits(p.Hand, p.Melds.Select(m => m.Shape)) == 0 ? 1 : value;
        }
        var shanten = Enumerable.Range(0, 4).Select(s => s == 0 ? 0 : ShantenAt(s)).ToArray();
        var active = Enumerable.Range(1, 3).Where(s => targets[s]).ToArray();
        if (active.Length == 0)
            return;
        double Energy(int seat, int value) => !targets[seat] ? 0 : Math.Abs(value) * (game.Players[seat].Riichi ? 5 : 1);
        for (var step = 0; step < maxSwaps && active.Any(s => shanten[s] != 0); step++)
        {
            if (step % 64 == 0)
                ct.ThrowIfCancellationRequested();
            var seat = active[random.Next(active.Length)];
            var own = game.Players[seat].Hand;
            var otherSeat = random.Next(4); // 0 means the unassigned wall reservoir, never our known hand.
            if (otherSeat == seat)
                otherSeat = 0;
            var other = otherSeat == 0 ? reservoir : game.Players[otherSeat].Hand;
            var a = random.Next(own.Count);
            var b = random.Next(other.Count);
            (own[a], other[b]) = (other[b], own[a]);
            var next = ShantenAt(seat);
            var nextOther = otherSeat == 0 ? 0 : ShantenAt(otherSeat);
            var delta = Energy(seat, next) - Energy(seat, shanten[seat]);
            if (otherSeat != 0)
                delta += Energy(otherSeat, nextOther) - Energy(otherSeat, shanten[otherSeat]);
            var temperature = 0.08 + 0.7 * (1 - step % 1500 / 1500.0);
            if (delta <= 0 || random.NextDouble() < Math.Exp(-delta / temperature))
            {
                shanten[seat] = next;
                if (otherSeat != 0)
                    shanten[otherSeat] = nextOther;
            }
            else
                (own[a], other[b]) = (other[b], own[a]);
        }
        if (Enumerable.Range(1, 3).Any(s => game.Players[s].Riichi && shanten[s] != 0))
            throw new BeliefSamplingException("Could not sample a tile-conserving tenpai hand for every riichi opponent within the belief budget.");
    }

    public static void ReshuffleHiddenWall(SimGame game, Random random)
    {
        var hiddenIndices = Enumerable.Range(0, 14).Where(i => !(i >= 4 && i % 2 == 0 && (i - 4) / 2 <= game.KanCount)).ToArray();
        var hidden = game.LiveWall.Concat(hiddenIndices.Select(i => game.DeadWall[i])).ToList();
        SimTiles.Shuffle(hidden, random);
        var cursor = game.LiveWall.Count;
        game.LiveWall = hidden.Take(cursor).ToList();
        foreach (var index in hiddenIndices)
            game.DeadWall[index] = hidden[cursor++];
    }
}
