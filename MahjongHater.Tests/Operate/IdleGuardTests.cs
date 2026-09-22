using MahjongHater.Core.Operate;
using Xunit;

namespace MahjongHater.Tests.Operate;

// Writing the timers is one struct access; what needs covering is when the guard is allowed
// to touch them at all, since outside an unattended match the normal AFK behaviour must stand.
public class IdleGuardTests
{
    private static readonly DateTime T0 = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public IdleGuard.Timers? Seen { get; set; } = new(42f, 130f, 42f);

        public int Clears { get; private set; }

        public IdleGuard Guard { get; }

        public Harness() => this.Guard = new IdleGuard(_ => { }, () => { this.Clears++; return this.Seen; });

        public void Tick(bool autoPlay = true, bool inMatch = true, double afterSeconds = 0) =>
            this.Guard.Tick(autoPlay, inMatch, T0.AddSeconds(afterSeconds));
    }

    [Fact]
    public void Clears_the_timers_only_while_an_unattended_match_is_running()
    {
        var h = new Harness();
        h.Tick(autoPlay: false, inMatch: true);
        h.Tick(autoPlay: true, inMatch: false);
        h.Guard.Enabled = false;
        h.Tick();
        Assert.Equal(0, h.Clears);
        Assert.Equal("Off", h.Guard.Status);

        h.Guard.Enabled = true;
        h.Tick();
        Assert.Equal(1, h.Clears);
        Assert.Equal(1, h.Guard.ClearsThisSession);
    }

    [Fact]
    public void Every_tick_clears_because_the_timer_climbs_every_frame()
    {
        var h = new Harness();
        for (var i = 0; i < 5; i++)
            h.Tick(afterSeconds: i);
        Assert.Equal(5, h.Clears);
        Assert.Equal(5, h.Guard.ClearsThisSession);
    }

    [Fact]
    public void Reports_the_highest_timer_it_had_to_clear()
    {
        var h = new Harness { Seen = new IdleGuard.Timers(10f, 20f, 5f) };
        h.Tick();
        Assert.Equal(20f, h.Guard.HighestSeenSeconds);

        h.Seen = new IdleGuard.Timers(1f, 2f, 3f);       // a lower reading does not lower the mark
        h.Tick(afterSeconds: 1);
        Assert.Equal(20f, h.Guard.HighestSeenSeconds);
        Assert.Contains("20 s", h.Guard.Status);
    }

    [Fact]
    public void Without_the_timer_module_it_does_nothing()
    {
        var h = new Harness { Seen = null };
        h.Tick();
        Assert.Equal(0, h.Guard.ClearsThisSession);
        Assert.Contains("Unavailable", h.Guard.Status);
    }

    [Fact]
    public void Reset_returns_to_the_idle_status()
    {
        var h = new Harness();
        h.Tick();
        h.Guard.Reset();
        Assert.Contains("Idle", h.Guard.Status);
    }
}
