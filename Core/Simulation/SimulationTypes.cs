using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Precomputed;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Simulation;

public sealed record SimulationRules
{
    public const string Version = "doman-four-player-v1";
    public bool Kuitan { get; init; } = true;
    public int HandsInMatch { get; init; } = 8;
    public int StartingScore { get; init; } = 25000;
    public int DoubleWindPairFu { get; init; } = 4;
    public int MaxHandsPerMatch { get; init; } = 256;

    public void Validate()
    {
        if (this.HandsInMatch is not (4 or 8) || this.StartingScore < 1000 || this.StartingScore > 100000
            || this.DoubleWindPairFu is not (2 or 4) || this.MaxHandsPerMatch < this.HandsInMatch)
            throw new ArgumentException("Invalid Doman simulation rules.");
    }

    public string Profile(PolicyWeights weights, double placementWeight = 0) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Version + JsonSerializer.Serialize(this) + JsonSerializer.Serialize(weights) + JsonSerializer.Serialize(placementWeight))));
}

public enum SimActionKind { Discard, Riichi, Tsumo, Ron, Pass, Chi, Pon, OpenKan, ClosedKan, AddedKan, NineTerminals }
public enum SimPhase { Turn, DiscardResponses, KanResponses, Ended }
public enum HandEnd { Tsumo, Ron, ExhaustiveDraw, NagashiMangan, NineTerminals, TripleRon, FourKans, FourWinds }

// Physical tile codes and sorted consumption make identities stable across processes.
public sealed record SimAction(SimActionKind Kind, string? Tile = null, string Consumed = "")
{
    public string Key => $"{this.Kind}:{this.Tile ?? "-"}:{this.Consumed}";
    public Tile[] ConsumedTiles() => this.Consumed.Length == 0 ? [] : this.Consumed.Split(',').Select(Core.Tile.Parse).ToArray();
    public static SimAction Make(SimActionKind kind, Tile? tile = null, IEnumerable<Tile>? consumed = null) =>
        new(kind, tile?.ToString(), string.Join(",", (consumed ?? []).Order().Select(t => t.ToString())));
}

public sealed record SimDiscard(Tile Tile, int Order, bool Riichi = false, bool Claimed = false, bool Tsumogiri = false);
public sealed record SimMeld(Meld Shape, int FromSeat);

public sealed class SimPlayer
{
    public List<Tile> Hand { get; set; } = [];
    public List<SimMeld> Melds { get; set; } = [];
    public List<SimDiscard> River { get; set; } = [];
    public int Score { get; set; }
    public bool Riichi { get; set; }
    public bool RiichiPaid { get; set; }
    public bool DoubleRiichi { get; set; }
    public bool Ippatsu { get; set; }
    public bool TemporaryFuriten { get; set; }
    public bool RiichiFuriten { get; set; }
    public int DrawCount { get; set; }
    public int DragonLiability { get; set; } = -1;
    public int WindLiability { get; set; } = -1;
    public SimPlayer Clone() => new()
    {
        Hand = this.Hand.ToList(), Melds = this.Melds.Select(m => new SimMeld(SimTiles.Meld(m.Shape.Type, m.Shape.Tiles, m.Shape.IsOpen), m.FromSeat)).ToList(),
        River = this.River.ToList(), Score = this.Score, Riichi = this.Riichi, RiichiPaid = this.RiichiPaid,
        DoubleRiichi = this.DoubleRiichi, Ippatsu = this.Ippatsu, TemporaryFuriten = this.TemporaryFuriten,
        RiichiFuriten = this.RiichiFuriten, DrawCount = this.DrawCount, DragonLiability = this.DragonLiability, WindLiability = this.WindLiability,
    };
}

public sealed record SimHandResult(HandEnd End, int[] Winners, int[] Deltas, bool DealerRepeats, bool[] Tenpai);

