namespace MahjongHater.Core.State;

// DecodedStruct + EventTracker → StateSnapshot. Pure; keeps only the previous snapshot
// so Sequence bumps exactly when the content changes.
public sealed class SnapshotBuilder
{
    private StateSnapshot previous = StateSnapshot.Empty;
    private string previousKey = string.Empty;
    private List<Tile> lastHealthyHand = [];

    public StateSnapshot Previous => this.previous;

    public StateSnapshot Build(DecodedStruct s, EventTracker t, EmjLayout layout, RulesetOptions ruleset)
    {
        var melds = t.Melds.ToList();
        var m = melds.Count;
        var closedSlots = s.ClosedTiles.ToList();
        var drawn = s.DrawnTile;

        // Slot 13 is ours only when it completes a 14-3m hand: after a call the claimed
        // tile is parked there (already part of the meld), and during a claim prompt it
        // may hold the offered tile.
        var noDraw = HandTracking.MaxClosedTiles(m) - 1;
        Tile? offeredFromSlot = null;
        var includeDraw = drawn is not null;
        if (drawn is not null && closedSlots.Count == noDraw + 1)
            includeDraw = false;                       // post-call echo of the claimed tile
        else if (drawn is not null && closedSlots.Count == noDraw && t.CallWindowActive && t.CallIsClaim)
        {
            includeDraw = false;                       // claim prompt: offered tile
            offeredFromSlot = drawn;
        }

        var hand = new List<Tile>(closedSlots);
        if (includeDraw && drawn is { } d)
            hand.Add(d);

        var healthy = s.Healthy;
        if (healthy)
            this.lastHealthyHand = hand;
        else if (this.lastHealthyHand.Count > 0)
            hand = this.lastHealthyHand;               // keep advising on the last good read

        var callTile = t.CallWindowActive && t.CallIsClaim ? t.CallTile ?? offeredFromSlot : null;
        var options = t.CallWindowActive ? t.CallOptions.ToList() : [];
        var selfDeclare = t.CallWindowActive && !t.CallIsClaim;

        var seats = new SeatState[4];
        var riichiFlags = s.RiichiFlags;
        for (var seat = 0; seat < 4; seat++)
        {
            var discards = s.SeatDiscards[seat] ?? t.SeatDiscardsOf(seat).ToList();
            var riichi = seat == 0
                ? t.RiichiDeclared || SeatRiichi(riichiFlags, 0)
                : SeatRiichi(riichiFlags, seat);
            seats[seat] = new SeatState(
                seat,
                discards,
                seat == 0 ? melds : [],
                riichi,
                riichi ? Math.Max(0, discards.Count - 1) : -1,
                s.Scores[seat] ?? 0);
        }

        var doras = new List<Tile>();
        if (s.DoraIndicator is { } dora)
        {
            doras.Add(dora);
            // Event-cached indicators extend the struct's first one (kan doras).
            if (t.EventDoras.Count > 1 && TileHelpers.SameKind(t.EventDoras[0], dora))
                doras.AddRange(t.EventDoras.Skip(1));
        }
        else
        {
            doras.AddRange(t.EventDoras);
        }

        var ura = new List<Tile>();
        if (s.UraDoraIndicator is { } u)
            ura.Add(u);

        var stateCodes = layout.StateCodes;
        var totalClosed = hand.Count + (3 * m);
        var phase = ComputePhase(s.StateCode, stateCodes, t.CallWindowActive, selfDeclare, totalClosed, hand.Count);

        var legal = LegalAction.None;
        if (t.CallWindowActive)
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

        if (phase == GamePhase.OurTurn || (selfDeclare && totalClosed == 14))
            legal |= LegalAction.Discard;

        var wall = s.WallRemaining
                   ?? (t.EventWallRemaining < 70 ? t.EventWallRemaining : (s.DiscardCounts.Any(c => c is not null) ? Math.Max(0, 70 - s.TotalDiscards) : t.EventWallRemaining));

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
            DealerSeat: s.DealerSeatRaw is { } ds && ds is >= 0 and <= 3 ? ds : 0,
            WallRemaining: wall,
            Honba: s.Honba ?? 0,
            RiichiSticks: s.RiichiSticks ?? 0,
            OurRiichi: seats[0].Riichi,
            Legal: legal,
            CallTile: callTile,
            CallFromSeat: callTile is null ? -1 : t.CallFromSeat,
            CallOptions: options,
            Ruleset: ruleset,
            LayoutHealthy: healthy);

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

    private static GamePhase ComputePhase(int code, StateCodeTable codes, bool callWindow, bool selfDeclare, int totalClosed, int handCount)
    {
        if (code == codes.Score || code == codes.Win)
            return GamePhase.RoundEnd;
        if (callWindow)
            return selfDeclare ? GamePhase.SelfDeclare : GamePhase.CallPrompt;
        if (handCount > 0 && totalClosed == 14)
            return GamePhase.OurTurn;
        if (handCount == 0)
            return code == codes.Deal ? GamePhase.Dealing : GamePhase.Unknown;
        return GamePhase.OthersTurn;
    }

    // Provisional until docs/EMJ_STRUCT.md pins the encoding: one byte per seat, non-zero = riichi.
    private static bool SeatRiichi(int? flags, int seat)
        => flags is { } f && ((f >> (8 * seat)) & 0xFF) != 0;

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
        foreach (var meld in s.OurMelds)
            sb.Append(meld.Type).Append(':').Append(string.Join(",", meld.Tiles)).Append(';');
        sb.Append('|');
        foreach (var seat in s.Seats)
            sb.Append(seat.Discards.Count).Append(':').Append(seat.Riichi ? 1 : 0).Append(':').Append(seat.Score).Append(';');
        sb.Append('|').Append(string.Join(",", s.DoraIndicators)).Append('|').Append(string.Join(",", s.UraDoraIndicators));
        sb.Append('|').Append(s.RoundWind).Append(s.SeatWind).Append(s.DealerSeat).Append('|').Append(s.WallRemaining)
          .Append('|').Append(s.Honba).Append(s.RiichiSticks).Append('|').Append(s.OurRiichi ? 1 : 0)
          .Append('|').Append((int)s.Legal).Append('|').Append(s.CallTile).Append(s.CallFromSeat)
          .Append('|').Append(string.Join(",", s.CallOptions)).Append('|').Append(s.LayoutHealthy ? 1 : 0);
        return sb.ToString();
    }
}
