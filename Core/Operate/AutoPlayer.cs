using MahjongHater.Core.Policy;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Operate;

// Plays the match the overlay is advising on: executes every fresh policy decision once,
// advances the round recap, and — the reason it exists — notices when the game is
// waiting on us and nothing happens. A stall writes a full dump (snapshot, decision,
// prompt rows, slot clickability, the tracker's recent events) to the stall log, then a
// recovery ladder tries to get the match moving again so the session keeps producing
// data. Framework-thread only; the UI reads the public properties on the render thread
// (same thread in Dalamud).
public sealed class AutoPlayer
{
    // A decision that did not change the state is retried after this long, this many times.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(6);
    private const int MaxAttempts = 3;

    // No snapshot change for this long is a stall. The game waits for us on our turn or a
    // prompt; on others' turns three humans can legitimately think for a while, so that
    // budget is generous — but a "waiting for others" that never ends is exactly the
    // riichi symptom under investigation, hence the bound.
    private static readonly TimeSpan StallAfterOurs = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StallAfterOthers = TimeSpan.FromSeconds(45);

    // Recovery ladder, measured from the stall detection.
    private static readonly TimeSpan RecoverPassAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecoverDiscardAfter = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RecoverRecapAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecoverRepeatEvery = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RecapClickEvery = TimeSpan.FromSeconds(4);
    private const int JournalCap = 60;

    private readonly EmjStateReader reader;
    private readonly AnalysisService analysis;
    private readonly EmjActuator actuator;
    private readonly Action<string> log;
    private readonly Action<string> logWarning;
    private readonly Func<string> stallLogPath;
    private readonly Queue<string> journal = new(JournalCap);

    private string? actedFingerprint;
    private DateTime actedAtUtc;
    private int attempts;

    private string? pendingFingerprint;
    private DateTime actAfterUtc;

    // The last thing we actually delivered to the game, and the snapshot it was aimed at.
    // Dispatch is not acceptance: this is what lets the journal say "sent and ignored"
    // instead of reporting a click as a success (docs/research/STALL_2026_09_22.md).
    private sealed record PendingDispatch(string What, long Sequence, DateTime SentUtc, bool NodeActivation);

    private PendingDispatch? pending;
    private double? lastAckMs;

    private long lastSequence = -1;
    private DateTime lastChangeUtc;
    private DateTime lastRecapClickUtc;
    private string? lastRecapLine;
    private string? lastRecapReason;

    private bool stalled;
    private DateTime stallSinceUtc;
    private int recoveryStep;
    private DateTime lastRecoveryUtc;

    public AutoPlayer(EmjStateReader reader, AnalysisService analysis, EmjActuator actuator,
        Action<string> log, Action<string> logWarning, Func<string> stallLogPath)
    {
        this.reader = reader;
        this.analysis = analysis;
        this.actuator = actuator;
        this.log = log;
        this.logWarning = logWarning;
        this.stallLogPath = stallLogPath;
    }

    public bool Enabled { get; set; }

    // Opt-in: let an unacknowledged NODE activation switch the session to the hover click
    // style. Off by default - the hover cycle is the suspect behind the three
    // AgentEmj.Update crashes, and the audit wants a matched manual-input trace before it
    // comes back (docs/research/CALL_WINDOW_AUDIT_2026_09_22.md).
    public bool AllowHoverEscalation { get; set; }

    // One line for the overlay: what the player is doing right now.
    public string Status { get; private set; } = "Off";

    public bool IsStalled => this.stalled;

    public TimeSpan StalledFor => this.stalled ? DateTime.UtcNow - this.stallSinceUtc : TimeSpan.Zero;

    public int StallsThisSession { get; private set; }

    public int RecoveriesThisSession { get; private set; }

    public int DecisionsExecuted { get; private set; }

    public DateTime? LastStallUtc { get; private set; }

    // True while the Emj addon is open (the queue must not requeue on top of a match).
    public bool InMatch { get; private set; }

    public IReadOnlyList<string> Journal => this.journal.ToList();

    // Dispatch and acceptance as separate outcomes, for Diagnostics: "sent" never means
    // the game took it (docs/research/STALL_2026_09_22.md).
    public string LastDispatchStatus { get; private set; } = "nothing dispatched yet";

