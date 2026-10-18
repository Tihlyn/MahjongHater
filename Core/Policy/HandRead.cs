using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

public enum ReadKind
{
    Closed,    // no open meld: riichi or dama
    Flush,     // honitsu/chinitsu skeleton in FlushSuit
    Toitoi,
    Yakuhai,
    Tanyao,
    Other,
}

// What an open hand's calls and discards say it is going for. The rules mirror read_type in
// tools/open_hand_danger.py exactly, because open_danger.json was counted with them: change one
// and the other must follow (then regenerate the table).
public sealed record HandRead(ReadKind Kind, TileSuit? FlushSuit, IReadOnlyList<ReadKind> Tags)
{
    public static HandRead Closed { get; } = new(ReadKind.Closed, null, []);

    // Table context in open_danger.json ("open:flush", ...).
    public string TableContext => this.Kind switch
    {
        ReadKind.Closed => "dama",
        _ => "open:" + this.Kind.ToString().ToLowerInvariant(),
    };

    public string Describe() => this.Kind switch
    {
        ReadKind.Closed => "closed",
        ReadKind.Flush => $"{this.FlushSuit?.ToString().ToLowerInvariant()} flush read",
        ReadKind.Other => "open, no clear read",
        _ => $"{this.Kind.ToString().ToLowerInvariant()} read",
    };

    // `callsOnly`: read a flush from the calls alone (a novice-like river says nothing).
    public static HandRead Of(SeatState seat, Wind seatWind, Wind roundWind, bool callsOnly = false) =>
        Classify(seat, seatWind, roundWind, callsOnly, riverFlush: null);

    // V3: the reads the danger model blends, with weights summing to 1. Of() decides a flush
    // from the river on hard cutoffs (in-suit share 15 % / 20 %), so one tile flips the read and
    // every danger with it. Here a river-only flush is a probability that runs from 1 at a clean
    // river to 0 a tenth past the cutoff, and the hand counts as both reads in proportion. A
    // flush shown by two calls in one suit stays certain.
    public static IReadOnlyList<(HandRead Read, double Weight)> Alternatives(SeatState seat, Wind seatWind, Wind roundWind, bool callsOnly = false)
    {
        var read = Of(seat, seatWind, roundWind, callsOnly);
        if (callsOnly || RiverFlush(seat) is not { } river || Of(seat, seatWind, roundWind, callsOnly: true).Kind == ReadKind.Flush)
            return [(read, 1)];
        var flush = Math.Clamp(0.5 + (river.Cutoff - river.Share) / 0.2, 0, 1);
        if (flush <= 0)
            return [(read, 1)];
        var withFlush = Classify(seat, seatWind, roundWind, false, riverFlush: true);
        var without = Classify(seat, seatWind, roundWind, false, riverFlush: false);
        if (flush >= 1 || withFlush.Kind == without.Kind)
            return [(flush >= 0.5 ? withFlush : without, 1)];
        return [(withFlush, flush), (without, 1 - flush)];
    }

    // The suit a one-suit-or-honor caller's river avoids, its in-suit share and the cutoff Of()
    // applies to it; null when the calls span two suits or fewer than four number tiles are out.
    private static (TileSuit Suit, double Share, double Cutoff)? RiverFlush(SeatState seat)
    {
        var melds = seat.Melds.Where(m => m.IsOpen).ToList();
        var meldSuits = melds.Select(m => m.Tiles[0]).Where(t => !t.IsHonor).Select(t => t.Suit).Distinct().ToList();
        var numberDiscards = seat.Discards.Where(t => !t.IsHonor).ToList();
        if (melds.Count == 0 || meldSuits.Count > 1 || numberDiscards.Count < 4)
            return null;
        var suit = meldSuits.Count == 1 ? meldSuits[0]
            : new[] { TileSuit.Man, TileSuit.Pin, TileSuit.Sou }.OrderBy(s => numberDiscards.Count(t => t.Suit == s)).ThenBy(s => s).First();
        return (suit, (double)numberDiscards.Count(t => t.Suit == suit) / numberDiscards.Count, meldSuits.Count == 1 ? 0.2 : 0.15);
    }

    // `riverFlush`: null applies Of()'s cutoffs; true reads the river's suit as a flush
    // whatever the share; false never reads a flush from the river.
    private static HandRead Classify(SeatState seat, Wind seatWind, Wind roundWind, bool callsOnly, bool? riverFlush)
    {
        var melds = seat.Melds.Where(m => m.IsOpen).ToList();
        if (melds.Count == 0)
            return Closed;
        bool Yakuhai(Tile t) => t.Suit == TileSuit.Dragon || t.Suit == TileSuit.Wind && ((Wind)t.Number == seatWind || (Wind)t.Number == roundWind);
        var meldSuits = melds.Select(m => m.Tiles[0]).Where(t => !t.IsHonor).Select(t => t.Suit).Distinct().ToList();
        var numberDiscards = seat.Discards.Where(t => !t.IsHonor).ToList();
        var tags = new List<ReadKind>();
        TileSuit? flushSuit = null;
        if (meldSuits.Count <= 1)
        {
            var suit = meldSuits.Count == 1 ? meldSuits[0] : (TileSuit?)null;
            if (suit is null && numberDiscards.Count >= 4 && !callsOnly && riverFlush != false)
            {
                // Honor calls only: a flush when the number discards avoid one suit.
                var least = new[] { TileSuit.Man, TileSuit.Pin, TileSuit.Sou }
                    .Select(s => (Suit: s, Count: numberDiscards.Count(t => t.Suit == s))).OrderBy(x => x.Count).ThenBy(x => x.Suit).First();
                if (least.Count <= numberDiscards.Count * 0.15 || riverFlush == true)
                    suit = least.Suit;
            }

            if (suit is { } s)
            {
                var inSuit = numberDiscards.Count(t => t.Suit == s);
                var fromRiver = !callsOnly && numberDiscards.Count >= 4 && riverFlush != false
                                && (inSuit <= numberDiscards.Count * 0.2 || riverFlush == true);
                if (melds.Count >= 2 || fromRiver)
                {
                    tags.Add(ReadKind.Flush);
                    flushSuit = s;
                }
            }
        }

        if (melds.Count >= 2 && melds.All(m => m.Type != MeldType.Chi))
            tags.Add(ReadKind.Toitoi);
        if (melds.Any(m => m.Type != MeldType.Chi && Yakuhai(m.Tiles[0])))
            tags.Add(ReadKind.Yakuhai);
        if (melds.All(m => m.Tiles.All(t => t.IsSimple)))
            tags.Add(ReadKind.Tanyao);
        if (tags.Count == 0)
            tags.Add(ReadKind.Other);
        return new HandRead(tags[0], tags[0] == ReadKind.Flush ? flushSuit : null, tags);
    }
}
