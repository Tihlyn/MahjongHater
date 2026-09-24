using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

public sealed record CallCandidate(ActionKind Kind, Meld Meld, IReadOnlyList<Tile> Consumed,
    IReadOnlyList<Tile> Remaining, IReadOnlyList<Meld> MeldsAfter)
{
    public string Display => $"{this.Kind} [{string.Join(" ", this.Meld.Tiles)}]";
}

public sealed record CallDescription(bool Valid, IReadOnlyList<CallCandidate> Candidates, IReadOnlyList<Reason> Reasons);

// Describe what can actually be constructed before deciding whether it is worth calling.
// Legal flags are offers, not proof that the decoded hand supports the offered shape.
public static class CallDescriptor
{
    public const LegalAction Claims = LegalAction.Pon | LegalAction.Chi | LegalAction.MinKan;
    public const LegalAction OwnKans = LegalAction.AnKan | LegalAction.ShouMinKan;

    public static CallDescription Describe(StateSnapshot state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var candidates = new List<CallCandidate>();
        var reasons = new List<Reason>();
        void Note(string why) => reasons.Add(new Reason("call-shape", why));
        CallDescription Invalid(string why)
        {
            Note($"Invalid call construction: {why}");
            return new(false, [], reasons);
        }

        var claim = (state.Legal & Claims) != 0;
        var own = (state.Legal & OwnKans) != 0;
        if (!claim && !own)
            return new(true, [], []);
        Note($"Offers={state.Legal & (Claims | OwnKans)}; confirmed={state.CallWindowConfirmed}; "
            + $"tile={state.CallTile?.ToString() ?? "-"} from={state.CallFromSeat}; "
            + $"closed=[{string.Join(" ", state.Hand)}]; melds=["
            + string.Join("; ", state.OurMelds.Select(m => $"{m.Type} {string.Join(" ", m.Tiles)}")) + "].");
        if (!state.LayoutHealthy || !state.Us.MeldsVerified)
            return Invalid("hand layout or meld composition is unverified.");
        if (claim && own)
            return Invalid("opponent claims and own-turn kans are offered together.");
        if (state.OurMelds.Count > 4 || state.OurMelds.Any(m => !ValidMeld(m)))
            return Invalid("existing meld has an invalid shape, length or open/closed status.");
        if (state.Us.MeldCount >= 0 && state.Us.MeldCount != state.OurMelds.Count)
            return Invalid("declared meld count disagrees with reconstructed melds.");
        var expected = claim ? 13 : 14;
        var total = state.Hand.Count + 3 * state.OurMelds.Count;
        if (total != expected)
            return Invalid($"{state.Hand.Count} closed + 3*{state.OurMelds.Count} melds = {total}; expected {expected}.");
        if (claim && (state.CallTile is null || state.CallFromSeat is < 1 or > 3))
            return Invalid("a claim needs an offered tile from an opponent.");
        if (own && state.CallTile is not null)
            return Invalid("an own-turn kan cannot claim an opponent's tile.");
        if (own && state.DrawnTile is { } drawn && !state.Hand.Contains(drawn))
            return Invalid("the drawn tile is missing from the closed hand.");
        var physical = state.Hand.Concat(state.OurMelds.SelectMany(m => m.Tiles));
        if (claim) physical = physical.Append(state.CallTile!.Value);
        if (physical.Any(t => t.Number < 1 || t.Number > (t.Suit == TileSuit.Wind ? 4 : t.Suit == TileSuit.Dragon ? 3 : 9))
            || physical.GroupBy(TileHelpers.ToIndex).Any(g => g.Count() > 4))
            return Invalid("invalid tile or more than four physical copies of one kind (including the offered tile).");

        void Add(ActionKind kind, MeldType type, Tile[] consumed, Tile? called = null, int replace = -1)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = state.Hand.ToList();
            if (consumed.Any(t => !remaining.Remove(t)))
            {
                Note($"{kind}: consumed physical copies are missing from the hand.");
                return;
            }
            var tiles = replace >= 0 ? state.OurMelds[replace].Tiles.Concat(consumed).ToArray()
                : called is { } tile ? consumed.Append(tile).ToArray() : consumed;
            var meld = new Meld(type, tiles, type != MeldType.Ankan);
            if (!ValidMeld(meld))
            {
                Note($"{kind}: resulting meld is not a valid shape.");
                return;
            }
            if (kind == ActionKind.Chi && state.CallShapes.Count > 0
                && !state.CallShapes.Any(m => m.Type == MeldType.Chi && ValidSequence(m.Tiles)
                    && m.Tiles.Select(TileHelpers.ToIndex).Order().SequenceEqual(meld.Tiles.Select(TileHelpers.ToIndex).Order())))
                return;
            var melds = state.OurMelds.ToList();
            if (replace >= 0) melds[replace] = meld;
            else melds.Add(meld);
            var afterTotal = remaining.Count + 3 * melds.Count;
            if (melds.Count > 4 || afterTotal != (meld.IsKan ? 13 : 14))
            {
                Note($"{kind}: resulting hand has invalid tile/meld counts ({remaining.Count} closed, {melds.Count} melds).");
                return;
            }
            var candidate = new CallCandidate(kind, meld, consumed, remaining, melds);
            candidates.Add(candidate);
            Note($"Validated {candidate.Display}: consume [{string.Join(" ", consumed)}]; "
                + $"closed {state.Hand.Count}->{remaining.Count}, melds {state.OurMelds.Count}->{melds.Count}, "
                + $"hand total {total}->{afterTotal}{(meld.IsKan ? " before replacement draw" : " before discard")}.");
        }

