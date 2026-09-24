using MahjongHater.Core.Operate;
using Xunit;

namespace MahjongHater.Tests.Operate;

public class ResultButtonResolverTests
{
    [Fact]
    public void Ranked_result_uses_end_match_without_total_results_node_26()
    {
        // IDs here are deliberately synthetic; rank-result IDs have not been captured.
        Assert.Equal(41u, ResultButtonResolver.RankEndMatch([
            new(9, "Close", true), new(41, "End match", true)]));
    }

    [Fact]
    public void Ignores_close_and_hidden_or_disabled_end_match_buttons()
    {
        Assert.Equal(41u, ResultButtonResolver.RankEndMatch([
            new(26, "Close", true), new(40, "End match", false), new(41, " End Match ", true)]));
        Assert.Null(ResultButtonResolver.RankEndMatch([
            new(26, "Close", true), new(40, "End match", false)]));
    }

    [Fact]
    public void Does_not_guess_when_no_button_or_multiple_buttons_match()
    {
        Assert.Null(ResultButtonResolver.RankEndMatch([]));
        Assert.Null(ResultButtonResolver.RankEndMatch([new(41, "End match now", true)]));
        Assert.Null(ResultButtonResolver.RankEndMatch([
            new(41, "End match", true), new(42, "End match", true)]));
    }
}
