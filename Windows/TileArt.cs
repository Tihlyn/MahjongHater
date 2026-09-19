using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using MahjongHater.Core;

namespace MahjongHater.Windows;

// The game's own tile faces: the same icon ids the Emj struct stores (tileIconBase +
// 34-index; red fives at +34..+36), served by Dalamud's texture provider. Textures load
// asynchronously — TryGet reports false until one is ready and the chip falls back to text.
internal static class TileArt
{
    private static ITextureProvider? provider;
    private static int iconBase;

    public static void Initialize(ITextureProvider textureProvider, int tileIconBase)
    {
        provider = textureProvider;
        iconBase = tileIconBase;
    }

    public static int IconId(Tile tile)
    {
        if (tile.IsRedFive)
        {
            var red = tile.Suit switch { TileSuit.Man => 34, TileSuit.Pin => 35, _ => 36 };
            return iconBase + red;
        }

        return iconBase + TileHelpers.ToIndex(tile);
    }

    public static bool TryGet(Tile tile, out ImTextureID handle, out float aspect)
    {
        handle = default;
        aspect = 1f;
        if (provider is null)
            return false;

        var shared = provider.GetFromGameIcon(new GameIconLookup((uint)IconId(tile)));
        if (!shared.TryGetWrap(out var wrap, out _) || wrap.Width == 0 || wrap.Height == 0)
            return false;

        handle = wrap.Handle;
        aspect = wrap.Width / (float)wrap.Height;
        return true;
    }
}
