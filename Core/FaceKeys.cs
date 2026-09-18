namespace MahjongHater.Core;

// Pure decoding of tile-face render keys produced by EmjScanner.
//
// Slot components contain an inner image whose texture is a tile ICON (recorded
// 2026-07-04 round 3: hand keys "c...|i76042|..." and pile keys "i76068") — the icon id
// identifies the tile directly, no learning required. Non-icon keys (constant backdrops,
// empty pile faces, and whatever the count=109 mode renders) fall back to TileFaceMap.
public static class FaceKeys
{
    // Extracts icon fragments ("i" + digits) from a face key and decodes the tile.
    // Composite keys may carry the icon twice (normal + highlight image) — all icon
    // fragments must agree, otherwise the key is ambiguous and decoding fails.
    public static bool TryDecodeIcon(string? faceKey, out Tile tile)
    {
        tile = default;
        if (string.IsNullOrEmpty(faceKey))
            return false;

        var found = false;
        for (var i = 0; i < faceKey.Length; i++)
        {
            if (faceKey[i] != 'i')
                continue;

            // "i" must start the key or follow a fragment separator.
            if (i > 0 && faceKey[i - 1] != '|' && faceKey[i - 1] != 'c')
                continue;

            var j = i + 1;
            var iconId = 0;
            while (j < faceKey.Length && char.IsAsciiDigit(faceKey[j]))
            {
                iconId = (iconId * 10) + (faceKey[j] - '0');
                j++;
            }

            if (j == i + 1 || (j < faceKey.Length && faceKey[j] != '|'))
                continue; // not a bare icon fragment

            if (!TileHelpers.TryTileFromIconId(iconId, out var decoded))
                return false; // icon-shaped fragment outside the tile range → not a tile face

            if (found && !decoded.Equals(tile))
            {
                tile = default;
                return false; // conflicting icons within one key
            }

            tile = decoded;
            found = true;
        }

        return found;
    }
}
