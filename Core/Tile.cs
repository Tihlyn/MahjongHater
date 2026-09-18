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
            TileSuit.Man => $"{tile.Number} Characters",
            TileSuit.Pin => $"{tile.Number} Dots",
            TileSuit.Sou => $"{tile.Number} Bamboo",
            TileSuit.Wind => tile.Number switch
            {
                1 => "East Wind",
                2 => "South Wind",
                3 => "West Wind",
                4 => "North Wind",
                _ => throw new InvalidOperationException(),
            },
            TileSuit.Dragon => tile.Number switch
            {
                1 => "White Dragon",
                2 => "Green Dragon",
                3 => "Red Dragon",
                _ => throw new InvalidOperationException(),
            },
            _ => throw new InvalidOperationException(),
        };
    }

    public static int CountKind(IEnumerable<Tile> tiles, Tile tile)
    {
        return tiles.Count(candidate => SameKind(candidate, tile));
    }

    // Maps FFXIV tile icon IDs to tiles.
    // 76041-76049: 1m-9m | 76050-76058: 1p-9p | 76059-76067: 1s-9s
    // 76068-76071: E/S/W/N winds | 76072-76074: Haku/Hatsu/Chun
    // 76075-76077: red fives 0m/0p/0s (confirmed via hover pairing: 76076 → red 5p).
    public static bool TryTileFromIconId(int iconId, out Tile tile)
    {
        tile = default;
        switch (iconId - 76041)
        {
            case >= 0 and <= 8:   tile = new Tile(TileSuit.Man,    iconId - 76041 + 1); return true;
            case >= 9 and <= 17:  tile = new Tile(TileSuit.Pin,    iconId - 76050 + 1); return true;
            case >= 18 and <= 26: tile = new Tile(TileSuit.Sou,    iconId - 76059 + 1); return true;
            case 27: tile = new Tile(TileSuit.Wind,   1); return true;
            case 28: tile = new Tile(TileSuit.Wind,   2); return true;
            case 29: tile = new Tile(TileSuit.Wind,   3); return true;
            case 30: tile = new Tile(TileSuit.Wind,   4); return true;
            case 31: tile = new Tile(TileSuit.Dragon, 1); return true;
            case 32: tile = new Tile(TileSuit.Dragon, 2); return true;
            case 33: tile = new Tile(TileSuit.Dragon, 3); return true;
            case 34: tile = new Tile(TileSuit.Man, 5, isRedFive: true); return true;
            case 35: tile = new Tile(TileSuit.Pin, 5, isRedFive: true); return true;
            case 36: tile = new Tile(TileSuit.Sou, 5, isRedFive: true); return true;
            default: return false;
        }
    }

    public static List<Tile> Sort(IEnumerable<Tile> tiles)
    {
        return tiles.Select(Normalize).OrderBy(t => t).ToList();
    }
}
