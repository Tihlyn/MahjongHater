using System.Buffers.Binary;
using System.Text.Json;
using MahjongHater.Core;
using MahjongHater.Core.State;

namespace MahjongHater.Tests.State;

// Builds AddonEmj-shaped byte buffers for the struct reader tests, and loads the live
// dumps under resources/fixtures (docs/EMJ_STRUCT.md, "Fixtures"). The hand hex below is
// the first dump captured 2026-09-18 at +0x0DB8 (EU client): 1m 8m 3p 3p 1s 1s 5s r5s
// 8s 8s 9s S W + drawn E, followed by a pointer.
internal static class StructFixture
{
    public const string LiveHandHex =
        "092901001029010014290100142901001B2901001B2901001F2901002D29010022290100222901002329010025290100262901002429010044D9ECDC22020000";

    public const string LiveHand = "1m 8m 3p 3p 1s 1s 5s 0s 8s 8s 9s 2z 3z";

    public const string LiveDrawn = "1z";

    public static EmjLayout Layout { get; } = EmjLayout.Parse(EmjLayout.ReadEmbedded());

    // A struct meld record for synthetic buffers: 34-index (or the chi marker) + from-direction.
    public readonly record struct MeldRecord(int TileIndex, int From);

    public static byte[] Bytes(
        EmjLayout? layout = null,
        string? handHex = LiveHandHex,
        int[]? scores = null,
        int? doraIcon = 76054,
        byte[]? discardCounts = null,
        byte[]? closedCounts = null,
        byte[]? meldCounts = null,
        MeldRecord[]?[]? melds = null,
        byte[]? riichiIndices = null)
    {
        layout ??= Layout;
        var buf = new byte[Math.Max(layout.RequiredBytes, EmjLayout.MaxReadBytes)];
        if (handHex is not null)
        {
            var bytes = Convert.FromHexString(handHex);
            Array.Copy(bytes, 0, buf, layout.HandArray, Math.Min(bytes.Length, buf.Length - layout.HandArray));
        }

        scores ??= [25000, 25000, 25000, 25000];
        closedCounts ??= [13, 13, 13, 13];
        meldCounts ??= [0, 0, 0, 0];
        riichiIndices ??= [255, 255, 255, 255];
        for (var i = 0; i < 4; i++)
        {
            if (layout.Scores[i] is { } so)
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(so, 4), scores[i]);
            if (layout.DiscardCounts[i] is { } dc && discardCounts is not null)
                buf[dc] = discardCounts[i];
            if (layout.ClosedTileCounts[i] is { } cc)
                buf[cc] = closedCounts[i];
            if (layout.MeldCounts[i] is { } mc)
                buf[mc] = meldCounts[i];
            if (layout.RiichiDiscardIndexBytes[i] is { } ri)
                buf[ri] = riichiIndices[i];
            if (layout.MeldTileIndexArrays[i] is { } mi)
            {
                var records = melds?[i] ?? [];
                for (var k = 0; k < layout.MeldTileIndexSlots; k++)
                {
                    var idx = k < records.Length ? records[k].TileIndex : layout.MeldTileIndexEmpty;
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(mi + (k * 4), 4), idx);
                    if (layout.MeldFromDirectionBytes[i] is { } mf)
                        buf[mf + k] = (byte)(k < records.Length ? records[k].From : 0);
                }
            }
        }

        if (doraIcon is { } dora && layout.DoraIndicator is { } doraOff)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(doraOff, 4), dora);
            if (layout.DoraIndicatorCount is { } dn)
                buf[dn] = 1;
        }

        return buf;
    }

    // Hand slots from tile notation: closed tiles fill slots 0.., drawn goes to the last slot.
    public static byte[] BytesFor(
        string closed,
        string? drawn,
        byte[]? discardCounts = null,
        EmjLayout? layout = null,
        int ourMelds = 0,
        MeldRecord[]?[]? melds = null,
        byte[]? riichiIndices = null)
    {
        layout ??= Layout;
        var raw = new int[layout.HandSlots];
        var tiles = TestTiles.Parse(closed);
        for (var i = 0; i < tiles.Count && i < raw.Length - 1; i++)
            raw[i] = IconOf(tiles[i], layout.TileIconBase);
        if (drawn is not null)
            raw[^1] = IconOf(Tile.Parse(drawn), layout.TileIconBase);

        var hex = new byte[raw.Length * 4];
        for (var i = 0; i < raw.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(hex.AsSpan(i * 4, 4), raw[i]);

        var closedCounts = new byte[] { (byte)tiles.Count, 13, 13, 13 };
        var meldCounts = new byte[] { (byte)ourMelds, 0, 0, 0 };
        if (melds is not null)
            for (var i = 0; i < 4; i++)
                if (melds[i] is { } records && records.Length > 0)
                    meldCounts[i] = (byte)records.Length;
        return Bytes(layout, Convert.ToHexString(hex), discardCounts: discardCounts, closedCounts: closedCounts,
            meldCounts: meldCounts, melds: melds, riichiIndices: riichiIndices);
    }

    public static int IconOf(Tile tile, int iconBase)
    {
        if (tile.IsRedFive)
            return iconBase + tile.Suit switch { TileSuit.Man => 34, TileSuit.Pin => 35, _ => 36 };
        return iconBase + TileHelpers.ToIndex(tile);
    }

    public static DecodedStruct Decoded(
        string closed, string? drawn, byte[]? discardCounts = null, int stateCode = 6, int ourMelds = 0,
        MeldRecord[]?[]? melds = null, byte[]? riichiIndices = null)
        => StructFrame.FromBytes(BytesFor(closed, drawn, discardCounts, ourMelds: ourMelds, melds: melds, riichiIndices: riichiIndices), Layout, stateCode, 0, 50)
            .Decode(Layout);

    // Right after we pon'd shimocha's S (2z, index 28): 11 closed, the claimed tile parked in
    // slot 13, struct meld count 1 with the pon record.
    public static DecodedStruct PostPon
        => Decoded("34567m11p3459s", "2z", melds: [[new MeldRecord(28, 1)], null, null, null]);

    // ── live dumps ──

    public static string FixturesDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "resources", "fixtures")))
                dir = dir.Parent;
            return dir is null
                ? throw new DirectoryNotFoundException("resources/fixtures not found above the test output directory.")
                : Path.Combine(dir.FullName, "resources", "fixtures");
        }
    }

    public static IEnumerable<string> FixtureNames()
        => Directory.GetFiles(FixturesDirectory, "emj_struct_*.hex").Select(p => Path.GetFileNameWithoutExtension(p)).OrderBy(n => n);

    public static byte[] LoadHex(string name)
        => Convert.FromHexString(File.ReadAllText(Path.Combine(FixturesDirectory, name + ".hex")).Trim());

    public static JsonElement LoadSidecar(string name)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesDirectory, name + ".json"))).RootElement;

    public static DecodedStruct DecodeFixture(string name, out JsonElement sidecar)
    {
        sidecar = LoadSidecar(name);
        var state0 = sidecar.GetProperty("state0").GetInt32();
        return StructFrame.FromBytes(LoadHex(name), Layout, state0, 0, 50).Decode(Layout);
    }

    // Sidecar tile names: "1m".."9s", "r5s" (red), E S W N Wh G R.
    public static Tile SidecarTile(string name) => name switch
    {
        "E" => new Tile(TileSuit.Wind, 1),
        "S" => new Tile(TileSuit.Wind, 2),
        "W" => new Tile(TileSuit.Wind, 3),
        "N" => new Tile(TileSuit.Wind, 4),
        "Wh" => new Tile(TileSuit.Dragon, 1),
        "G" => new Tile(TileSuit.Dragon, 2),
        "R" => new Tile(TileSuit.Dragon, 3),
        _ when name.StartsWith('r') => Tile.Parse("0" + name[2..]),
        _ => Tile.Parse(name),
    };

    public static List<Tile> SidecarTiles(JsonElement array)
        => array.EnumerateArray().Select(e => SidecarTile(e.GetString()!)).ToList();
}
