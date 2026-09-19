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

    public StateSnapshot Build(DecodedStruct s, EventTracker t, EmjLayout layout, RulesetOptions ruleset)
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

        var callTile = t.CallWindowActive && t.CallIsClaim ? t.CallTile : null;
        var options = t.CallWindowActive ? t.CallOptions.ToList() : [];
        var selfDeclare = t.CallWindowActive && !t.CallIsClaim;

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
        var phase = t.WinDeclared
            ? GamePhase.RoundEnd
            : ComputePhase(s.StateCode, codes, t.CallWindowActive, selfDeclare, totalClosed, hand.Count);

        var legal = LegalAction.None;
        if (t.CallWindowActive && !t.WinDeclared)
        {
            legal |= LegalAction.Pass;
            foreach (var o in options)
            {
                legal |= o switch
                {
                    "Pon" => LegalAction.Pon,
                    "Chi" => LegalAction.Chi,
                    "Kan" => selfDeclare ? LegalAction.AnKan : LegalAction.MinKan,
                    "Ron" => LegalAction.Ron,
                    "Riichi" => LegalAction.Riichi,
                    "Tsumo" => LegalAction.Tsumo,
                    _ => LegalAction.None,
                };
            }
        }

        if (phase == GamePhase.OurTurn || (selfDeclare && totalClosed == 14 && !t.WinDeclared))
            legal |= LegalAction.Discard;

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
        return new SeatState(seat, discards, melds, riichi, riichiIndex, panel.Score ?? 0)
        {
            DiscardsVerified = verified,
            DiscardCount = panel.DiscardCount ?? -1,
        };
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
          .Append('|').Append(string.Join(";", s.Notes));
        return sb.ToString();
    }
}
