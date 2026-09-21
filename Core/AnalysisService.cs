using MahjongHater.Core.Policy;
using MahjongHater.Core.Precomputed;
using MahjongHater.Core.State;

namespace MahjongHater.Core;

public enum AnalysisStatus
{
    Ready,
    Failed,
    TimedOut,
}

public sealed record AnalysisPublication(
    string Fingerprint,
    AnalysisStatus Status,
    ActionChoice? Choice,
    string? Error,
    DateTime CompletedUtc);

// Runs IPolicy.Choose off the render/framework threads with versioned dispatch, a
// watchdog timeout, and stale-result discard. No failure mode leaves the UI without a
// publication: exceptions and timeouts publish too, and any new fingerprint re-dispatches.
// StateSnapshot is immutable, so the worker gets the snapshot itself — no copying.
public sealed class AnalysisService : IDisposable
{
    // Require the state to be identical this many consecutive ticks before analyzing,
    // so 13↔14 flicker across the draw/discard boundary doesn't trigger wasted work.
    private const int DebounceTicks = 3;

    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StalledAfter = TimeSpan.FromSeconds(3);

    private readonly IPolicy policy;
    private readonly Action<Exception, string>? logError;

    private int version;
    private CancellationTokenSource? cts;
    private string? candidateFingerprint;
    private int stableTicks;
    private string? dispatchedFingerprint;
    private DateTime dispatchedAtUtc;
    private volatile AnalysisPublication? latest;

    // logError keeps this class free of Dalamud types (unit-testable); the plugin
    // passes IPluginLog.Error through it.
    public AnalysisService(IPolicy policy, Action<Exception, string>? logError = null)
    {
        this.policy = policy;
        this.logError = logError;
    }

    public AnalysisPublication? Latest => this.latest;

    public bool IsComputing =>
        this.dispatchedFingerprint is not null && this.dispatchedFingerprint != this.latest?.Fingerprint;

    public bool IsStalled =>
        this.IsComputing && DateTime.UtcNow - this.dispatchedAtUtc > StalledAfter;

    // States worth a decision: seated, with either tiles to look at or something legal to do.
    public static bool IsAnalyzable(StateSnapshot? state) =>
        state is not null
        && state.Phase is not (GamePhase.NotInGame or GamePhase.Dealing or GamePhase.RoundEnd)
        && (state.Hand.Count > 0 || state.Legal != LegalAction.None);

    // Everything the policy's answer depends on. Order-independent for the hand (the struct
    // hand is already sorted; opponents' discards keep their order because chronology
    // matters for safety). Phase/legal/call fields flip the recommendation without any
    // tile changing, so they are part of the identity too.
    public static string ComputeFingerprint(StateSnapshot state)
    {
        // Also track score/round/rules and discard chronology: these change utility
        // or opponent beliefs even when the hand and action flags stay identical.
        return IncrementalStateKey.Create(state).Verification;
    }

    // Framework thread, every tick. Cheap: fingerprint compare + debounce counter.
    public void Update(StateSnapshot? state)
    {
        if (!IsAnalyzable(state))
            return;

        var fingerprint = ComputeFingerprint(state!);
        if (fingerprint == this.latest?.Fingerprint || fingerprint == this.dispatchedFingerprint)
            return;

        if (fingerprint != this.candidateFingerprint)
        {
            this.candidateFingerprint = fingerprint;
            this.stableTicks = 1;
            return;
        }

        if (++this.stableTicks < DebounceTicks)
            return;

        this.Dispatch(state!, fingerprint);
    }

    public void Dispose()
    {
        this.cts?.Cancel();
        this.cts?.Dispose();
        this.cts = null;
    }

    private void Dispatch(StateSnapshot state, string fingerprint)
    {
        var myVersion = Interlocked.Increment(ref this.version);
        this.cts?.Cancel();
        this.cts?.Dispose();
        this.cts = new CancellationTokenSource(WatchdogTimeout);
        var token = this.cts.Token;

        this.dispatchedFingerprint = fingerprint;
        this.dispatchedAtUtc = DateTime.UtcNow;

        _ = Task.Run(() =>
        {
            AnalysisPublication publication;
            try
            {
                var choice = this.policy.Choose(state, token);
                publication = new AnalysisPublication(fingerprint, AnalysisStatus.Ready, choice, null, DateTime.UtcNow);
            }
            catch (OperationCanceledException)
            {
                publication = new AnalysisPublication(fingerprint, AnalysisStatus.TimedOut, null, "Analysis timed out.", DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                this.logError?.Invoke(ex, "[Analysis] Policy evaluation failed.");
                publication = new AnalysisPublication(fingerprint, AnalysisStatus.Failed, null, ex.Message, DateTime.UtcNow);
            }

            // Latest-wins: a newer dispatch owns the slot; stale results are dropped.
            if (myVersion == Volatile.Read(ref this.version))
                this.latest = publication;
        }, CancellationToken.None);
    }
}
