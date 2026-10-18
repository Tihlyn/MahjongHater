using System.Text.Json;
using MahjongHater.Core;
using MahjongHater.Core.Policy;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Policy;

// hand_rivers.jsonl: the raw rivers tools/hand_rivers.py reads back.
public class HandRiversTests
{
    [Fact]
    public void A_line_carries_every_seat_river_with_flags_order_riichi_and_melds()
    {
        var state = Seat(Snap(), 2, "1z9m0p", riichi: true, riichiIndex: 2, melds: []);
        var seats = state.Seats.ToArray();
        seats[2] = seats[2] with { DiscardTsumogiri = [false, true, false], DiscardOrder = [3, 7, 11] };
        seats[1] = seats[1] with { Discards = TestTiles.Parse("2z8s"), Melds = [Meld.MakePon(Tile.Parse("7z"), true)] };
        state = state with { Seats = seats, DoraIndicators = TestTiles.Parse("4p") };
        var result = new HandResult(new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc), "East", 2, 9, "other-ron", 0, false, 1, "human", "learned-guarded-v3");

        using var doc = JsonDocument.Parse(HandRivers.Line(state, result, 2));
        var root = doc.RootElement;
        Assert.Equal("other-ron", root.GetProperty("outcome").GetString());
        Assert.Equal("4p", root.GetProperty("dora").GetString());
        var two = root.GetProperty("seats")[2];
        Assert.Equal("1z 9m 0p", two.GetProperty("discards").GetString());
        Assert.Equal("010", two.GetProperty("tsumogiri").GetString());
        Assert.Equal(11, two.GetProperty("order")[2].GetInt32());
        Assert.Equal(2, two.GetProperty("riichi").GetInt32());
        var one = root.GetProperty("seats")[1];
        Assert.Equal(JsonValueKind.Null, one.GetProperty("tsumogiri").ValueKind);   // flags unknown
        Assert.Equal("Pon:7z7z7z", one.GetProperty("melds")[0].GetString());
    }
}