        if (claim && !state.OurRiichi && state.CallTile is { } offered)
        {
            var copies = state.Hand.Where(t => TileHelpers.SameKind(t, offered)).OrderBy(t => t.IsRedFive).ToArray();
            if (state.Can(LegalAction.Pon) && copies.Length >= 2)
                Add(ActionKind.Pon, MeldType.Pon, copies.Take(2).ToArray(), offered);
            // Opening a closed hand with kan is a strategic choice, not a shape failure.
            if (state.Can(LegalAction.MinKan) && copies.Length >= 3)
                Add(ActionKind.MinKan, MeldType.Daiminkan, copies.Take(3).ToArray(), offered);
            if (state.Can(LegalAction.Chi) && state.CallFromSeat == 3 && !offered.IsHonor)
                for (var start = Math.Max(1, offered.Number - 2); start <= Math.Min(7, offered.Number); start++)
                {
                    var consumed = new List<Tile>();
                    foreach (var number in Enumerable.Range(start, 3).Where(n => n != offered.Number))
                    {
                        var matches = state.Hand.Where(t => t.Suit == offered.Suit && t.Number == number).OrderBy(t => t.IsRedFive).ToArray();
                        if (matches.Length > 0) consumed.Add(matches[0]);
                    }
                    if (consumed.Count == 2) Add(ActionKind.Chi, MeldType.Chi, consumed.ToArray(), offered);
                }
        }
        if (own && state.Can(LegalAction.AnKan))
            foreach (var group in state.Hand.GroupBy(TileHelpers.ToIndex).Where(g => g.Count() == 4))
            {
                if (state.OurRiichi && (state.DrawnTile is not { } draw || !TileHelpers.SameKind(group.First(), draw)))
                    continue;
                Add(ActionKind.AnKan, MeldType.Ankan, group.ToArray());
            }
        if (own && state.Can(LegalAction.ShouMinKan) && !state.OurRiichi)
            for (var i = 0; i < state.OurMelds.Count; i++)
            {
                var pon = state.OurMelds[i];
                if (pon.Type != MeldType.Pon || !pon.IsOpen) continue;
                foreach (var tile in state.Hand.Where(t => TileHelpers.SameKind(t, pon.Tiles[0])).Distinct())
                    Add(ActionKind.ShouMinKan, MeldType.Shouminkan, [tile], replace: i);
            }

        foreach (var (flag, kind) in new[] { (LegalAction.Pon, ActionKind.Pon), (LegalAction.Chi, ActionKind.Chi), (LegalAction.MinKan, ActionKind.MinKan) })
            if (state.Can(flag) && candidates.All(c => c.Kind != kind))
                Note($"Offered {kind} has no constructible candidate: check copies, source seat, riichi and chooser shapes.");
        // The game's single Kan label maps to both flags; one valid kind is sufficient.
        if (own && candidates.Count == 0)
            Note("Offered own-turn Kan has no constructible candidate: need four closed copies or an open pon plus its fourth tile; riichi permits only the drawn kind.");
        ct.ThrowIfCancellationRequested();
        return new(true, candidates, reasons);
    }

    private static bool ValidMeld(Meld meld) => meld.Type switch
    {
        MeldType.Chi => meld.IsOpen && ValidSequence(meld.Tiles),
        MeldType.Pon => meld.IsOpen && SameCopies(meld.Tiles, 3),
        MeldType.Ankan => !meld.IsOpen && SameCopies(meld.Tiles, 4),
        MeldType.Daiminkan or MeldType.Shouminkan => meld.IsOpen && SameCopies(meld.Tiles, 4),
        _ => false,
    };

    private static bool SameCopies(Tile[] tiles, int count) => tiles.Length == count && tiles.All(t => TileHelpers.SameKind(t, tiles[0]));

    private static bool ValidSequence(Tile[] tiles)
    {
        var sorted = tiles.Order().ToArray();
        return sorted.Length == 3 && sorted.All(t => !t.IsHonor && t.Suit == sorted[0].Suit)
            && sorted[0].Number + 1 == sorted[1].Number && sorted[1].Number + 1 == sorted[2].Number;
    }
}