    public void Tick(StateSnapshot? state)
    {
        var now = DateTime.UtcNow;
        this.InMatch = this.actuator.IsAddonOpen;

        if (!this.Enabled)
        {
            this.Status = "Off";
            this.ResetProgress();
            return;
        }

        if (state is null || state.Phase == GamePhase.NotInGame || !this.InMatch)
        {
            this.Status = "Waiting for a table";
            this.ResetProgress();
            return;
        }

        if (state.Sequence != this.lastSequence)
        {
            if (this.stalled)
                this.Record($"stall ended after {(now - this.stallSinceUtc).TotalSeconds:F0} s (seq {this.lastSequence} → {state.Sequence})");
            // The game moved after our dispatch: that, and only that, is acceptance.
            if (this.pending is { } acked && acked.Sequence == this.lastSequence)
            {
                this.lastAckMs = (now - acked.SentUtc).TotalMilliseconds;
                this.LastDispatchStatus = $"{acked.What} accepted after {this.lastAckMs:F0} ms";
                this.pending = null;
            }

            this.lastSequence = state.Sequence;
            this.lastChangeUtc = now;
            this.stalled = false;
            this.recoveryStep = 0;
            if (state.Phase != GamePhase.RoundEnd)
                this.lastRecapReason = null;
        }

        if (state.Phase == GamePhase.RoundEnd)
        {
            this.pendingFingerprint = null;
            this.HandleRecap(state, now);
            return;
        }

        this.ActOnDecision(state, now);
        this.WatchForStall(state, now);
    }

    // The overlay's Enabled toggle: reset the bookkeeping so an old decision is never
    // replayed against a new state after a pause.
    public void SetEnabled(bool enabled)
    {
        if (this.Enabled == enabled)
            return;
        this.Enabled = enabled;
        this.ResetProgress();
        this.Record(enabled ? "auto play ON" : "auto play OFF");
    }

