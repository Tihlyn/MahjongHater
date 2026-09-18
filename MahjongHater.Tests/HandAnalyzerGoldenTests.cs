using MahjongHater.Core;
using Xunit;

namespace MahjongHater.Tests;

public class HandAnalyzerGoldenTests
{
    private static Hand MakeHand(string closed, params Meld[] melds)
    {
        var hand = new Hand();
        hand.ClosedTiles.AddRange(TestTiles.Parse(closed));
        hand.CalledMelds.AddRange(melds);
        return hand;
    }

    private static Meld OpenPon(string tile)
    {
        var t = Tile.Parse(tile);
        return new Meld(MeldType.Pon, [t, t, t], true);
    }

    [Fact]
    public void Recommends_the_tenpai_discard()
    {
        // 123m 456m 789m + 4467p + 1z: only discarding 1z reaches tenpai (waiting 5p/8p).
        var result = new HandAnalyzer().Analyze(MakeHand("123m456m789m4467p1z"));

        Assert.True(result.IsValid);
        Assert.NotNull(result.BestDiscard);
        Assert.True(TileHelpers.SameKind(result.BestDiscard.Value, Tile.Parse("1z")));
        Assert.Equal(0, result.ShantenAfterDiscard);
        Assert.Equal([Tile.Parse("5p"), Tile.Parse("8p")], result.TenpaiWaits);
        Assert.Equal(8, result.Ukeire);
        Assert.True(result.RiichiRecommended);
    }

    [Fact]
    public void Ukeire_subtracts_seen_tiles()
    {
        var ctx = new AnalysisContext { SeenTiles = TestTiles.Parse("888p") };
        var result = new HandAnalyzer().Analyze(MakeHand("123m456m789m4467p1z"), ctx);

        Assert.Equal(5, result.Ukeire); // 4× 5p + 1× 8p
    }

    [Fact]
    public void Ukeire_subtracts_dora_indicators()
    {
        // The 5p indicator is itself a visible 5p copy → 3× 5p + 4× 8p.
        var ctx = new AnalysisContext { DoraIndicators = [Tile.Parse("5p")] };
        var result = new HandAnalyzer().Analyze(MakeHand("123m456m789m4467p1z"), ctx);

        Assert.Equal(7, result.Ukeire);
        // 6p (the actual dora) is kept in hand → value estimate reflects it.
        Assert.True(result.Ranked[0].ValueEstimate >= 1);
    }

    [Fact]
    public void Keeps_the_red_five_when_discarding_that_kind()
    {
        // 555m (one red) + three triplets + 11z. The 5m-discard option must throw the
        // PLAIN copy, keeping the red five in hand.
        var result = new HandAnalyzer().Analyze(MakeHand("055m111p222s333s11z"));

        var fiveOption = result.Ranked.First(o => TileHelpers.SameKind(o.Eval.Discard, Tile.Parse("5m")));
        Assert.False(fiveOption.DiscardTile.IsRedFive);

        // Value-aware bonus: the analyzer prefers the 1z tanki — every triplet stays
        // concealed, so the wait is a suuankou (yakuman) wait despite lower raw ukeire.
        Assert.True(TileHelpers.SameKind(result.BestDiscard!.Value, Tile.Parse("1z")));
    }

    [Fact]
    public void Flags_open_yakuless_tenpai()
    {
        // Open pon 2p + 123m 789m 55s 67s: tenpai on 5s/8s but no wait yields any yaku.
        var result = new HandAnalyzer().Analyze(
            MakeHand("123m789m55s67s2z", OpenPon("2p")));

        Assert.True(TileHelpers.SameKind(result.BestDiscard!.Value, Tile.Parse("2z")));
        Assert.Equal(0, result.ShantenAfterDiscard);
        Assert.True(result.Ranked[0].OpenYakuRisk);
        Assert.Contains("yaku", result.Reasoning, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.RiichiRecommended); // open hand
    }

    [Fact]
    public void Open_yakuhai_tenpai_is_not_flagged()
    {
        // Open pon of Haku is always at least 1 han.
        var result = new HandAnalyzer().Analyze(
            MakeHand("234m456p55s67s2z", OpenPon("5z")));

        Assert.Equal(0, result.ShantenAfterDiscard);
        Assert.False(result.Ranked[0].OpenYakuRisk);
        Assert.True(result.Ranked[0].ValueEstimate >= 1);
    }

