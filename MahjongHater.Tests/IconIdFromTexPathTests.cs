using MahjongHater.Core;
using Xunit;

namespace MahjongHater.Tests;

// The IconId field on a pooled node's texture can go stale (drawn-tile slot kept the
// previous draw's id while displaying the new tile); the tex file path is what is
// actually rendered, so it must parse reliably — and never fire on non-icon textures.
public class IconIdFromTexPathTests
{
    [Theory]
    [InlineData("ui/icon/076000/076050_hr1.tex", 76050u)]
    [InlineData("ui/icon/076000/076071.tex", 76071u)]
    [InlineData("ui\\icon\\076000\\076060_hr1.tex", 76060u)]
    public void Parses_id_named_files_under_icon_directories(string path, uint expected)
        => Assert.Equal(expected, EmjScanner.IconIdFromTexPath(path));

    [Theory]
    [InlineData("ui/uld/EmjTile_hr1.tex")]      // tile atlas — not an icon
    [InlineData("ui/icon/076000/icon.tex")]     // no leading digits
    [InlineData("chara/076050_hr1.tex")]        // digits but not an icon directory
    [InlineData("ui/icon/076000/076050x.tex")]  // digits not terminated by '.' or '_'
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_everything_else(string? path)
        => Assert.Equal(0u, EmjScanner.IconIdFromTexPath(path));
}
