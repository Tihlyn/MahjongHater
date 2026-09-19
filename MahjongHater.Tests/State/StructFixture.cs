using System.Buffers.Binary;
using MahjongHater.Core;
using MahjongHater.Core.State;

namespace MahjongHater.Tests.State;

// Builds AddonEmj-shaped byte buffers for the struct reader tests. The hand hex below is
// the real dump captured 2026-09-18 at +0x0DB8 (EU client): 1m 8m 3p 3p 1s 1s 5s r5s
// 8s 8s 9s S W + drawn E, followed by a pointer.
internal static class StructFixture
{
    public const string LiveHandHex =
        "092901001029010014290100142901001B2901001B2901001F2901002D29010022290100222901002329010025290100262901002429010044D9ECDC22020000";

    public const string LiveHand = "1m 8m 3p 3p 1s 1s 5s 0s 8s 8s 9s 2z 3z";

    public const string LiveDrawn = "1z";

    public static EmjLayout Layout { get; } = EmjLayout.Parse(EmjLayout.ReadEmbedded());

    public static byte[] Bytes(EmjLayout? layout = null, string? handHex = LiveHandHex, int[]? scores = null, int? doraIcon = 76054, byte[]? discardCounts = null)
    {
        layout ??= Layout;
        var buf = new byte[layout.RequiredBytes];
        if (handHex is not null)
        {
            var bytes = Convert.FromHexString(handHex);
            Array.Copy(bytes, 0, buf, layout.HandArray, Math.Min(bytes.Length, buf.Length - layout.HandArray));
        }

        scores ??= [25000, 25000, 25000, 25000];
        for (var i = 0; i < 4; i++)
            if (layout.Scores[i] is { } off)
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(off, 4), scores[i]);

        if (doraIcon is { } dora && layout.DoraIndicator is { } doraOff)
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(doraOff, 4), dora);

        if (discardCounts is not null)
            for (var i = 0; i < 4; i++)
                if (layout.DiscardCounts[i] is { } off)
                    buf[off] = discardCounts[i];

        return buf;
    }

    // Hand slots from tile notation: closed tiles fill slots 0.., drawn goes to the last slot.
    public static byte[] BytesFor(string closed, string? drawn, byte[]? discardCounts = null, EmjLayout? layout = null)
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
        return Bytes(layout, Convert.ToHexString(hex), discardCounts: discardCounts);
    }

    public static int IconOf(Tile tile, int iconBase)
    {
        if (tile.IsRedFive)
            return iconBase + tile.Suit switch { TileSuit.Man => 34, TileSuit.Pin => 35, _ => 36 };
        return iconBase + TileHelpers.ToIndex(tile);
    }

    public static DecodedStruct Decoded(string closed, string? drawn, byte[]? discardCounts = null, int stateCode = 6)
        => StructFrame.FromBytes(BytesFor(closed, drawn, discardCounts), Layout, stateCode, 0, 50).Decode(Layout);
}
