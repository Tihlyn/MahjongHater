namespace MahjongHater.Core.State;

// DecodedStruct + EventTracker → StateSnapshot. The struct's counters are the authority
// (closed/meld/discard counts, riichi, scores); events fill in what the struct does not
// store (discard tiles, chi tiles, red fives in melds). Pure; keeps only the previous
// snapshot so Sequence bumps exactly when the content changes.
public sealed class SnapshotBuilder
{
    private StateSnapshot previous = StateSnapshot.Empty;
    private string previousKey = string.Empty;
    private List<Tile> lastHealthyHand = [];

    public StateSnapshot Previous => this.previous;

    public StateSnapshot Build(DecodedStruct s, EventTracker t, EmjLayout layout, RulesetOptions ruleset,
        IReadOnlyList<Tile>? discardableTiles = null)
    {
        var notes = new List<string>(4);
        var seats = new SeatState[4];
        for (var seat = 0; seat < 4; seat++)
            seats[seat] = BuildSeat(seat, s, t, notes);

        var melds = seats[0].Melds;
        var m = melds.Count;
        var closedSlots = s.ClosedTiles.ToList();
        var drawn = s.DrawnTile;

        // Slot 13 is ours only when it completes a 14-3m hand: after a call the claimed
        // tile is parked there (already part of the meld) — the struct's closed count
        // (which excludes the draw) makes that exact. A claim prompt never counts it.
        var includeDraw = drawn is not null
                          && closedSlots.Count + 1 == HandTracking.MaxClosedTiles(m)
                          && !(t.CallWindowActive && t.CallIsClaim);
        if (s.Us.ClosedTileCount is { } cc && cc != closedSlots.Count && s.StateCode != layout.StateCodes.Deal
            && s.StateCode != layout.StateCodes.Win && s.StateCode != layout.StateCodes.PostWin)
            notes.Add($"closed count {closedSlots.Count} ≠ struct {cc}");

        var hand = new List<Tile>(closedSlots);
        if (includeDraw && drawn is { } d)
            hand.Add(d);

        var healthy = s.Healthy;
        if (healthy)
            this.lastHealthyHand = hand;
        else if (this.lastHealthyHand.Count > 0)
        {
            hand = this.lastHealthyHand;               // keep advising on the last good read
            notes.Add("hand slots failed to decode — showing the last good read");
        }

        if (s.BaseShifted)
            notes.Add($"icon base shifted to {s.EffectiveIconBase}");

        // A hand only ever totals 13 (waiting) or 14 (holding a draw/claim), counting every
        // meld as three whether it is a kan or not. Anything else means the closed read and
        // the meld list disagree, and every decision built on it is guesswork - the policy
        // used to discover this silently, deep inside the win evaluation, and answer a Ron
        // window with Pass (docs/research/WIN_OFFERS_2026_09_22.md).
        var handTotal = hand.Count + (3 * melds.Count);
        if (handTotal is not (13 or 14) && s.StateCode != layout.StateCodes.Deal
            && s.StateCode != layout.StateCodes.Win && s.StateCode != layout.StateCodes.PostWin)
            notes.Add($"hand does not add up: {hand.Count} closed + {melds.Count} meld(s) = {handTotal}, expected 13 or 14");

        var callTile = t.CallWindowActive && t.CallIsClaim ? t.CallTile : null;
        var options = t.CallWindowActive ? t.CallOptions.ToList() : [];
        var selfDeclare = t.CallWindowActive && !t.CallIsClaim;

        // Work the window backwards as a CHECK, never as the source. Which of Chi/Pon/Kan a
        // tile allows is pure arithmetic over the closed hand, so the game's own offer and
        // our read must agree - and when they do not, the offer is right and the read is the
        // bug. This turns every claim window into an assertion about the hand
        // (docs/research/WIN_OFFERS_2026_09_22.md); across 2026-09-22's 242 confirmed claim
        // windows the coarse version of this check never once disagreed.
        if (t.CallWindowActive && t.CallIsClaim && callTile is { } claimed)
            notes.AddRange(ClaimMismatches(hand, melds.Count, claimed, t.CallFromSeat, options));

        var doras = new List<Tile>();
        if (s.DoraIndicator is { } dora && (s.DoraIndicatorCount ?? 1) > 0)
        {
            doras.Add(dora);
            // Event-cached indicators extend the struct's first one (kan doras).
            if (t.EventDoras.Count > 1 && TileHelpers.SameKind(t.EventDoras[0], dora))
                doras.AddRange(t.EventDoras.Skip(1));
        }
        else if (s.DoraIndicator is null)
        {
            doras.AddRange(t.EventDoras);
        }

        var ura = new List<Tile>();
        if (s.UraDoraIndicator is { } u)
            ura.Add(u);

        var codes = layout.StateCodes;
        var totalClosed = hand.Count + (3 * m);
        // The phase is what the GAME is showing. A Tsumo/Ron we answered used to force it to
        // RoundEnd, which meant sending a win made the plugin believe the round had ended
        // whether or not the game agreed - the auto player then went into its recap handling
        // against a live table. Our own answer is carried separately, as AwaitingOurWin, and
        // it suppresses decisions without relabelling what the game is doing.
        var phase = ComputePhase(s.StateCode, codes, t.CallWindowActive, selfDeclare, totalClosed, hand.Count);

        var legal = LegalAction.None;
        // An already-answered window offers nothing: re-deciding it is how one Riichi prompt
        // became three [11, 0] sends. With the calls withheld, a self-declare falls through to
        // the discard below - which is exactly what a declared riichi is waiting for.
        if (t.CallWindowActive && !t.WinAnswerPending && !t.CurrentWindowAnswered)
        {
            legal |= LegalAction.Pass;
            foreach (var o in options)
            {
                legal |= o switch
                {
                    "Pon" => LegalAction.Pon,
                    "Chi" => LegalAction.Chi,
                    // The game offers one "Kan" option code and does not say WHICH kan. On our
                    // own turn that is either a concealed kan (four in hand) or an added kan on
                    // an existing pon, and mapping it to AnKan alone made every added kan
                    // invisible: CallPolicy looks for four copies in the closed hand, finds
                    // three of them sitting in a meld, offers nothing, and the turn falls
                    // through to a discard. On 2026-09-23 that discarded the fourth 4z of our
                    // own pon. Both kinds are marked legal and CallPolicy works out which the
                    // hand actually supports.
                    "Kan" => selfDeclare ? LegalAction.AnKan | LegalAction.ShouMinKan : LegalAction.MinKan,
                    "Ron" => LegalAction.Ron,
                    "Riichi" => LegalAction.Riichi,
                    "Tsumo" => LegalAction.Tsumo,
                    _ => LegalAction.None,
                };
            }
        }

        if (phase == GamePhase.OurTurn || (selfDeclare && totalClosed == 14 && !t.WinAnswerPending))
            legal |= LegalAction.Discard;
        if (discardableTiles is { Count: 0 })
            legal &= ~LegalAction.Discard;

        var countsMapped = s.Seats.Any(x => x.DiscardCount is not null);
        var wall = s.WallRemaining
                   ?? (t.EventWallRemaining < 70 ? t.EventWallRemaining : (countsMapped ? Math.Max(0, 70 - s.TotalDiscards) : t.EventWallRemaining));

        var dealer = s.DealerSeatRaw is { } ds && ds is >= 0 and <= 3 ? ds : t.DealerSeat;
        var snapshot = new StateSnapshot(
            Sequence: this.previous.Sequence,
            Phase: phase,
            RawStateCode: s.StateCode,
            Hand: hand,
            DrawnTile: includeDraw ? drawn : null,
            OurMelds: melds,
            Seats: seats,
            DoraIndicators: doras,
            UraDoraIndicators: ura,
            RoundWind: WindFromRaw(s.RoundWindRaw) ?? t.RoundWind,
            SeatWind: WindFromRaw(s.SeatWindRaw) ?? t.SeatWind ?? Wind.East,
            DealerSeat: dealer < 0 ? 0 : dealer,
            WallRemaining: wall,
            Honba: s.Honba ?? 0,
            RiichiSticks: s.RiichiSticks ?? 0,
            OurRiichi: seats[0].Riichi,
            Legal: legal,
            CallTile: callTile,
            CallFromSeat: callTile is null ? -1 : t.CallFromSeat,
            CallOptions: options,
            Ruleset: ruleset,
            LayoutHealthy: healthy)
        {
            Notes = notes,
            HandNumber = t.HandNumber,
            CallShapes = t.CallWindowActive
                ? t.CallShapes.Select(s => new Meld(MeldType.Chi, s, true)).ToList()
                : [],
            CallWindowConfirmed = t.CallWindowActive,
            AwaitingOurWin = t.WinAnswerPending,
            AnswerPending = t.Answer is not null,
            DiscardableTiles = discardableTiles?.ToArray(),
        };

        var key = ContentKey(snapshot);
        if (key == this.previousKey)
            return this.previous;

        snapshot = snapshot with { Sequence = this.previous.Sequence + 1 };
        this.previous = snapshot;
        this.previousKey = key;
        return snapshot;
    }

