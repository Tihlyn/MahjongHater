using MahjongHater.Core.Operate;
using Xunit;

namespace MahjongHater.Tests.Operate;

public class IdleGuardTests
{
    private static readonly DateTime T0 = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public IdleGuard.Timers? Seen { get; set; } = new(42, 130, 42);
        public nint Window { get; set; } = 123;
        public bool ControlHeld { get; set; }
        public bool DownSucceeds { get; set; } = true;
        public bool UpSucceeds { get; set; } = true;
        public List<(nint Window, bool Down)> Sent { get; } = [];
        public List<string> Logs { get; } = [];
        public IdleGuard Guard { get; }

        public Harness() => this.Guard = new IdleGuard(this.Logs.Add, () => this.Seen,
            () => this.Window, (window, down) =>
            {
                this.Sent.Add((window, down));
                return down ? this.DownSucceeds : this.UpSucceeds;
            }, () => this.ControlHeld);

        public void Tick(double seconds = 0, bool autoPlay = true, bool inMatch = true) =>
            this.Guard.Tick(autoPlay, inMatch, T0.AddMilliseconds(Math.Round(seconds * 1000)));
    }

    [Fact]
    public void Sends_only_during_an_enabled_unattended_match()
    {
        var h = new Harness();
        h.Tick(autoPlay: false);
        h.Tick(inMatch: false);
        h.Guard.Enabled = false;
        h.Tick();
        Assert.Empty(h.Sent);
        h.Guard.Enabled = true;
        h.Tick();
        Assert.Equal([(123, true)], h.Sent);
        Assert.Equal(1, h.Guard.NudgesThisSession);
    }

    [Fact]
    public void Holds_across_frames_then_releases_the_original_window()
    {
        var h = new Harness();
        h.Tick();
        h.Window = 456;
        h.Tick(.199);
        Assert.Single(h.Sent);
        Assert.True(h.Guard.IsKeyDown);
        h.Tick(.2);
        Assert.Equal([(123, true), (123, false)], h.Sent);
        Assert.False(h.Guard.IsKeyDown);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Disabling_any_gate_releases_without_waiting_for_the_deadline(bool enabled, bool autoPlay, bool inMatch)
    {
        var h = new Harness();
        h.Tick();
        h.Guard.Enabled = enabled;
        h.Tick(.01, autoPlay, inMatch);
        Assert.Equal([(123, true), (123, false)], h.Sent);
        Assert.False(h.Guard.IsKeyDown);
        h.Tick(20, autoPlay, inMatch);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void Reset_releases_a_pending_press_and_is_idempotent()
    {
        var h = new Harness();
        h.Tick();
        h.Guard.Reset();
        h.Guard.Reset();
        Assert.Equal([(123, true), (123, false)], h.Sent);
        Assert.False(h.Guard.IsKeyDown);
    }

    [Fact]
    public void Checks_game_idle_threshold_and_preserves_negative_afk_state()
    {
        var h = new Harness { Seen = new(-1, 30, 5) };
        h.Tick();
        Assert.Empty(h.Sent);
        h.Seen = new(-1, 31, 6);
        h.Tick(10);
        Assert.Single(h.Sent);
        Assert.Equal(new IdleGuard.Timers(-1, 31, 6), h.Seen);
    }

    [Fact]
    public void Missing_or_invalid_timers_never_send_input()
    {
        foreach (var value in new IdleGuard.Timers?[] { null, new(float.NaN, 130, 42),
                     new(0, float.PositiveInfinity, 42), new(0, -1, 42), new(0, 40, -1) })
        {
            var h = new Harness { Seen = value };
            h.Tick();
            Assert.Empty(h.Sent);
            Assert.Null(h.Guard.LastTimers);
        }
    }

    [Fact]
    public void Missing_window_or_physically_held_control_defers_input()
    {
        var h = new Harness { Window = 0 };
        h.Tick();
        h.Window = 123;
        h.ControlHeld = true;
        h.Tick(10);
        Assert.Empty(h.Sent);
        h.ControlHeld = false;
        h.Tick(20);
        Assert.Single(h.Sent);
    }

    [Fact]
    public void Failed_key_down_still_gets_a_release_and_is_not_counted_as_success()
    {
        var h = new Harness { DownSucceeds = false };
        h.Tick();
        Assert.Equal([(123, true), (123, false)], h.Sent);
        Assert.Equal(0, h.Guard.NudgesThisSession);
        Assert.Null(h.Guard.LastNudgeObservedReset);
        h.Tick(1);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void Failed_release_blocks_new_presses_and_retries_without_flooding_frames()
    {
        var h = new Harness { UpSucceeds = false };
        h.Tick();
        h.Tick(.2);
        h.Tick(.3);
        Assert.Equal(2, h.Sent.Count);
        Assert.True(h.Guard.IsKeyDown);
        h.Tick(10);
        Assert.Single(h.Sent, x => x.Down);
        h.UpSucceeds = true;
        h.Tick(11, autoPlay: false);
        Assert.False(h.Guard.IsKeyDown);
        Assert.Single(h.Sent, x => x.Down);
    }

    // A held key is allowed to stand automation down - a real modifier changes what a click
    // means, and the Emj panel reads Ctrl for its point-difference toggle. An UNDELIVERABLE
    // release is not: it used to block every automated action for the rest of the session,
    // freezing a match over a keystroke. Auto play now speaks the addon's command channel,
    // which no modifier alters, so the block expires while the release keeps retrying.
    [Fact]
    public void A_stuck_release_stops_blocking_automation_but_keeps_retrying()
    {
        var h = new Harness { UpSucceeds = false };
        h.Tick();
        Assert.True(h.Guard.IsKeyDown);
        Assert.True(h.Guard.BlocksAutomation);     // genuinely held: stand aside

        h.Tick(.3);
        Assert.True(h.Guard.BlocksAutomation);     // release only just overdue

        h.Tick(6);
        Assert.True(h.Guard.IsKeyDown);            // still held, still retrying
        Assert.False(h.Guard.BlocksAutomation);    // ...but no longer freezing the match
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reports_observed_timer_response_separately_from_delivery(bool reset)
    {
        var h = new Harness();
        h.Tick();
        h.Tick(.2);
        Assert.Null(h.Guard.LastNudgeObservedReset);
        h.Seen = reset ? new(0, 1, 1) : new(43, 131, 43);
        h.Tick(1.2);
        Assert.Equal(reset, h.Guard.LastNudgeObservedReset);
        Assert.Contains(h.Logs, l => l.Contains("before [") && l.Contains("after ["));
        Assert.Equal(130f + (reset ? 0 : 1), h.Guard.HighestSeenSeconds);
        h.Tick(9.9);
        Assert.Equal(2, h.Sent.Count);
        h.Tick(10);
        Assert.Equal(reset ? 2 : 3, h.Sent.Count);
    }

    [Fact]
    public void Long_frame_gap_releases_and_observes_before_starting_another_press()
    {
        var h = new Harness();
        h.Tick();
        h.Tick(20);
        Assert.Equal([(123, true), (123, false)], h.Sent);
        Assert.False(h.Guard.IsKeyDown);
        h.Seen = new(1, 1, 1);
        h.Tick(21);
        Assert.True(h.Guard.LastNudgeObservedReset);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public void Partial_reset_does_not_claim_all_idle_counters_responded()
    {
        var h = new Harness();
        h.Tick();
        h.Tick(.2);
        h.Seen = new(0, 132, 1);
        h.Tick(1.2);
        Assert.False(h.Guard.LastNudgeObservedReset);
    }

    [Fact]
    public void Unavailable_read_after_release_is_not_reported_as_success()
    {
        var h = new Harness();
        h.Tick();
        h.Tick(.2);
        h.Seen = null;
        h.Tick(1.2);
        Assert.Null(h.Guard.LastNudgeObservedReset);
        Assert.Contains("unavailable", h.Guard.Status);
    }
}