public sealed class SimGame
{
    public SimulationRules Rules { get; set; } = new();
    public SimPlayer[] Players { get; set; } = Enumerable.Range(0, 4).Select(_ => new SimPlayer()).ToArray();
    public List<Tile> LiveWall { get; set; } = [];
    // Four rinshan slots, then five alternating dora/ura pairs. Spent rinshan slots
    // receive the last live tile, preserving a physical fourteen-tile dead wall.
    public Tile[] DeadWall { get; set; } = [];
    public int KanCount { get; set; }
    public int Dealer { get; set; }
    public int InitialDealer { get; set; }
    public int RoundIndex { get; set; }
    public int Honba { get; set; }
    public int RiichiSticks { get; set; }
    public int TurnSeat { get; set; }
    public Tile? DrawnTile { get; set; }
    public bool Rinshan { get; set; }
    public bool Interrupted { get; set; }
    public bool AbortAfterDiscard { get; set; }
    public HashSet<int> ForbiddenDiscards { get; set; } = [];
    public SimPhase Phase { get; set; }
    public Tile? PendingTile { get; set; }
    public int PendingSeat { get; set; } = -1;
    public int PendingMeldIndex { get; set; } = -1;
    public bool PendingRiichi { get; set; }
    public bool LastDiscardWasRinshan { get; set; }
    public int[] ResponseOrder { get; set; } = [];
    public int ResponseIndex { get; set; }
    public SimAction?[] Responses { get; set; } = new SimAction?[4];
    public int DiscardCounter { get; set; }
    public int DecisionCount { get; set; }
    public int[] StartingScores { get; set; } = [];
    public SimHandResult? Result { get; set; }
    public int Actor => this.Phase == SimPhase.Turn ? this.TurnSeat : this.Phase == SimPhase.Ended ? -1 : this.ResponseOrder[this.ResponseIndex];
    public IReadOnlyList<Tile> Dora => Enumerable.Range(0, Math.Min(5, this.KanCount + 1)).Select(i => this.DeadWall[4 + i * 2]).ToArray();

    public SimGame Clone() => new()
    {
        Rules = this.Rules, Players = this.Players.Select(p => p.Clone()).ToArray(), LiveWall = this.LiveWall.ToList(), DeadWall = this.DeadWall.ToArray(),
        KanCount = this.KanCount, Dealer = this.Dealer, InitialDealer = this.InitialDealer, RoundIndex = this.RoundIndex, Honba = this.Honba,
        RiichiSticks = this.RiichiSticks, TurnSeat = this.TurnSeat, DrawnTile = this.DrawnTile, Rinshan = this.Rinshan, Interrupted = this.Interrupted,
        AbortAfterDiscard = this.AbortAfterDiscard, ForbiddenDiscards = this.ForbiddenDiscards.ToHashSet(), Phase = this.Phase,
        PendingTile = this.PendingTile, PendingSeat = this.PendingSeat, PendingMeldIndex = this.PendingMeldIndex,
        PendingRiichi = this.PendingRiichi, LastDiscardWasRinshan = this.LastDiscardWasRinshan,
        ResponseOrder = this.ResponseOrder.ToArray(), ResponseIndex = this.ResponseIndex, Responses = this.Responses.ToArray(),
        DiscardCounter = this.DiscardCounter, DecisionCount = this.DecisionCount, StartingScores = this.StartingScores.ToArray(), Result = this.Result,
    };
}

public static class SimTiles
{
    public static int Physical(Tile t) => t.IsRedFive ? 34 + (int)t.Suit : TileHelpers.ToIndex(t);
    public static Tile FromPhysical(int i) => i < 34 ? TileHelpers.FromIndex(i) : new Tile((TileSuit)(i - 34), 5, true);
    public static List<Tile> Set() => Enumerable.Range(0, 37).SelectMany(i => Enumerable.Repeat(FromPhysical(i),
        i >= 34 ? 1 : i is 4 or 13 or 22 ? 3 : 4)).ToList();
    public static void Shuffle<T>(IList<T> values, Random random)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
    public static Meld Meld(MeldType type, IEnumerable<Tile> tiles, bool open)
    {
        var copies = tiles.Order().ToArray();
        var meld = new Meld(type, copies, open);
        copies.CopyTo(meld.Tiles, 0);
        return meld;
    }
    public static ulong Waits(IEnumerable<Tile> tiles, int melds)
    {
        var hand = tiles.ToList();
        if (hand.Count + 3 * melds != 13 || Shanten.Calculate(hand, melds) != 0)
            return 0;
        ulong mask = 0;
        foreach (var tile in Shanten.GetUsefulTiles(hand, melds))
            mask |= 1UL << TileHelpers.ToIndex(tile);
        return mask;
    }
    public static ulong Waits(IEnumerable<Tile> tiles, IEnumerable<Meld> melds)
    {
        var hand = tiles.ToArray();
        var called = melds.ToArray();
        var waits = Waits(hand, called.Length);
        foreach (var group in hand.Concat(called.SelectMany(m => m.Tiles)).GroupBy(TileHelpers.ToIndex))
            if (group.Count() >= 4)
                waits &= ~(1UL << group.Key);
        return waits;
    }
}
