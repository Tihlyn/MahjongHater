using MahjongHater.Core.Operate;
using Xunit;

namespace MahjongHater.Tests.Operate;

// The nudge itself is one P/Invoke; what needs covering is when it is allowed to happen,
// because a nudge at the wrong moment lands in whatever the user is doing.
public class IdleGuardTests
{
    private static readonly DateTime T0 = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public TimeSpan? Idle { get; set; } = TimeSpan.FromMinutes(10);

        public int Nudges { get; private set; }

        public int LastKey { get; private set; }

        public IdleGuard Guard { get; }

        public Harness() => this.Guard = new IdleGuard(_ => { }, () => this.Idle, key => { this.Nudges++; this.LastKey = key; return true; });

        public void Tick(bool autoPlay = true, bool inMatch = true, double afterSeconds = 0) =>
            this.Guard.Tick(autoPlay, inMatch, T0.AddSeconds(afterSeconds));
    }

    [Fact]
    public void Nudges_only_while_an_unattended_match_is_running()
    {
        var h = new Harness();
        h.Tick(autoPlay: false, inMatch: true);
        h.Tick(autoPlay: true, inMatch: false);
        h.Guard.Enabled = false;
        h.Tick();
        Assert.Equal(0, h.Nudges);

        h.Guard.Enabled = true;
        h.Tick();
        Assert.Equal(1, h.Nudges);
        Assert.Equal(1, h.Guard.NudgesThisSession);
        Assert.Equal(T0, h.Guard.LastNudgeUtc);
    }

    [Fact]
    public void Never_nudges_while_the_machine_is_in_use()
    {
        var h = new Harness { Idle = TimeSpan.FromSeconds(5) };
        h.Tick();
        Assert.Equal(0, h.Nudges);
        Assert.Contains("Armed", h.Guard.Status);

        h.Idle = h.Guard.Interval;
        h.Tick(afterSeconds: 60);
        Assert.Equal(1, h.Nudges);
    }

    [Fact]
    public void One_nudge_per_interval_at_most()
    {
        var h = new Harness();
        h.Tick();
        h.Tick(afterSeconds: 5);
        h.Tick(afterSeconds: 20);
        Assert.Equal(1, h.Nudges);
        h.Tick(afterSeconds: IdleGuard.MinimumInterval.TotalSeconds + 1);
        Assert.Equal(2, h.Nudges);
    }

    [Fact]
    public void Interval_stays_inside_the_ejection_window()
    {
        Assert.Equal(IdleGuard.MinimumInterval, IdleGuard.Clamp(TimeSpan.Zero));
        Assert.Equal(IdleGuard.MaximumInterval, IdleGuard.Clamp(TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.FromSeconds(90), IdleGuard.Clamp(TimeSpan.FromSeconds(90)));
        Assert.True(IdleGuard.MaximumInterval < TimeSpan.FromMinutes(5), "the duty ejects at about five minutes");
        Assert.Equal(IdleGuard.DefaultInterval, new IdleGuard(_ => { }).Interval);
    }

    [Fact]
    public void Presses_a_function_key_the_game_cannot_act_on()
    {
        var h = new Harness();
        h.Tick();
        Assert.Equal(IdleGuard.DefaultKey, h.LastKey);
        Assert.Equal("F19", IdleGuard.KeyName(h.LastKey));
        Assert.InRange(h.LastKey, IdleGuard.FirstKey, IdleGuard.LastKey);
        // Synthetic mouse movement does not reset this client's timer, so the nudge is a key.
        Assert.Contains("F19", h.Guard.Status);

        // Anything outside F13-F24 falls back to the default rather than pressing it.
        h.Guard.Key = 0x0D;   // Enter
        Assert.Equal(IdleGuard.DefaultKey, h.Guard.Key);
        h.Guard.Key = 0x85;   // F22
        h.Tick(afterSeconds: 300);
        Assert.Equal(0x85, h.LastKey);
        Assert.Equal("F22", IdleGuard.KeyName(h.LastKey));
    }

    [Fact]
    public void Without_a_readable_idle_time_it_does_nothing()
    {
        var h = new Harness { Idle = null };
        h.Tick();
        Assert.Equal(0, h.Nudges);
        Assert.Contains("Unavailable", h.Guard.Status);
    }

    [Fact]
    public void Reset_clears_the_cooldown_so_a_new_match_can_be_nudged()
    {
        var h = new Harness();
        h.Tick();
        h.Guard.Reset();
        Assert.Null(h.Guard.LastNudgeUtc);
        h.Tick(afterSeconds: 1);
        Assert.Equal(2, h.Nudges);
    }
}