    private void ActOnDecision(StateSnapshot state, DateTime now)
    {
        var publication = this.analysis.Latest;
        if (publication?.Choice is not { } choice || publication.Status != AnalysisStatus.Ready)
        {
            this.pendingFingerprint = null;
            this.Status = this.analysis.IsStalled ? "Analysis stalled" : publication is null ? "Waiting for analysis" : $"Analysis {publication.Status}";
            return;
        }

        if (publication.Fingerprint != AnalysisService.ComputeFingerprint(state))
        {
            this.pendingFingerprint = null;
            this.Status = "Waiting for a fresh decision";
            return;
        }

        // The game only offers riichi on a tenpai hand. When it offers and we answer with a
        // plain discard, our hand read and the game disagree — twice on 2026-09-22, both times
        // with our analysis calling the hand 1-shanten. Record the hand so the disagreement can
        // be reproduced instead of guessed at (docs/research/LIVE_ISSUES_2026_09_22.md §4).
        if (state.Can(LegalAction.Riichi) && choice.Kind == ActionKind.Discard)
        {
            var melds = state.OurMelds.Count == 0 ? "closed" : $"{state.OurMelds.Count} meld(s)";
            this.logWarning($"[AutoPlay] The game offered riichi and the policy discarded: hand=[{string.Join(" ", state.Hand)}]"
                            + $" drawn={state.DrawnTile?.ToString() ?? "-"} ({melds}); decision: {choice.Summary}");
            this.Record($"riichi offered, discarded instead: {choice.Tile?.ToString() ?? "-"} — {choice.Summary}");
        }

        // The game offered a win our own read cannot explain. The offer is taken anyway
        // (DecisionPolicy), but the hand is recorded so the read can be repaired: on
        // 2026-09-22 exactly this situation was answered with Pass and the hand ran out as a
        // draw (docs/research/WIN_OFFERS_2026_09_22.md).
        if (choice.Steps.FirstOrDefault(s => s.Stage == DecisionPolicy.WinReadDisagrees) is { } disagreement)
        {
            this.logWarning($"[AutoPlay] {disagreement.Display}");
            this.Record($"win read disagrees: {choice.Kind} offered on a hand we score as no win");
        }

        // A win the game offered must never end as anything else. If the policy ever answers
        // an offered Tsumo/Ron with something else, that is the expensive failure and it says
        // so here rather than passing quietly.
        if ((state.Can(LegalAction.Ron) || state.Can(LegalAction.Tsumo))
            && choice.Kind is not (ActionKind.Ron or ActionKind.Tsumo))
        {
            this.logWarning($"[AutoPlay] The game offered {(state.Can(LegalAction.Ron) ? "Ron" : "Tsumo")} and the policy answered {choice.Kind}: "
                            + $"hand=[{string.Join(" ", state.Hand)}] melds={state.OurMelds.Count} riichi={state.OurRiichi} "
                            + $"tile={state.CallTile?.ToString() ?? state.DrawnTile?.ToString() ?? "-"}; decision: {choice.Summary}");
            this.Record($"WIN OFFER NOT TAKEN: {choice.Kind} — {choice.Summary}");
        }

        if (!IsActionable(choice, state))
        {
            this.pendingFingerprint = null;
            this.Status = choice.Kind == ActionKind.None ? $"Decision: none — {choice.Summary}"
                : state.Phase == GamePhase.OthersTurn ? "Waiting for others" : $"Decision {choice.Kind}: nothing to do";
            return;
        }

        // A window only the panel texts opened is a phantom (the texts persist after every
        // prompt); answering it would fire a list click at a closed list. A genuinely missed
        // event ends in a stall, and the recovery ladder passes it then. Our turn is still
        // real, so a riichi on such a window becomes the plain discard it carries.
        var labelOnly = this.reader.Tracker.CallWindowFromLabels;
        if (labelOnly && choice.Kind is not (ActionKind.Discard or ActionKind.Riichi))
        {
            this.pendingFingerprint = null;
            this.Status = $"Decision {choice.Kind} on a label-only window — not answering";
            return;
        }

        if (publication.Fingerprint == this.actedFingerprint)
        {
            this.pendingFingerprint = null;
            if (this.attempts >= MaxAttempts || now - this.actedAtUtc < RetryAfter)
            {
                this.Status = $"Acted {choice.Kind} {choice.Tile?.ToString() ?? string.Empty} ({this.attempts}×), waiting for the game";
                return;
            }

            this.attempts++;
            var unacknowledged = this.pending is { } sent && sent.Sequence == state.Sequence;
            this.Record($"retry {this.attempts}/{MaxAttempts}: {choice.Kind} {choice.Tile?.ToString() ?? string.Empty} — "
                        + (unacknowledged
                            ? $"'{this.pending!.What}' was dispatched {(now - this.pending.SentUtc).TotalSeconds:F0} s ago and the game has not acknowledged it"
                            : $"nothing was dispatched for {(now - this.actedAtUtc).TotalSeconds:F0} s"));
            this.MaybeEscalate(unacknowledged);
        }
        else
        {
            // Start once the recommendation is ready and actionable so it remains
            // readable. Poll the deadline without blocking the framework thread;
            // the freshness checks above cancel it if the game moves on.
            if (this.pendingFingerprint != publication.Fingerprint)
            {
                this.pendingFingerprint = publication.Fingerprint;
                this.actAfterUtc = now + TimeSpan.FromSeconds(2 + Random.Shared.NextDouble() * 2);
            }

            if (now < this.actAfterUtc)
            {
                this.Status = $"In {(this.actAfterUtc - now).TotalSeconds:F1} s: {choice.Kind} {choice.Tile?.ToString() ?? string.Empty}";
                return;
            }

            this.pendingFingerprint = null;
            this.actedFingerprint = publication.Fingerprint;
            this.attempts = 1;
            this.DecisionsExecuted++;
        }

        this.actedAtUtc = now;
        var downgraded = labelOnly && choice.Kind == ActionKind.Riichi;
        var result = downgraded ? this.actuator.Discard(choice) : this.actuator.Execute(state, choice);
        // "Sent" is the honest word: acceptance shows up later, as a snapshot change.
        this.Status = $"{(result.Ok ? "Sent" : "REFUSED")} {choice.Kind} {choice.Tile?.ToString() ?? string.Empty}";
        var line = $"seq {state.Sequence} {state.Phase} → {choice.Kind}{(downgraded ? " (label-only window: discard without riichi)" : string.Empty)} {choice.Tile?.ToString() ?? string.Empty}" +
                   (choice.Call is { } meld ? $" [{string.Join(" ", meld.Tiles)}]" : string.Empty) +
                   $" | {choice.Summary} | {(result.Ok ? "sent" : "REFUSED")}: {result.Detail}";
        this.Record(line);
        this.LastDispatchStatus = result.Ok
            ? $"{choice.Kind} {choice.Tile?.ToString() ?? string.Empty} dispatched, waiting for the game".Replace("  ", " ")
            : $"{choice.Kind} refused: {result.Detail}";
        if (result.Ok)
            this.pending = new PendingDispatch($"{choice.Kind} {choice.Tile?.ToString() ?? string.Empty}".Trim(), state.Sequence, now, result.NodeActivation);
        if (result.Detail.StartsWith("RECOVERY", StringComparison.Ordinal))
        {
            this.RecoveriesThisSession++;
            this.logWarning($"[AutoPlay] {line}");
        }
        else if (!result.Ok)
        {
            this.logWarning($"[AutoPlay] {line}");
        }
    }