    // Addon closed: publish one NotInGame snapshot (once) so consumers drop stale state.
    public StateSnapshot BuildNotInGame()
    {
        if (this.previous.Phase == GamePhase.NotInGame)
            return this.previous;
        this.previous = StateSnapshot.Empty with { Sequence = this.previous.Sequence + 1 };
        this.previousKey = ContentKey(this.previous);
        this.lastHealthyHand = [];
        return this.previous;
    }

    // Melds: the struct says how many and which tile a pon/kan is; the type-13 event
    // list says the exact tiles (chi composition, red fives). Reconcile in order.
    private static SeatState BuildSeat(int seat, DecodedStruct s, EventTracker t, List<string> notes)
    {
        var panel = s.Seats[seat];
        var eventMelds = t.SeatMeldsOf(seat);
        var melds = new List<Meld>(4);
        if (panel.MeldCount is { } count)
        {
            var chis = eventMelds.Where(x => x.IsSequence).ToList();
            var chiUsed = 0;
            for (var i = 0; i < count; i++)
            {
                var sm = i < panel.Melds.Count ? panel.Melds[i] : null;
                if (sm is null)
                {
                    if (i < eventMelds.Count)
                        melds.Add(eventMelds[i]);
                    else
                        notes.Add($"seat {seat} meld {i}: not in struct or events");
                    continue;
                }

                if (sm.IsChi)
                {
                    if (chiUsed < chis.Count)
                        melds.Add(chis[chiUsed++]);
                    else
                        notes.Add($"seat {seat} meld {i}: chi composition unknown (no type-13 seen)");
                    continue;
                }

                // Pon/kan: prefer the event's tiles (keeps red fives) when the kind matches.
                var fromEvents = eventMelds.FirstOrDefault(x => !x.IsSequence && sm.Tile is { } st && TileHelpers.SameKind(x.Tiles[0], st)
                                                                 && !melds.Contains(x));
                if (fromEvents is not null)
                    melds.Add(fromEvents);
                else if (sm.Tile is { } tile)
                    melds.Add(Meld.MakePon(tile, true));
            }
        }
        else
        {
            melds.AddRange(eventMelds);
        }

        var discards = s.SeatDiscards[seat] ?? t.SeatDiscardsOf(seat).ToList();
        var verified = panel.DiscardCount is not { } dc || discards.Count == dc;
        if (!verified)
            notes.Add($"seat {seat} discards {discards.Count}/{panel.DiscardCount} tracked");

        var riichi = panel.RiichiDiscardIndex is not null || (seat == 0 && t.RiichiDeclared);
        var riichiIndex = panel.RiichiDiscardIndex ?? (riichi ? Math.Max(0, discards.Count - 1) : -1);
        var order = t.SeatDiscardOrderOf(seat);
        return new SeatState(seat, discards, melds, riichi, riichiIndex, panel.Score ?? 0)
        {
            DiscardsVerified = verified,
            DiscardCount = panel.DiscardCount ?? -1,
            DiscardOrder = order.Count == discards.Count ? order.ToList() : [],
        };
    }

