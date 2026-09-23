using MahjongHater.Core;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.State;

// Replays the exact AtkValues read off a live round recap on 2026-09-23
// (artifacts/capture/recap_state29_values.json). The layout was confirmed by decoding a valid
// winning hand out of it rather than by assuming offsets, and these tests keep it that way:
// if the decode drifts, the hand stops being a winning shape and the yaku stop matching the
// printed total.
public class RoundRecapTests
{
    // Riichi + Ura Dora, 40 Fu 2 Han, Ron. Hand 1m1m 678m 999m 456p 23s completed by 1s.
    private static AtkFrame LiveRecap()
    {
        var v = new int[112];
        v[0] = 29;
        v[1] = 26; v[2] = -26; v[3] = 0; v[4] = 0;      // per-seat deltas / 100
        v[14] = 13;                                      // tiles in the hand array
        int[] hand = [76041, 76041, 76046, 76047, 76048, 76049, 76049, 76049, 76053, 76054, 76055, 76060, 76061];
        for (var i = 0; i < hand.Length; i++)
            v[24 + i] = hand[i];
        v[41] = 76059;                                   // the winning tile, 1s
        v[42] = 2;                                       // yaku count
        v[97] = 1; v[98] = 76051;                        // dora indicator
        v[103] = 1; v[104] = 76054;                      // ura dora indicator

        return AtkFrame.OfInts(v)
            .WithString(5, "Called Ron")
            .WithString(6, "40 Fu 2 Han")
            .WithString(43, "Riichi").WithString(44, "Ura Dora")
            .WithString(61, "1 Han").WithString(62, "1 Han")
            .WithString(79, "Win after calling riichi.")
            .WithString(80, "Awards bonus han but is not a yaku by itself.");
    }

    [Fact]
    public void Reads_the_scoring_the_game_printed()
    {
        var recap = RoundRecapReader.Read(LiveRecap());
        Assert.NotNull(recap);
        Assert.Equal("Called Ron", recap!.WinMethod);
        Assert.Equal(40, recap.Fu);
        Assert.Equal(2, recap.Han);
        Assert.Equal([2600, -2600, 0, 0], recap.SeatDeltas);
    }

    [Fact]
    public void Reads_the_yaku_with_their_han_and_blurbs()
    {
        var recap = RoundRecapReader.Read(LiveRecap())!;
        Assert.Equal(2, recap.Yaku.Count);
        Assert.Equal("Riichi", recap.Yaku[0].Name);
        Assert.Equal(1, recap.Yaku[0].Han);
        Assert.StartsWith("Win after calling riichi", recap.Yaku[0].Description);
        Assert.Equal("Ura Dora", recap.Yaku[1].Name);
        Assert.Equal(1, recap.Yaku[1].Han);
    }

    // Dora are listed as named entries alongside real yaku; our detector counts them
    // separately, so the two han pools are kept apart.
    [Fact]
    public void Dora_entries_are_separated_from_real_yaku()
    {
        var recap = RoundRecapReader.Read(LiveRecap())!;
        Assert.Equal(1, recap.YakuHan);
        Assert.Equal(1, recap.DoraHan);
        Assert.Equal(["Riichi"], recap.YakuNames);
        Assert.True(recap.Yaku[1].IsDoraLike);
        Assert.False(recap.Yaku[0].IsDoraLike);
    }

    // The check that proves the offsets rather than assuming them: the tiles have to form a
    // legal winning hand, and its han has to be the han the game printed.
    [Fact]
    public void The_decoded_hand_is_a_winning_shape_worth_the_printed_han()
    {
        var recap = RoundRecapReader.Read(LiveRecap())!;
        Assert.Equal(13, recap.Hand.Count);
        Assert.Equal(Tile.Parse("1s"), recap.WinningTile);

        var hand = new Hand { IsRiichi = true, WinningTile = recap.WinningTile, WinMethod = WinMethod.Ron };
        hand.ClosedTiles.AddRange(recap.Hand);
        hand.ClosedTiles.Add(recap.WinningTile!.Value);
        var decompositions = HandDecomposer.GetWinningDecompositions(hand).ToList();
        Assert.NotEmpty(decompositions);

        var detector = new YakuDetector(RulesetOptions.Default);
        var best = decompositions.Max(d => detector.Detect(hand, d.Melds, d.Pair, d.Wait).Sum(y => y.Han));
        Assert.Equal(recap.YakuHan, best);   // Riichi = 1; the other han is the ura dora
    }

    [Fact]
    public void Dora_and_ura_indicators_are_read()
    {
        var recap = RoundRecapReader.Read(LiveRecap())!;
        Assert.Equal([Tile.Parse("2p")], recap.Dora);
        Assert.Equal([Tile.Parse("5p")], recap.UraDora);
    }

    [Fact]
    public void A_draw_carries_no_scoring_and_is_skipped()
    {
        var draw = AtkFrame.OfInts([29, 20, -10, -10, -10, .. new int[20]]).WithString(5, "Draw");
        Assert.Null(RoundRecapReader.Read(draw));
        Assert.Null(RoundRecapReader.Read(AtkFrame.OfInts([32, .. new int[20]])));
    }

    // A real draw recap, captured live on 2026-09-23. It is NOT the win layout with empty
    // fields: [42] holds a tile icon where a win puts the yaku count, the tile block starts a
    // slot later, and yaku strings are present at [43..45] even though nobody won. Reading it
    // with the win offsets would invent a hand and a yaku list out of a draw, so the decoder
    // requires the fu/han line at [6] - which a draw does not have - and refuses instead.
    [Fact]
    public void The_live_draw_layout_is_refused_rather_than_misparsed()
    {
        var v = new int[112];
        v[0] = 29;
        v[1] = -15; v[2] = 15; v[3] = 15; v[4] = -15;    // noten / tenpai / tenpai / noten
        v[24] = 13;                                       // a count, but one slot later than a win
        int[] tiles = [76043, 76043, 76044, 76044, 76045, 76075, 76047, 76048, 76049, 76058, 76058, 76058, 76062];
        for (var i = 0; i < tiles.Length; i++)
            v[25 + i] = tiles[i];
        v[42] = 76043;                                    // a TILE where a win holds the yaku count
        v[97] = 1; v[98] = 76068;
        v[103] = 1; v[104] = 76042;

        var draw = AtkFrame.OfInts(v)
            .WithString(5, "Draw")
            .WithString(43, "Pure Double Chi").WithString(44, "Riichi").WithString(45, "Aka Dora")
            .WithString(61, "1 Han").WithString(62, "1 Han").WithString(63, "1 Han");

        Assert.Null(RoundRecapReader.Read(draw));
    }
}