    // The click STYLE is only in question when an activation we really delivered was then
    // ignored by the game. A rejected dispatch means the target was wrong (hidden, stale,
    // missing) - hover cannot help with that - and a list row never went through hover at
    // all, so neither is a reason to change how clicks are sent. Opt-in either way.
    private void MaybeEscalate(bool unacknowledged)
    {
        if (!this.AllowHoverEscalation || !unacknowledged
            || this.pending is not { NodeActivation: true }
            || EmjOperator.Style != EmjOperator.ClickStyle.Activation)
            return;

        EmjOperator.Style = EmjOperator.ClickStyle.HoverCycle;
        this.Record("click style → HoverCycle (matched MouseOver+MouseOut before the activation); reset at the next match");
        this.logWarning("[AutoPlay] An activation was dispatched and ignored; switching to the hover click style for this match.");
    }

    // Actionable = the game is waiting for this decision. A Pass outside a prompt or a
    // None decision has nothing to click; a Discard needs a discardable hand.
    private static bool IsActionable(ActionChoice choice, StateSnapshot state) => choice.Kind switch
    {
        ActionKind.Discard or ActionKind.Riichi => state.Can(LegalAction.Discard),
        ActionKind.Pass => state.Phase is GamePhase.CallPrompt or GamePhase.SelfDeclare || state.CallShapes.Count > 0,
        ActionKind.None => false,
        _ => true,
    };

    private void HandleRecap(StateSnapshot state, DateTime now)
    {
        // Say WHY we think the round is over the first time we think it. A recap that appears
        // while the match is still running is the expensive kind of wrong - it ends with a
        // duty forfeit - and "WinDeclared with the table still up" reads very differently
        // from "the game moved to its own score state".
        var because = this.reader.Tracker.WinDeclared
            ? $"a win we declared has not been settled yet (state {state.RawStateCode})"
            : $"the game is in state {state.RawStateCode}";
        if (because != this.lastRecapReason)
        {
            this.lastRecapReason = because;
            this.Record($"round recap: {because}");
            // Read the surface instead of assuming it: which result screen the game has up,
            // whether the recap control is really there and addon-bound, and what each
            // control SAYS. The labels these print are how the English string search gets
            // replaced by something structural.
            foreach (var surface in this.actuator.DescribeRecap(state.RawStateCode))
                this.Record($"  recap surface: {surface}");
        }

        this.Status = "Round recap";
        if (now - this.lastRecapClickUtc < RecapClickEvery)
            return;
        this.lastRecapClickUtc = now;
        var result = this.actuator.AdvanceRecap();
        // Score animations have nothing clickable for a while; say so once, not every 4 s.
        var line = result.Ok ? result.Detail : "nothing clickable: " + result.Detail;
        if (result.Ok || line != this.lastRecapLine)
            this.Record($"recap → {line}");
        this.lastRecapLine = line;
    }