    // The disagreements worth reporting between the game's claim offer and what our closed
    // hand allows. Asymmetric on purpose:
    //   * Chi/Pon/Kan are pure tile counting, so a difference EITHER way is a read error -
    //     the game offering what we cannot derive means tiles are missing from our read, and
    //     deriving what it never offered means our read holds tiles that are not there.
    //   * Ron is only checked in the direction that cannot be explained away. The game also
    //     demands a yaku and a furiten-free wait, so our seeing a win it did not offer is
    //     ordinary; its offering a win we cannot complete is the 16:20:40 signature.
    private static IEnumerable<string> ClaimMismatches(IReadOnlyList<Tile> hand, int meldCount, Tile claimed,
        int fromSeat, IReadOnlyList<string> offered)
    {
        var inferred = HandTracking.InferClaims(hand, claimed, meldCount, allowChi: fromSeat is 3 or -1);
        foreach (var (option, flag) in new[]
                 {
                     ("Chi", ClaimOptions.Chi), ("Pon", ClaimOptions.Pon), ("Kan", ClaimOptions.Kan),
                 })
        {
            var game = offered.Contains(option);
            var ours = inferred.HasFlag(flag);
            if (game && !ours)
                yield return $"the game offers {option} on {claimed} and our hand cannot: closed read is missing tiles";
            else if (ours && !game)
                yield return $"our hand claims {option} on {claimed} and the game does not offer it: closed read has tiles the game does not";
        }

        if (offered.Contains("Ron") && !inferred.HasFlag(ClaimOptions.Ron))
            yield return $"the game offers Ron on {claimed} and our hand does not complete: the win is taken, the read is wrong";
    }

