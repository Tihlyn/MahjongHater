using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

internal static class PolicyInput
{
    public static Hand MakeHand(StateSnapshot state)
    {
        var hand = new Hand
        {
            SeatWind = state.SeatWind,
            RoundWind = state.RoundWind,
            IsRiichi = state.OurRiichi,
        };
        hand.ClosedTiles.AddRange(state.Hand);
        // Analyzer's riichi lock reads the final tile; snapshots may keep the hand sorted.
        if (state.DrawnTile is { } drawn && hand.ClosedTiles.Remove(drawn))
            hand.ClosedTiles.Add(drawn);
        hand.CalledMelds.AddRange(state.OurMelds);
        return hand;
    }

    public static AnalysisContext Context(StateSnapshot state) => new()
    {
        SeenTiles = state.SeenForAnalyzer().ToList(),
        DoraIndicators = state.DoraIndicators.ToList(),
        WallRemaining = state.WallRemaining,
        SeatWind = state.SeatWind,
        RoundWind = state.RoundWind,
        Ruleset = state.Ruleset,
        IsRiichi = state.OurRiichi,
    };
}
