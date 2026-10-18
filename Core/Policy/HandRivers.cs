using System.Text.Json;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

// One JSON line per finished hand in hand_rivers.jsonl: every seat's river (tile, tsumogiri,
// global order), calls and riichi tile, with the outcome. The other calibration files keep
// either threatening seats only or hand ends only, and dalamud.log rotates, so per-player
// style (OpponentProfiles) had nothing durable to be fitted on. Raw data only: profiles are
// recomputed offline from the rivers (tools/hand_rivers.py).
public static class HandRivers
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Line(StateSnapshot last, HandResult result, int winnerSeat) => JsonSerializer.Serialize(new
    {
        utc = result.Utc.ToString("O"),
        round = result.Round,
        hand = result.HandNumber,
        dealer = last.DealerSeat,
        outcome = result.Outcome,
        winner = winnerSeat,
        delta = result.Delta,
        population = result.Population,
        model = result.Model,
        dora = string.Join(" ", last.DoraIndicators),
        seats = last.Seats.Select(s => new
        {
            seat = s.Seat,
            score = s.Score,
            verified = s.DiscardsVerified,
            discards = string.Join(" ", s.Discards),
            tsumogiri = s.DiscardTsumogiri.Count == s.Discards.Count ? string.Concat(s.DiscardTsumogiri.Select(t => t ? '1' : '0')) : null,
            order = s.DiscardOrder.Count == s.Discards.Count ? s.DiscardOrder : null,
            riichi = s.Riichi ? s.RiichiDiscardIndex : -1,
            melds = (s.Seat == 0 ? last.OurMelds : s.Melds).Select(m => $"{m.Type}:{string.Concat(m.Tiles)}{(m.IsOpen ? "" : ":closed")}"),
        }),
    }, Json);
}