    private static GamePhase ComputePhase(int code, StateCodeTable codes, bool callWindow, bool selfDeclare, int totalClosed, int handCount)
    {
        if (code == codes.Score || code == codes.Win || code == codes.PostWin)
            return GamePhase.RoundEnd;
        if (callWindow)
            return selfDeclare ? GamePhase.SelfDeclare : GamePhase.CallPrompt;
        if (code == codes.Deal)
            return GamePhase.Dealing;
        if (handCount > 0 && totalClosed == 14)
            return GamePhase.OurTurn;
        if (handCount == 0)
            return GamePhase.Unknown;
        return GamePhase.OthersTurn;
    }

    // Provisional: accepts a wind icon id or a 0..3 index.
    private static Wind? WindFromRaw(int? raw) => raw switch
    {
        76068 or 0 => Wind.East,
        76069 or 1 => Wind.South,
        76070 or 2 => Wind.West,
        76071 or 3 => Wind.North,
        _ => null,
    };

    private static string ContentKey(StateSnapshot s)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append((int)s.Phase).Append('|').Append(s.RawStateCode).Append('|');
        foreach (var t in s.Hand)
            sb.Append(t).Append(',');
        sb.Append('|').Append(s.DrawnTile).Append('|');
        foreach (var seat in s.Seats)
        {
            sb.Append(seat.Discards.Count).Append(':').Append(seat.DiscardCount).Append(':').Append(seat.Riichi ? 1 : 0)
              .Append(':').Append(seat.Score).Append(':');
            foreach (var meld in seat.Melds)
                sb.Append(meld.Type).Append('=').Append(string.Join(",", meld.Tiles)).Append(';');
            sb.Append('/');
        }

        sb.Append('|').Append(string.Join(",", s.DoraIndicators)).Append('|').Append(string.Join(",", s.UraDoraIndicators));
        sb.Append('|').Append(s.RoundWind).Append(s.SeatWind).Append(s.DealerSeat).Append('|').Append(s.WallRemaining)
          .Append('|').Append(s.Honba).Append(s.RiichiSticks).Append('|').Append(s.OurRiichi ? 1 : 0)
          .Append('|').Append((int)s.Legal).Append('|').Append(s.CallTile).Append(s.CallFromSeat)
          .Append('|').Append(string.Join(",", s.CallOptions)).Append('|').Append(s.LayoutHealthy ? 1 : 0)
          .Append('|').Append(s.CallShapes.Count)
          .Append('|').Append(s.DiscardableTiles == null ? "unknown" : string.Join(",", s.DiscardableTiles))
          .Append('|').Append(string.Join(";", s.Notes));
        return sb.ToString();
    }
}
