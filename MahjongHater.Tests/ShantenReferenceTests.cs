using MahjongHater.Core;
using Xunit;

namespace MahjongHater.Tests;

// Curated ground-truth shanten values. Both the production implementation and the
// frozen naive oracle must agree with this table — the table wins any dispute.
public class ShantenReferenceTests
{
    public static TheoryData<string, int, int> Cases => new()
    {
        // notation, calledMeldCount, expected shanten
        // ── closed 13-tile hands ──
        { "123m456p789s1122z", 0, 0 },   // three runs + two pairs → shanpon tenpai
        { "123m456m789m123p1s", 0, 0 },  // four runs + tanki wait
        { "123m456p789s1234z", 0, 2 },   // three runs + four isolated honors
        { "19m19p19s1234567z", 0, 0 },   // kokushi 13-sided wait
        { "119m19p19s123456z", 0, 0 },   // kokushi with pair, waiting 7z
        { "1122334455667m", 0, 0 },      // chiitoi tenpai (also standard tenpai)
        { "147m258p369s1234z", 0, 6 },   // fully disconnected → chiitoi path gives 6
        { "123m456p13s79s112z", 0, 1 },  // two runs + two kanchan + pair + floater
        // ── 14-tile hands ──
        { "123m456p789s11122z", 0, -1 }, // complete: four sets + pair
        { "1111m2345678p999s", 0, 0 },   // quad usable as triplet + floater; tenpai
        { "11112233445566m", 0, -1 },    // complete: 11m pair + 123 123 456 456
        { "11112233445577m", 0, 0 },     // 77m head + 111 123 234 + 45m → tenpai
        { "11112233445566z", 0, 1 },     // chiitoi path; the 1z quad is ONE pair, not two
        // ── open hands (chiitoi/kokushi must be skipped) ──
        { "11m", 4, -1 },                // four called melds + pair = win
        { "123m456p78s11z", 1, 0 },      // two closed runs + ryanmen + pair
        { "123m456p7891s", 1, 0 },       // three closed runs + tanki
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Production_matches_reference(string notation, int calledMelds, int expected)
    {
        var tiles = TestTiles.Parse(notation);
        Assert.Equal(expected, Shanten.Calculate(tiles, calledMelds));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Oracle_matches_reference(string notation, int calledMelds, int expected)
    {
        var tiles = TestTiles.Parse(notation);
        Assert.Equal(expected, NaiveShantenOracle.Calculate(tiles, calledMelds));
    }

    [Fact]
    public void Red_fives_are_normalized_before_counting()
    {
        var plain = TestTiles.Parse("456m456p456s55m11z");
        var red = TestTiles.Parse("406m406p406s05m11z");
        Assert.Equal(Shanten.Calculate(plain), Shanten.Calculate(red));
    }
}
