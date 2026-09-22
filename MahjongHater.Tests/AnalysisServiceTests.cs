using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests;

public class AnalysisServiceTests
{
    // These tests are about debouncing and publication, not about how fast a machine is, so
    // they give the watchdog room; only the watchdog test itself keeps a tight budget.
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static StateSnapshot MakeState(string closed, string discards = "")
    {
        var seats = StateSnapshot.Empty.Seats.ToList();
        seats[1] = seats[1] with { Discards = discards.Length == 0 ? [] : TestTiles.Parse(discards) };
        return StateSnapshot.Empty with
        {
            Phase = GamePhase.OurTurn,
            Legal = LegalAction.Discard,
            Hand = TestTiles.Parse(closed),
            Seats = seats,
            WallRemaining = 50,
        };
    }

    private static AnalysisPublication WaitForPublication(AnalysisService service, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (service.Latest is { } publication)
                return publication;
            Thread.Sleep(10);
        }

        throw new TimeoutException("Service never published a result.");
    }

    [Fact]
    public void Publishes_after_debounce()
    {
        using var service = new AnalysisService(new DecisionPolicy(), watchdog: Generous);
        var state = MakeState("123m456m789m4467p1z");

        service.Update(state);              // tick 1 — candidate
        Assert.False(service.IsComputing);
        service.Update(state);              // tick 2 — still debouncing
        Assert.False(service.IsComputing);
        service.Update(state);              // tick 3 — dispatch

        var publication = WaitForPublication(service, Wait);
        Assert.Equal(AnalysisStatus.Ready, publication.Status);
        Assert.NotNull(publication.Choice);
        Assert.Equal(ActionKind.Discard, publication.Choice!.Kind);
        Assert.Equal(0, publication.Choice.Hand!.Shanten);
        Assert.Equal(AnalysisService.ComputeFingerprint(state), publication.Fingerprint);
    }

    [Fact]
    public void Flickering_hand_never_dispatches()
    {
        using var service = new AnalysisService(new DecisionPolicy(), watchdog: Generous);
        var a = MakeState("123m456m789m4467p1z");
        var b = MakeState("123m456m789m4467p");

        for (var i = 0; i < 30; i++)
        {
            service.Update(a);
            service.Update(b);
        }

        Assert.False(service.IsComputing);
        Assert.Null(service.Latest);
    }

    [Fact]
    public void Fingerprint_is_order_independent()
    {
        var a = MakeState("123m456m789m4467p1z");
        var b = MakeState("1z7644p987m654m321m");
        Assert.Equal(AnalysisService.ComputeFingerprint(a), AnalysisService.ComputeFingerprint(b));
    }

    [Fact]
    public void Fingerprint_tracks_discard_pile()
    {
        var a = MakeState("123m456m789m4467p1z");
        var b = MakeState("123m456m789m4467p1z", "5p");
        Assert.NotEqual(AnalysisService.ComputeFingerprint(a), AnalysisService.ComputeFingerprint(b));
    }

    [Fact]
    public void Newer_hand_wins_over_stale_dispatch()
    {
        using var service = new AnalysisService(new DecisionPolicy(), watchdog: Generous);
        var first = MakeState("123m456m789m4467p1z");
        var second = MakeState("123m456m789m44678p");

        for (var i = 0; i < 3; i++)
            service.Update(first);
        for (var i = 0; i < 3; i++)
            service.Update(second);

        var deadline = DateTime.UtcNow + Wait;
        var expected = AnalysisService.ComputeFingerprint(second);
        while (DateTime.UtcNow < deadline)
        {
            if (service.Latest?.Fingerprint == expected)
                return;
            Thread.Sleep(10);
        }

        Assert.Equal(expected, service.Latest?.Fingerprint); // fails with diagnostic
    }

    private sealed class HangingPolicy : IPolicy
    {
        public ActionChoice Choose(StateSnapshot state, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(20);
            }
        }
    }

    // A missing or non-positive budget must mean the shipped default, not "cancel at once".
    [Fact]
    public void A_non_positive_watchdog_falls_back_to_the_default()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), AnalysisService.DefaultWatchdog);
        using var service = new AnalysisService(new HangingPolicy(), watchdog: TimeSpan.Zero);
        var state = MakeState("123m456m789m4467p1z");
        for (var i = 0; i < 3; i++)
            service.Update(state);

        Thread.Sleep(500);              // well inside the two-second default
        Assert.Null(service.Latest);
    }

    [Fact]
    public void Watchdog_publishes_timeout_instead_of_hanging()
    {
        // A short budget here: the point is that a policy that never returns is cut off.
        using var service = new AnalysisService(new HangingPolicy(), watchdog: TimeSpan.FromMilliseconds(300));
        var state = MakeState("123m456m789m4467p1z");

        for (var i = 0; i < 3; i++)
            service.Update(state);

        var publication = WaitForPublication(service, Wait);
        Assert.Equal(AnalysisStatus.TimedOut, publication.Status);
        Assert.Null(publication.Choice);
    }

    [Fact]
    public void Malformed_hand_publishes_none_instead_of_hanging()
    {
        using var service = new AnalysisService(new DecisionPolicy(), watchdog: Generous);
        var state = MakeState("123m45p"); // 5 tiles — malformed

        for (var i = 0; i < 3; i++)
            service.Update(state);

        var publication = WaitForPublication(service, Wait);
        Assert.Equal(AnalysisStatus.Ready, publication.Status);
        Assert.Equal(ActionKind.None, publication.Choice!.Kind);
        Assert.Contains("out of sync", publication.Choice.Summary);
    }

    [Fact]
    public void Waiting_hand_publishes_summary_without_discard()
    {
        using var service = new AnalysisService(new DecisionPolicy(), watchdog: Generous);
        var state = MakeState("123m456m789m4467p") with { Phase = GamePhase.OthersTurn, Legal = LegalAction.None };

        for (var i = 0; i < 3; i++)
            service.Update(state);

        var publication = WaitForPublication(service, Wait);
        Assert.Equal(ActionKind.Pass, publication.Choice!.Kind);
        Assert.NotNull(publication.Choice.Hand);
        Assert.Equal(0, publication.Choice.Hand!.Shanten);
        Assert.NotEmpty(publication.Choice.Hand.Waits);
    }

    [Fact]
    public void Fingerprint_tracks_legal_actions_and_call_tile()
    {
        var a = MakeState("123m456m789m4467p");
        var b = a with { Legal = LegalAction.Pon | LegalAction.Pass, CallTile = TestTiles.Parse("4p")[0], CallFromSeat = 3 };
        Assert.NotEqual(AnalysisService.ComputeFingerprint(a), AnalysisService.ComputeFingerprint(b));
    }
}