    private void WatchForStall(StateSnapshot state, DateTime now)
    {
        var waitingOnUs = state.Phase is GamePhase.OurTurn or GamePhase.CallPrompt or GamePhase.SelfDeclare;
        var budget = waitingOnUs ? StallAfterOurs : StallAfterOthers;
        var idle = now - this.lastChangeUtc;
        if (!this.stalled)
        {
            if (idle < budget)
                return;
            this.stalled = true;
            this.stallSinceUtc = now;
            this.recoveryStep = 0;
            this.lastRecoveryUtc = now;
            this.StallsThisSession++;
            this.LastStallUtc = now;
            this.DumpStall(state, idle);
            return;
        }

        this.Status = $"STALLED {this.StalledFor.TotalSeconds:F0} s ({state.Phase})";
        var since = now - this.stallSinceUtc;
        switch (this.recoveryStep)
        {
            case 0 when since >= RecoverPassAfter:
                this.recoveryStep = 1;
                // AnswerCall refuses unless an event-confirmed window is open on a visible
                // list whose rows still carry it - the 18:27:48 recovery answered "Pass" to
                // a list the dump had already logged hidden, with a finished hand's labels.
                this.Recover("pass", this.actuator.AnswerCall("Pass", isWin: false));
                break;
            case 1 when since >= RecoverDiscardAfter:
                this.recoveryStep = 2;
                this.Recover("discard", this.RecoverDiscard(state));
                break;
            case 2 when since >= RecoverRecapAfter:
                this.recoveryStep = 3;
                this.Recover("recap", this.actuator.AdvanceRecap());
                break;
            case 3 when now - this.lastRecoveryUtc >= RecoverRepeatEvery:
                // Cycle the ladder again; a human-paced table can take a while to unfreeze.
                this.recoveryStep = 0;
                this.stallSinceUtc = now;
                break;
        }
    }

    // The draw slot first (tsumogiri is always legal on our turn), then any slot the
    // game will take a click on. A registered activation is NOT proof that a discard is
    // legal: during the 18:27 stall all 13 slots passed that check while the snapshot said
    // legal=None on another player's turn, and recovery discarded anyway.
    private OperateResult RecoverDiscard(StateSnapshot state)
    {
        if (!state.Can(LegalAction.Discard))
            return OperateResult.Fail($"no discard is legal right now (phase={state.Phase}, legal={state.Legal})");

        if (state.DrawnTile is { } drawn)
        {
            var r = this.actuator.DiscardTile(drawn);
            if (r.Ok)
                return r;
        }

        for (var i = state.Hand.Count - 1; i >= 0; i--)
        {
            var r = this.actuator.DiscardTile(state.Hand[i]);
            if (r.Ok)
                return r;
        }

        return OperateResult.Fail("no slot is discardable");
    }

    private void Recover(string step, OperateResult result)
    {
        this.lastRecoveryUtc = DateTime.UtcNow;
        var line = $"stall recovery '{step}': {(result.Ok ? "sent" : "refused")} — {result.Detail}";
        this.Record(line);
        this.logWarning($"[AutoPlay] {line}");
        if (result.Ok)
            this.RecoveriesThisSession++;
        try
        {
            File.AppendAllText(this.stallLogPath(), $"{DateTime.Now:O} {line}{Environment.NewLine}");
        }
        catch
        {
            // best effort
        }
    }

