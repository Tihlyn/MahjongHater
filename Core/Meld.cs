namespace MahjongHater.Core;

public enum MeldType
{
    Chi,
    Pon,
    Daiminkan,
    Ankan,
    Shouminkan,
    Pair,
}

public sealed class Meld
{
    public Meld(MeldType type, Tile[] tiles, bool isOpen = false)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.Length is < 2 or > 4)
        {
            throw new ArgumentException("Melds must contain between two and four tiles.", nameof(tiles));
        }

        this.Type = type;
        this.Tiles = tiles.Select(TileHelpers.Normalize).OrderBy(tile => tile).ToArray();
        this.Opened = type switch
        {
            MeldType.Ankan or MeldType.Pair => false,
            _ => isOpen,
        };
    }

    public MeldType Type { get; }

    public Tile[] Tiles { get; }

    public bool Opened { get; }

    public bool IsOpen => this.Type != MeldType.Ankan && this.Type != MeldType.Pair && this.Opened;

    public bool IsTriplet => this.Type is MeldType.Pon or MeldType.Daiminkan or MeldType.Ankan or MeldType.Shouminkan;

    public bool IsKan => this.Type is MeldType.Daiminkan or MeldType.Ankan or MeldType.Shouminkan;

    public bool IsSequence => this.Type == MeldType.Chi;

    public bool IsTerminalOrHonor => this.Tiles[0].IsTerminalOrHonor;

    public static Meld MakeChi(Tile t1, Tile t2, Tile t3)
    {
        var tiles = new[] { TileHelpers.Normalize(t1), TileHelpers.Normalize(t2), TileHelpers.Normalize(t3) }
            .OrderBy(tile => tile)
            .ToArray();

        if (tiles.Any(tile => tile.IsHonor) || tiles.Select(tile => tile.Suit).Distinct().Count() != 1)
        {
            throw new ArgumentException("A chi must be in the same numbered suit.");
        }

        if (tiles[0].Number + 1 != tiles[1].Number || tiles[1].Number + 1 != tiles[2].Number)
        {
            throw new ArgumentException("A chi must be a consecutive sequence.");
        }

        return new Meld(MeldType.Chi, tiles, false);
    }

    public static Meld MakePon(Tile t, bool isOpen)
    {
        var normalized = TileHelpers.Normalize(t);
        return new Meld(MeldType.Pon, [normalized, normalized, normalized], isOpen);
    }

    public static Meld MakeKan(Tile t, MeldType kanType)
    {
        if (kanType is not MeldType.Daiminkan and not MeldType.Ankan and not MeldType.Shouminkan)
        {
            throw new ArgumentException("kanType must be a kan meld type.", nameof(kanType));
        }

        var normalized = TileHelpers.Normalize(t);
        return new Meld(kanType, [normalized, normalized, normalized, normalized], kanType != MeldType.Ankan);
    }

    public static Meld MakePair(Tile t)
    {
        var normalized = TileHelpers.Normalize(t);
        return new Meld(MeldType.Pair, [normalized, normalized], false);
    }

    public int FuValue(bool isOpen)
    {
        if (this.IsSequence || this.Type == MeldType.Pair)
        {
            return 0;
        }

        var terminalOrHonor = this.Tiles[0].IsTerminalOrHonor;
        return this.Type switch
        {
            MeldType.Pon => terminalOrHonor ? (isOpen ? 4 : 8) : (isOpen ? 2 : 4),
            MeldType.Daiminkan or MeldType.Shouminkan => terminalOrHonor ? 16 : 8,
            MeldType.Ankan => terminalOrHonor ? 32 : 16,
            _ => 0,
        };
    }
}
