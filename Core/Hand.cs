namespace MahjongHater.Core;

public sealed class Hand
{
    public List<Tile> ClosedTiles { get; } = [];

    public List<Meld> CalledMelds { get; } = [];

    public Tile? WinningTile { get; set; }

    public WinMethod WinMethod { get; set; } = WinMethod.Ron;

    public bool IsRiichi { get; set; }

    public bool IsDoubleRiichi { get; set; }

    public bool IsIppatsu { get; set; }

    public bool IsRinshan { get; set; }

    public bool IsChankan { get; set; }

    public bool IsHaitei { get; set; }

    public bool IsHoutei { get; set; }

    public bool IsFirstDraw { get; set; }

    public Wind SeatWind { get; set; } = Wind.East;

    public Wind RoundWind { get; set; } = Wind.East;

    public int DoraCount { get; set; }

    public int AkadoraCount { get; set; }

    public int UraDoraCount { get; set; }

    public bool IsOpen => this.CalledMelds.Any(m => m.IsOpen);

    public List<Tile> AllTiles => this.ClosedTiles
        .Concat(this.CalledMelds.SelectMany(meld => meld.Tiles))
        .Select(TileHelpers.Normalize)
        .OrderBy(tile => tile)
        .ToList();
}

public enum WinMethod
{
    Ron,
    Tsumo,
}

public enum Wind
{
    East = 1,
    South = 2,
    West = 3,
    North = 4,
}
