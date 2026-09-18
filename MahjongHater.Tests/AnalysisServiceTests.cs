using MahjongHater.Core;
using Xunit;

namespace MahjongHater.Tests;

public class AnalysisServiceTests
{
    private static GameState MakeState(string closed, string discards = "")
    {
        return new GameState
        {
            InGame = true,
            ClosedTiles = TestTiles.Parse(closed),
            DiscardPile = discards.Length == 0 ? [] : TestTiles.Parse(discards),
            TilesRemainingInWall = 50,
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
        using var service = new AnalysisService(new HandAnalyzer());
        var state = MakeState("123m456m789m4467p1z");

        service.Update(state);              // tick 1 — candidate
        Assert.False(service.IsComputing);
        service.Update(state);              // tick 2 — still debouncing
        Assert.False(service.IsComputing);
        service.Update(state);              // tick 3 — dispatch

        var publication = WaitForPublication(service, TimeSpan.FromSeconds(5));
        Assert.Equal(AnalysisStatus.Ready, publication.Status);
        Assert.NotNull(publication.Result);
        Assert.Equal(0, publication.Result!.ShantenAfterDiscard);
        Assert.Equal(AnalysisSnapshot.ComputeFingerprint(state), publication.Fingerprint);
    }

    [Fact]
    public void Flickering_hand_never_dispatches()
    {
        using var service = new AnalysisService(new HandAnalyzer());
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
        Assert.Equal(AnalysisSnapshot.ComputeFingerprint(a), AnalysisSnapshot.ComputeFingerprint(b));
    }

    [Fact]
    public void Fingerprint_tracks_discard_pile()
    {
        var a = MakeState("123m456m789m4467p1z");
        var b = MakeState("123m456m789m4467p1z", "5p");
        Assert.NotEqual(AnalysisSnapshot.ComputeFingerprint(a), AnalysisSnapshot.ComputeFingerprint(b));
    }

    [Fact]
    public void Newer_hand_wins_over_stale_dispatch()
    {
        using var service = new AnalysisService(new HandAnalyzer());
        var first = MakeState("123m456m789m4467p1z");
        var second = MakeState("123m456m789m44678p");

        for (var i = 0; i < 3; i++)
            service.Update(first);
        for (var i = 0; i < 3; i++)
            service.Update(second);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var expected = AnalysisSnapshot.ComputeFingerprint(second);
        while (DateTime.UtcNow < deadline)
        {
            if (service.Latest?.Fingerprint == expected)
                return;
            Thread.Sleep(10);
        }

        Assert.Equal(expected, service.Latest?.Fingerprint); // fails with diagnostic
    }

    private sealed class HangingAnalyzer : HandAnalyzer
    {
        public override AnalysisResult Analyze(Hand hand, AnalysisContext? context = null, CancellationToken ct = default)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(20);
            }
        }
    }

    [Fact]
    public void Watchdog_publishes_timeout_instead_of_hanging()
    {
        using var service = new AnalysisService(new HangingAnalyzer());
        var state = MakeState("123m456m789m4467p1z");

        for (var i = 0; i < 3; i++)
            service.Update(state);

        var publication = WaitForPublication(service, TimeSpan.FromSeconds(10));
        Assert.Equal(AnalysisStatus.TimedOut, publication.Status);
        Assert.Null(publication.Result);
    }

    [Fact]
    public void Malformed_hand_publishes_invalid_result_instead_of_hanging()
    {
        using var service = new AnalysisService(new HandAnalyzer());
        var state = MakeState("123m45p"); // 5 tiles — malformed

        for (var i = 0; i < 3; i++)
            service.Update(state);

        var publication = WaitForPublication(service, TimeSpan.FromSeconds(5));
        Assert.Equal(AnalysisStatus.Ready, publication.Status);
        Assert.False(publication.Result!.IsValid);
    }
}
