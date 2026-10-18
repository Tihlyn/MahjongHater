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
    public static HandRead Of(SeatState seat, Wind seatWind, Wind roundWind, bool callsOnly = false)
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
            if (suit is null && numberDiscards.Count >= 4 && !callsOnly)
            {
                // Honor calls only: a flush when the number discards avoid one suit.
                var least = new[] { TileSuit.Man, TileSuit.Pin, TileSuit.Sou }
                    .Select(s => (Suit: s, Count: numberDiscards.Count(t => t.Suit == s))).OrderBy(x => x.Count).ThenBy(x => x.Suit).First();
                if (least.Count <= numberDiscards.Count * 0.15)
                    suit = least.Suit;
            }

            if (suit is { } s)
            {
                var inSuit = numberDiscards.Count(t => t.Suit == s);
                if (melds.Count >= 2 || (!callsOnly && numberDiscards.Count >= 4 && inSuit <= numberDiscards.Count * 0.2))
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
