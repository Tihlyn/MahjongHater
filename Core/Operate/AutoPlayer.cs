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

    private long lastSequence = -1;
    private DateTime lastChangeUtc;
    private DateTime lastRecapClickUtc;
    private string? lastRecapLine;

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
            this.lastSequence = state.Sequence;
            this.lastChangeUtc = now;
            this.stalled = false;
            this.recoveryStep = 0;
        }

        if (state.Phase == GamePhase.RoundEnd)
        {
            this.HandleRecap(now);
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
            this.Status = this.analysis.IsStalled ? "Analysis stalled" : publication is null ? "Waiting for analysis" : $"Analysis {publication.Status}";
            return;
        }

        if (publication.Fingerprint != AnalysisService.ComputeFingerprint(state))
        {
            this.Status = "Waiting for a fresh decision";
            return;
        }

        if (!IsActionable(choice, state))
        {
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
            this.Status = $"Decision {choice.Kind} on a label-only window — not answering";
            return;
        }

        if (publication.Fingerprint == this.actedFingerprint)
        {
            if (this.attempts >= MaxAttempts || now - this.actedAtUtc < RetryAfter)
            {
                this.Status = $"Acted {choice.Kind} {choice.Tile?.ToString() ?? string.Empty} ({this.attempts}×), waiting for the game";
                return;
            }

            this.attempts++;
            this.Record($"retry {this.attempts}/{MaxAttempts}: {choice.Kind} {choice.Tile?.ToString() ?? string.Empty} — state unchanged for {(now - this.actedAtUtc).TotalSeconds:F0} s");
        }
        else
        {
            this.actedFingerprint = publication.Fingerprint;
            this.attempts = 1;
            this.DecisionsExecuted++;
        }

        this.actedAtUtc = now;
        var downgraded = labelOnly && choice.Kind == ActionKind.Riichi;
        var result = downgraded ? this.actuator.Discard(choice) : this.actuator.Execute(state, choice);
        this.Status = $"{(result.Ok ? "Did" : "FAILED")} {choice.Kind} {choice.Tile?.ToString() ?? string.Empty}";
        var line = $"seq {state.Sequence} {state.Phase} → {choice.Kind}{(downgraded ? " (label-only window: discard without riichi)" : string.Empty)} {choice.Tile?.ToString() ?? string.Empty}" +
                   (choice.Call is { } meld ? $" [{string.Join(" ", meld.Tiles)}]" : string.Empty) +
                   $" | {choice.Summary} | {(result.Ok ? "ok" : "FAILED")}: {result.Detail}";
        this.Record(line);
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

    // Actionable = the game is waiting for this decision. A Pass outside a prompt or a
    // None decision has nothing to click; a Discard needs a discardable hand.
    private static bool IsActionable(ActionChoice choice, StateSnapshot state) => choice.Kind switch
    {
        ActionKind.Discard or ActionKind.Riichi => state.Can(LegalAction.Discard),
        ActionKind.Pass => state.Phase is GamePhase.CallPrompt or GamePhase.SelfDeclare || state.CallShapes.Count > 0,
        ActionKind.None => false,
        _ => true,
    };

    private void HandleRecap(DateTime now)
    {
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
                this.Recover("pass", this.actuator.ClickLabel("Pass"));
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
    // game will take a click on.
    private OperateResult RecoverDiscard(StateSnapshot state)
    {
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
        var line = $"stall recovery '{step}': {(result.Ok ? "ok" : "no effect")} — {result.Detail}";
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
        this.actedFingerprint = null;
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
