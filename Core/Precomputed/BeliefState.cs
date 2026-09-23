using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Precomputed;

// This is a compact marginal belief, not a posterior over complete hidden hands.
public sealed record OpponentBelief(int Seat, double Tenpai, double Value, IReadOnlyList<double> Danger);

public static class BeliefState
{
    public const string ModelVersion = "closed-discard-proxy-v1";

    public static string Profile(PolicyWeights weights) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(ModelVersion + JsonSerializer.Serialize(weights))));

    // The caller owns the model or holds its update/query lock.
    public static IReadOnlyList<OpponentBelief> Capture(StateSnapshot state, IOpponentModel model)
    {
        model.Update(state);
        return Enumerable.Range(1, 3).Select(seat => new OpponentBelief(seat,
            model.TenpaiProbability(seat), model.Value(seat), Array.AsReadOnly(
                TileHelpers.AllTileTypes.Select(t => model.Danger(t, seat)).ToArray()))).ToArray();
    }
}

public sealed record StateIdentity(string Hash, string Verification);

// Component-wise incremental hash: XOR out each changed component, XOR in its
// replacement. Rebuilding from a snapshot gives the same result after missed events,
// round resets or out-of-order worker completion. Sequence is deliberately excluded.
public sealed class IncrementalStateKey
{
    private readonly object gate = new();
    private string[] previous = [];
    private readonly byte[] hash = new byte[32];

    public StateIdentity Update(StateSnapshot state, IReadOnlyList<OpponentBelief>? beliefs = null, string profile = "")
    {
        var components = Components(state, beliefs, profile);
        lock (this.gate)
        {
            for (var i = 0; i < components.Length; i++)
            {
                if (this.previous.Length > i && this.previous[i] == components[i])
                    continue;
                if (this.previous.Length > i)
                    this.Toggle(i, this.previous[i]);
                this.Toggle(i, components[i]);
            }
            this.previous = components;
            // An independent canonical digest guards against composite-hash collisions.
            var verification = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(components)));
            return new StateIdentity(Convert.ToHexString(this.hash), Convert.ToHexString(verification));
        }
    }

    public static StateIdentity Create(StateSnapshot state, IReadOnlyList<OpponentBelief>? beliefs = null, string profile = "") =>
        new IncrementalStateKey().Update(state, beliefs, profile);

    private void Toggle(int index, string value)
    {
        var part = SHA256.HashData(Encoding.UTF8.GetBytes(index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value));
        for (var i = 0; i < this.hash.Length; i++)
            this.hash[i] ^= part[i];
    }

    private static string[] Components(StateSnapshot s, IReadOnlyList<OpponentBelief>? beliefs, string profile)
    {
        static string Tiles(IEnumerable<Tile> tiles) => string.Join(",", tiles.Select(t => t.ToString()));
        static string MeldKey(Meld m) => $"{(int)m.Type}:{m.IsOpen}:{string.Join(",", m.Tiles.Order().Select(t => t.ToString()))}";
        static string Melds(IEnumerable<Meld> melds) => string.Join(";", melds.Select(MeldKey).Order(StringComparer.Ordinal));
        static string Json(object value) => JsonSerializer.Serialize(value);
        return
        [
            Json(new { Schema = 1, profile }),
            Json(new { Hand = Tiles(s.Hand.Order()), Draw = s.DrawnTile?.ToString(), Melds = Melds(s.OurMelds), s.OurRiichi }),
            Json(new { s.Phase, s.Legal, Call = s.CallTile?.ToString(), s.CallFromSeat, Shapes = Melds(s.CallShapes) })
                + (s.DiscardableTiles == null ? string.Empty : "|discardable:" + Tiles(s.DiscardableTiles.Order())),
            Json(new { s.RoundWind, s.SeatWind, s.DealerSeat, s.WallRemaining, s.Honba, s.RiichiSticks, s.HandNumber, s.Ruleset, s.LayoutHealthy }),
            Json(new { Dora = Tiles(s.DoraIndicators), Ura = Tiles(s.UraDoraIndicators) }),
            Json(s.Seats.Select(seat => new { seat.Seat, Discards = Tiles(seat.Discards), Melds = Melds(seat.Melds),
                seat.Riichi, seat.RiichiDiscardIndex, seat.Score, seat.DiscardsVerified, seat.DiscardCount, seat.DiscardOrder,
                seat.ClaimedDiscardIndices }).ToArray()),
            Json(beliefs ?? []),
        ];
    }
}
