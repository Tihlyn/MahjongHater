using System.Globalization;

namespace MahjongHater.Core;

public enum TileSuit
{
    Man = 0,
    Pin = 1,
    Sou = 2,
    Wind = 3,
    Dragon = 4,
}

public readonly struct Tile : IEquatable<Tile>, IComparable<Tile>
{
    public Tile(TileSuit suit, int number, bool isRedFive = false)
    {
        Validate(suit, number, isRedFive);
        this.Suit = suit;
        this.Number = number;
        this.IsRedFive = isRedFive;
    }

    public TileSuit Suit { get; }

    public int Number { get; }

    public bool IsRedFive { get; }

    public bool IsTerminal => this.Suit <= TileSuit.Sou && (this.Number == 1 || this.Number == 9);

    public bool IsHonor => this.Suit >= TileSuit.Wind;

    public bool IsSimple => !this.IsTerminal && !this.IsHonor;

    public bool IsTerminalOrHonor => this.IsTerminal || this.IsHonor;

    public static Tile Parse(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 2)
        {
            throw new FormatException($"Invalid tile code '{code}'.");
        }

        var numberChar = code[0];
        var suitChar = char.ToLowerInvariant(code[1]);
        if (!char.IsDigit(numberChar))
        {
            throw new FormatException($"Invalid tile code '{code}'.");
        }

        var rawNumber = numberChar - '0';
        return suitChar switch
        {
            'm' => CreateNumberTile(TileSuit.Man, rawNumber),
            'p' => CreateNumberTile(TileSuit.Pin, rawNumber),
            's' => CreateNumberTile(TileSuit.Sou, rawNumber),
            'z' => CreateHonorTile(rawNumber),
            _ => throw new FormatException($"Unknown suit in tile code '{code}'."),
        };
    }

    public override string ToString()
    {
        return this.Suit switch
        {
            TileSuit.Man => $"{(this.IsRedFive ? '0' : (char)('0' + this.Number))}m",
            TileSuit.Pin => $"{(this.IsRedFive ? '0' : (char)('0' + this.Number))}p",
            TileSuit.Sou => $"{(this.IsRedFive ? '0' : (char)('0' + this.Number))}s",
            TileSuit.Wind => $"{this.Number.ToString(CultureInfo.InvariantCulture)}z",
            TileSuit.Dragon => $"{(this.Number + 4).ToString(CultureInfo.InvariantCulture)}z",
            _ => throw new InvalidOperationException("Unknown tile suit."),
        };
    }

    public int CompareTo(Tile other)
    {
        var suitCompare = this.Suit.CompareTo(other.Suit);
        if (suitCompare != 0)
        {
            return suitCompare;
        }

        var numberCompare = this.Number.CompareTo(other.Number);
        if (numberCompare != 0)
        {
            return numberCompare;
        }

        return this.IsRedFive.CompareTo(other.IsRedFive);
    }

    public bool Equals(Tile other)
    {
        return this.Suit == other.Suit && this.Number == other.Number && this.IsRedFive == other.IsRedFive;
    }

    public override bool Equals(object? obj)
    {
        return obj is Tile other && this.Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine((int)this.Suit, this.Number, this.IsRedFive);
    }

    public static bool operator ==(Tile left, Tile right) => left.Equals(right);

    public static bool operator !=(Tile left, Tile right) => !left.Equals(right);

    private static Tile CreateNumberTile(TileSuit suit, int rawNumber)
    {
        return rawNumber switch
        {
            0 => new Tile(suit, 5, true),
            >= 1 and <= 9 => new Tile(suit, rawNumber),
            _ => throw new FormatException($"Invalid numbered tile value '{rawNumber}'."),
        };
    }

    private static Tile CreateHonorTile(int rawNumber)
    {
        return rawNumber switch
        {
            >= 1 and <= 4 => new Tile(TileSuit.Wind, rawNumber),
            >= 5 and <= 7 => new Tile(TileSuit.Dragon, rawNumber - 4),
            _ => throw new FormatException($"Invalid honor tile value '{rawNumber}'."),
        };
    }

    private static void Validate(TileSuit suit, int number, bool isRedFive)
    {
        switch (suit)
        {
            case TileSuit.Man or TileSuit.Pin or TileSuit.Sou when number is >= 1 and <= 9:
                if (isRedFive && number != 5)
                {
                    throw new ArgumentOutOfRangeException(nameof(isRedFive), "Only five tiles may be red.");
                }

                return;
            case TileSuit.Wind when number is >= 1 and <= 4:
                if (isRedFive)
                {
                    throw new ArgumentOutOfRangeException(nameof(isRedFive), "Honor tiles cannot be red.");
                }

                return;
            case TileSuit.Dragon when number is >= 1 and <= 3:
                if (isRedFive)
                {
                    throw new ArgumentOutOfRangeException(nameof(isRedFive), "Honor tiles cannot be red.");
                }

                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(number), "The tile number is invalid for its suit.");
        }
    }
}

