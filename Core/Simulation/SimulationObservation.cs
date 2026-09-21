using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Precomputed;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Simulation;

public sealed record PublicPlayer(int ConcealedCount, SimMeld[] Melds, SimDiscard[] River, int Score,
    bool Riichi, bool RiichiPaid, bool DoubleRiichi, bool Ippatsu, int DrawCount, int DragonLiability, int WindLiability);

// All seats are rotated so the deciding player is seat 0. No opponent concealed
// tiles, future wall order, unrevealed indicators or prior private responses appear.
public sealed record SimulationObservation(StateSnapshot Snapshot, SimulationRules Rules, PublicPlayer[] Players,
    SimPhase Phase, int TurnSeat, int InitialDealer, int RoundIndex, int KanCount, bool Rinshan, bool Interrupted,
    bool AbortAfterDiscard, int[] ForbiddenDiscards, int PendingSeat, Tile? PendingTile, int PendingMeldIndex,
    bool PendingRiichi, bool LastDiscardWasRinshan, int DiscardCounter, bool OurTemporaryFuriten, bool OurRiichiFuriten)
{
    private static readonly JsonSerializerOptions Json = SnapshotJson.CreateOptions();
    public string InformationKey() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json))));

    public static SimulationObservation Observe(SimGame game, IReadOnlyList<SimAction>? legal = null)
    {
        if (game.Actor < 0)
            throw new ArgumentException("Cannot observe a decision after the hand ends.");
        legal ??= RiichiSimulator.Legal(game);
        var viewer = game.Actor;
        int Relative(int seat) => seat < 0 ? -1 : (seat - viewer + 4) % 4;
        var players = Enumerable.Range(0, 4).Select(relative =>
        {
            var p = game.Players[(viewer + relative) % 4];
            return new PublicPlayer(p.Hand.Count, p.Melds.Select(m => new SimMeld(SimTiles.Meld(m.Shape.Type, m.Shape.Tiles, m.Shape.IsOpen), Relative(m.FromSeat))).ToArray(),
                p.River.ToArray(), p.Score, p.Riichi, p.RiichiPaid, p.DoubleRiichi, p.Ippatsu, p.DrawCount,
                Relative(p.DragonLiability), Relative(p.WindLiability));
        }).ToArray();
        var seats = players.Select((p, i) => new SeatState(i, p.River.Select(d => d.Tile).ToArray(), p.Melds.Select(m => m.Shape).ToArray(),
            p.Riichi, Array.FindIndex(p.River, d => d.Riichi), p.Score)
        {
            DiscardCount = p.River.Length, DiscardOrder = p.River.Select(d => d.Order).ToArray(),
            ClaimedDiscardIndices = Enumerable.Range(0, p.River.Length).Where(j => p.River[j].Claimed).ToArray(),
        }).ToArray();
        var flags = legal.Aggregate(LegalAction.None, (f, a) => f | Flag(a.Kind));
        var phase = game.Phase != SimPhase.Turn ? GamePhase.CallPrompt
            : (flags & ~LegalAction.Discard) != 0 ? GamePhase.SelfDeclare : GamePhase.OurTurn;
        var own = game.Players[viewer];
        var snapshot = StateSnapshot.Empty with
        {
            Phase = phase, Hand = own.Hand.Order().ToArray(), DrawnTile = game.Phase == SimPhase.Turn ? game.DrawnTile : null,
            OurMelds = seats[0].Melds, Seats = seats, DoraIndicators = game.Dora.ToArray(),
            RoundWind = (Wind)(1 + game.RoundIndex / 4), SeatWind = (Wind)(1 + (viewer - game.Dealer + 4) % 4),
            DealerSeat = Relative(game.Dealer), WallRemaining = game.LiveWall.Count, Honba = game.Honba, RiichiSticks = game.RiichiSticks,
            OurRiichi = own.Riichi, Legal = flags, CallTile = game.Phase != SimPhase.Turn ? game.PendingTile : null,
            CallFromSeat = game.Phase != SimPhase.Turn ? Relative(game.PendingSeat) : -1,
            CallOptions = legal.Select(a => Label(a.Kind)).Where(s => s.Length > 0).Distinct().ToArray(),
            Ruleset = new RulesetOptions(game.Rules.Kuitan, game.Rules.HandsInMatch), HandNumber = game.RoundIndex % 4 + 1,
        };
        return new SimulationObservation(snapshot, game.Rules, players, game.Phase, Relative(game.TurnSeat), Relative(game.InitialDealer),
            game.RoundIndex, game.KanCount, game.Rinshan, game.Interrupted, game.AbortAfterDiscard, game.ForbiddenDiscards.Order().ToArray(),
            game.Phase == SimPhase.Turn ? -1 : Relative(game.PendingSeat), game.Phase == SimPhase.Turn ? null : game.PendingTile,
            game.Phase == SimPhase.KanResponses ? game.PendingMeldIndex : -1, game.PendingRiichi, game.LastDiscardWasRinshan,
            game.DiscardCounter, own.TemporaryFuriten, own.RiichiFuriten);
    }

    public static LegalAction Flag(SimActionKind kind) => kind switch
    {
        SimActionKind.Discard => LegalAction.Discard, SimActionKind.Riichi => LegalAction.Riichi,
        SimActionKind.Tsumo => LegalAction.Tsumo, SimActionKind.Ron => LegalAction.Ron, SimActionKind.Pass => LegalAction.Pass,
        SimActionKind.Chi => LegalAction.Chi, SimActionKind.Pon => LegalAction.Pon, SimActionKind.OpenKan => LegalAction.MinKan,
        SimActionKind.ClosedKan => LegalAction.AnKan, SimActionKind.AddedKan => LegalAction.ShouMinKan, _ => LegalAction.None,
    };
    public static string Label(SimActionKind kind) => kind switch
    {
        SimActionKind.Riichi => "Riichi", SimActionKind.Tsumo => "Tsumo", SimActionKind.Ron => "Ron", SimActionKind.Pass => "Pass",
        SimActionKind.Chi => "Chi", SimActionKind.Pon => "Pon", SimActionKind.OpenKan or SimActionKind.ClosedKan or SimActionKind.AddedKan => "Kan", _ => "",
    };
}