    private void DumpStall(StateSnapshot state, TimeSpan idle)
    {
        var publication = this.analysis.Latest;
        var lines = new List<string>(128)
        {
            $"=== STALL #{this.StallsThisSession} {DateTime.Now:O} — no snapshot change for {idle.TotalSeconds:F0} s ===",
            $"phase={state.Phase} stateCode={state.RawStateCode} seq={state.Sequence} legal={state.Legal} wall={state.WallRemaining} layoutHealthy={state.LayoutHealthy}",
            $"hand=[{string.Join(" ", state.Hand)}] drawn={state.DrawnTile?.ToString() ?? "-"} melds=[{string.Join(" | ", state.OurMelds.Select(m => $"{m.Type} {string.Join(" ", m.Tiles)}"))}]",
            $"ourRiichi={state.OurRiichi} riichiIndex={state.Us.RiichiDiscardIndex} winds={state.RoundWind}/{state.SeatWind} dealer={state.DealerSeat}",
            $"callWindow: options=[{string.Join(",", state.CallOptions)}] tile={state.CallTile?.ToString() ?? "-"} from={state.CallFromSeat} shapes=[{string.Join(" | ", state.CallShapes.Select(m => string.Join(" ", m.Tiles)))}]",
            $"tracker: callWindowActive={this.reader.Tracker.CallWindowActive} isClaim={this.reader.Tracker.CallIsClaim} winDeclared={this.reader.Tracker.WinDeclared} riichiDeclared={this.reader.Tracker.RiichiDeclared} roundEnded={this.reader.Tracker.RoundEnded}",
            $"seats: {string.Join(" | ", state.Seats.Select(s => $"{s.Seat}: {s.Discards.Count}/{s.DiscardCount} discards riichi={s.Riichi} melds={s.Melds.Count} score={s.Score}"))}",
            $"notes: [{string.Join("; ", state.Notes)}]",
        };

        if (publication is null)
            lines.Add("analysis: no publication");
        else
        {
            var fresh = publication.Fingerprint == AnalysisService.ComputeFingerprint(state);
            lines.Add($"analysis: status={publication.Status} fresh={fresh} computing={this.analysis.IsComputing} stalled={this.analysis.IsStalled} error={publication.Error ?? "-"} completed={publication.CompletedUtc:HH:mm:ss.fff}");
            if (publication.Choice is { } c)
            {
                lines.Add($"decision: {c.Kind} tile={c.Tile?.ToString() ?? "-"} meld={(c.Call is { } m ? string.Join(" ", m.Tiles) : "-")} | {c.Summary}");
                lines.Add($"decision hand: shanten={c.Hand?.Shanten.ToString() ?? "-"} ukeire={c.Hand?.Ukeire.ToString() ?? "-"} waits=[{string.Join(" ", c.Hand?.Waits ?? [])}]");
                foreach (var step in c.Steps)
                    lines.Add($"  [{step.Stage}] {step.Display}");
                foreach (var cand in c.Candidates.Take(6))
                    lines.Add($"  candidate {cand.Tile}: shanten={cand.ShantenAfter} ukeire={cand.Ukeire} risk={cand.DealInRisk:F2} score={cand.Score:F1} {cand.Note}");
            }
        }

        lines.Add($"acted: fingerprint={(this.actedFingerprint is null ? "-" : "set")} attempts={this.attempts} lastActedAgo={(this.actedFingerprint is null ? "-" : (DateTime.UtcNow - this.actedAtUtc).TotalSeconds.ToString("F0") + " s")}");
        lines.Add("-- prompt --");
        lines.AddRange(this.actuator.DescribePrompt());
        lines.Add("-- slots --");
        lines.AddRange(this.actuator.DescribeSlots());
        lines.Add("-- journal (auto play) --");
        lines.AddRange(this.journal);
        lines.Add("-- tracker notes (oldest first) --");
        lines.AddRange(this.reader.Tracker.RecentNotes(120));
        lines.Add(string.Empty);

        var path = this.stallLogPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllLines(path, lines);
        }
        catch (Exception ex)
        {
            this.logWarning($"[AutoPlay] stall dump failed: {ex.Message}");
        }

        var headline = $"STALL #{this.StallsThisSession}: {state.Phase} for {idle.TotalSeconds:F0} s, decision={(publication?.Choice?.Kind.ToString() ?? "none")} → {path}";
        this.Record(headline);
        this.logWarning($"[AutoPlay] {headline}");
    }

    private void ResetProgress()
    {
        this.pendingFingerprint = null;
        this.actedFingerprint = null;
        this.pending = null;
        this.lastAckMs = null;
        // A style raised for one stubborn click must not outlive the match: there was no
        // assignment back to Activation anywhere, so one escalation held until the assembly
        // was reloaded (docs/research/CALL_WINDOW_AUDIT_2026_09_22.md).
        EmjOperator.Style = EmjOperator.ClickStyle.Activation;
        this.attempts = 0;
        this.lastSequence = -1;
        this.lastChangeUtc = DateTime.UtcNow;
        this.stalled = false;
        this.recoveryStep = 0;
    }

    private void Record(string line)
    {
        if (this.journal.Count >= JournalCap)
            this.journal.Dequeue();
        this.journal.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
        this.log($"[AutoPlay] {line}");
    }
}