internal static class TileHelpers
{
    private static readonly Tile[] AllTilesInternal =
    [
        .. Enumerable.Range(1, 9).Select(i => new Tile(TileSuit.Man, i)),
        .. Enumerable.Range(1, 9).Select(i => new Tile(TileSuit.Pin, i)),
        .. Enumerable.Range(1, 9).Select(i => new Tile(TileSuit.Sou, i)),
        .. Enumerable.Range(1, 4).Select(i => new Tile(TileSuit.Wind, i)),
        .. Enumerable.Range(1, 3).Select(i => new Tile(TileSuit.Dragon, i)),
    ];

    public static IReadOnlyList<Tile> AllTileTypes => AllTilesInternal;

    public static Tile Normalize(Tile tile)
    {
        return tile.IsRedFive ? new Tile(tile.Suit, tile.Number) : tile;
    }

    public static bool SameKind(Tile left, Tile right)
    {
        left = Normalize(left);
        right = Normalize(right);
        return left.Suit == right.Suit && left.Number == right.Number;
    }

    public static int ToIndex(Tile tile)
    {
        tile = Normalize(tile);
        return tile.Suit switch
        {
            TileSuit.Man => tile.Number - 1,
            TileSuit.Pin => 9 + tile.Number - 1,
            TileSuit.Sou => 18 + tile.Number - 1,
            TileSuit.Wind => 27 + tile.Number - 1,
            TileSuit.Dragon => 31 + tile.Number - 1,
            _ => throw new InvalidOperationException("Unsupported tile suit."),
        };
    }

    public static Tile FromIndex(int index)
    {
        return index switch
        {
            >= 0 and <= 8 => new Tile(TileSuit.Man, index + 1),
            >= 9 and <= 17 => new Tile(TileSuit.Pin, index - 8),
            >= 18 and <= 26 => new Tile(TileSuit.Sou, index - 17),
            >= 27 and <= 30 => new Tile(TileSuit.Wind, index - 26),
            >= 31 and <= 33 => new Tile(TileSuit.Dragon, index - 30),
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
    }

    public static string GetDisplayName(Tile tile)
    {
        tile = Normalize(tile);
        return tile.Suit switch
        {
            TileSuit.Man => $"{tile.Number}-man",
            TileSuit.Pin => $"{tile.Number}-pin",
            TileSuit.Sou => $"{tile.Number}-sou",
            TileSuit.Wind => tile.Number switch
            {
                1 => "East",
                2 => "South",
                3 => "West",
                4 => "North",
                _ => throw new InvalidOperationException(),
            },
            TileSuit.Dragon => tile.Number switch
            {
                1 => "Haku",
                2 => "Hatsu",
                3 => "Chun",
                _ => throw new InvalidOperationException(),
            },
            _ => throw new InvalidOperationException(),
        };
    }

    public static int CountKind(IEnumerable<Tile> tiles, Tile tile)
    {
        return tiles.Count(candidate => SameKind(candidate, tile));
    }

    public static List<Tile> Sort(IEnumerable<Tile> tiles)
    {
        return tiles.Select(Normalize).OrderBy(t => t).ToList();
    }
}
