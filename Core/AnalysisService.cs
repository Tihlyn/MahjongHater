using MahjongHater.Core.State;

namespace MahjongHater.Core;

public enum AnalysisStatus
{
    Ready,
    Failed,
    TimedOut,
}

// Immutable input for one analysis run, adapted from a StateSnapshot on the framework
// thread so the worker never touches reader-owned state. Policy wiring (Phase 3) will
// hand the snapshot itself to IPolicy; until then this feeds HandAnalyzer directly.
public sealed record AnalysisSnapshot(
    Tile[] ClosedTiles,
    Meld[] CalledMelds,
    Tile[] SeenTiles,
    Tile[] DoraIndicators,
    int WallRemaining,
    Wind SeatWind,
    Wind RoundWind,
    bool IsRiichi,
    RulesetOptions Ruleset,
    string Fingerprint)
{
    public static AnalysisSnapshot? From(StateSnapshot? state)
    {
        if (state is null || state.Phase == GamePhase.NotInGame || state.Hand.Count == 0)
            return null;

        return new AnalysisSnapshot(
            [.. state.Hand],
            [.. state.OurMelds],
            [.. state.SeenForAnalyzer()],
            [.. state.DoraIndicators],
            state.WallRemaining,
            state.SeatWind,
            state.RoundWind,
            state.OurRiichi,
            state.Ruleset,
            ComputeFingerprint(state));
    }

    // Order-independent hand identity: sorted closed tiles + melds + doras + seen counts.
    // Sorting kills re-analysis thrash when only tile order changes. OurRiichi is
    // included since it can flip the recommendation without any other field changing.
    public static string ComputeFingerprint(StateSnapshot state)
    {
        var closed = string.Join(",", state.Hand.OrderBy(t => t).Select(t => t.ToString()));
        var melds = string.Join("|", state.OurMelds.Select(m => string.Join(",", m.Tiles.Select(TileHelpers.ToIndex).OrderBy(i => i))));
        var doras = string.Join(",", state.DoraIndicators.Select(TileHelpers.ToIndex));

        var seenHash = 17;
        foreach (var tile in state.SeenForAnalyzer())
            seenHash = unchecked((seenHash * 31) + TileHelpers.ToIndex(tile));

        return $"{closed}:{melds}:{doras}:{seenHash:X}:{(state.OurRiichi ? 1 : 0)}";
    }
}

public sealed record AnalysisPublication(
    string Fingerprint,
    AnalysisStatus Status,
    AnalysisResult? Result,
    string? Error,
    DateTime CompletedUtc);

// Runs HandAnalyzer.Analyze off the render/framework threads with versioned dispatch,
// a watchdog timeout, and stale-result discard. No failure mode leaves the UI without
// a publication: exceptions and timeouts publish too, and any new fingerprint re-dispatches.
public sealed class AnalysisService : IDisposable
{
    // Require the hand to be identical this many consecutive ticks before analyzing,
    // so 13↔14 flicker across the draw/discard boundary doesn't trigger wasted work.
    private const int DebounceTicks = 3;

    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StalledAfter = TimeSpan.FromSeconds(3);

    private readonly HandAnalyzer analyzer;
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
    public AnalysisService(HandAnalyzer analyzer, Action<Exception, string>? logError = null)
    {
        this.analyzer = analyzer;
        this.logError = logError;
    }

    public AnalysisPublication? Latest => this.latest;

    public bool IsComputing =>
        this.dispatchedFingerprint is not null && this.dispatchedFingerprint != this.latest?.Fingerprint;

    public bool IsStalled =>
        this.IsComputing && DateTime.UtcNow - this.dispatchedAtUtc > StalledAfter;

    // Framework thread, every tick. Cheap: fingerprint compare + debounce counter.
    public void Update(StateSnapshot? state)
    {
        var snapshot = AnalysisSnapshot.From(state);
        if (snapshot is null)
            return;

        if (snapshot.Fingerprint == this.latest?.Fingerprint || snapshot.Fingerprint == this.dispatchedFingerprint)
            return;

        if (snapshot.Fingerprint != this.candidateFingerprint)
        {
            this.candidateFingerprint = snapshot.Fingerprint;
            this.stableTicks = 1;
            return;
        }

        if (++this.stableTicks < DebounceTicks)
            return;

        this.Dispatch(snapshot);
    }

    public void Dispose()
    {
        this.cts?.Cancel();
        this.cts?.Dispose();
        this.cts = null;
    }

    private void Dispatch(AnalysisSnapshot snapshot)
    {
        var myVersion = Interlocked.Increment(ref this.version);
        this.cts?.Cancel();
        this.cts?.Dispose();
        this.cts = new CancellationTokenSource(WatchdogTimeout);
        var token = this.cts.Token;

        this.dispatchedFingerprint = snapshot.Fingerprint;
        this.dispatchedAtUtc = DateTime.UtcNow;

        _ = Task.Run(() =>
        {
            AnalysisPublication publication;
            try
            {
                var result = this.analyzer.Analyze(BuildHand(snapshot), BuildContext(snapshot), token);
                publication = new AnalysisPublication(snapshot.Fingerprint, AnalysisStatus.Ready, result, null, DateTime.UtcNow);
            }
            catch (OperationCanceledException)
            {
                publication = new AnalysisPublication(snapshot.Fingerprint, AnalysisStatus.TimedOut, null, "Analysis timed out.", DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                this.logError?.Invoke(ex, "[Analysis] Hand analysis failed.");
                publication = new AnalysisPublication(snapshot.Fingerprint, AnalysisStatus.Failed, null, ex.Message, DateTime.UtcNow);
            }

            // Latest-wins: a newer dispatch owns the slot; stale results are dropped.
            if (myVersion == Volatile.Read(ref this.version))
                this.latest = publication;
        }, CancellationToken.None);
    }

    private static Hand BuildHand(AnalysisSnapshot snapshot)
    {
        var hand = new Hand
        {
            SeatWind = snapshot.SeatWind,
            RoundWind = snapshot.RoundWind,
        };
        hand.ClosedTiles.AddRange(snapshot.ClosedTiles);
        hand.CalledMelds.AddRange(snapshot.CalledMelds);
        return hand;
    }

    private static AnalysisContext BuildContext(AnalysisSnapshot snapshot)
    {
        return new AnalysisContext
        {
            SeenTiles = [.. snapshot.SeenTiles],
            DoraIndicators = [.. snapshot.DoraIndicators],
            WallRemaining = snapshot.WallRemaining,
            SeatWind = snapshot.SeatWind,
            RoundWind = snapshot.RoundWind,
            Ruleset = snapshot.Ruleset,
            IsRiichi = snapshot.IsRiichi,
        };
    }
}
