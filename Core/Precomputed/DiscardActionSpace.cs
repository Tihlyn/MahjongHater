using MahjongHater.Core.State;

namespace MahjongHater.Core.Precomputed;

// Deliberately narrow first action space. A legal-action flag is not enough to
// enumerate legal riichi discards, kuikae restrictions or wait-preserving kans.
// Everything outside this contract goes through the established DecisionPolicy.
public static class DiscardActionSpace
{
    public static bool Supports(StateSnapshot state) =>
        state.LayoutHealthy && state.Phase == GamePhase.OurTurn && state.Legal == LegalAction.Discard
        && !state.OurRiichi && state.OurMelds.Count == 0 && state.Hand.Count == 14
        && state.CallTile is null && state.CallShapes.Count == 0
        && state.DrawnTile is { } draw && state.Hand.Contains(draw)
        && state.Seats.Count == 4 && state.Seats.Select((s, i) => s.Seat == i && s.DiscardsVerified).All(ok => ok)
        && state.Us.Melds.Count == 0 && !state.Us.Riichi && state.WallRemaining is >= 1 and <= 70
        && state.Hand.GroupBy(TileHelpers.ToIndex).All(g => g.Count() <= 4);

    public static IReadOnlyList<Tile> Generate(StateSnapshot state) => Supports(state)
        ? state.Hand.Distinct().Order().ToArray() : [];
}