    [Fact]
    public void Thirteen_tile_hand_reports_waits_without_discard()
    {
        // Three runs + 44p pair + two disconnected terminals → 1-shanten.
        var result = new HandAnalyzer().Analyze(MakeHand("123m456m789m44p19s"));

        Assert.True(result.IsValid);
        Assert.Null(result.BestDiscard);
        Assert.Equal(1, result.ShantenAfterDiscard);
    }

    [Fact]
    public void Thirteen_tile_tenpai_reports_waits()
    {
        var result = new HandAnalyzer().Analyze(MakeHand("123m456m789m44p67p"));

        Assert.Null(result.BestDiscard);
        Assert.Equal(0, result.ShantenAfterDiscard);
        Assert.Equal([Tile.Parse("5p"), Tile.Parse("8p")], result.TenpaiWaits);
        Assert.Equal(8, result.Ukeire);
        Assert.True(result.WinProbability > 0);
    }

    [Fact]
    public void Malformed_hand_returns_invalid_result()
    {
        var result = new HandAnalyzer().Analyze(MakeHand("123m45p"));

        Assert.False(result.IsValid);
        Assert.Null(result.BestDiscard);
    }

    [Fact]
    public void Ukeire2_breaks_ties_at_one_shanten()
    {
        var result = new HandAnalyzer().Analyze(MakeHand("123m456m4578p447s1z"));

        Assert.True(result.IsValid);
        Assert.True(result.ShantenAfterDiscard >= 1);
        var finalists = result.Ranked.Where(o => o.Eval.ShantenAfter == result.ShantenAfterDiscard).ToList();
        Assert.All(finalists.Where(f => f.Eval.ShantenAfter >= 1), f => Assert.True(f.Eval.Ukeire2 > 0));
    }

    [Fact]
    public void Analyze_is_cancellable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => new HandAnalyzer().Analyze(MakeHand("123m456m789m4467p1z"), null, cts.Token));
    }
}

public class YakuDetectorSpotTests
{
    private static Hand WinningHand(string closed, string winningTile, bool tsumo = false)
    {
        var hand = new Hand
        {
            WinningTile = Tile.Parse(winningTile),
            WinMethod = tsumo ? WinMethod.Tsumo : WinMethod.Ron,
        };
        hand.ClosedTiles.AddRange(TestTiles.Parse(closed));
        return hand;
    }

    private static List<YakuResult> Detect(Hand hand)
    {
        var decomposition = HandDecomposer.GetBestDecomposition(hand);
        Assert.NotNull(decomposition);
        return new YakuDetector(RulesetOptions.Default)
            .Detect(hand, decomposition.Melds, decomposition.Pair, decomposition.Wait);
    }

    [Fact]
    public void Detects_tanyao()
    {
        var yaku = Detect(WinningHand("234m567m234p345s55s", "5s"));
        Assert.Contains(yaku, y => y.Name == "Tanyao");
    }

    [Fact]
    public void Detects_yakuhai_dragon()
    {
        var yaku = Detect(WinningHand("123m456p789s555z55s", "5s"));
        Assert.Contains(yaku, y => y.Name.StartsWith("Yakuhai"));
    }

    [Fact]
    public void Detects_chiitoitsu()
    {
        var yaku = Detect(WinningHand("1122m3344p5566s77z", "7z"));
        Assert.Contains(yaku, y => y.Name == "Chiitoitsu");
    }

    [Fact]
    public void Detects_pinfu()
    {
        // All sequences, non-yakuhai pair, ryanmen wait on 4s.
        var yaku = Detect(WinningHand("234m456p55p234s678s", "4s"));
        Assert.Contains(yaku, y => y.Name == "Pinfu");
    }

    [Fact]
    public void Decomposer_returns_null_for_incomplete_hand()
    {
        var hand = WinningHand("234m567m234p345s5s9s", "9s");
        Assert.Null(HandDecomposer.GetBestDecomposition(hand));
    }
}
