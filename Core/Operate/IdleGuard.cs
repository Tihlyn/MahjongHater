using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace MahjongHater.Core.Operate;

// Read counters, then deliver a short Control press to this process's game window.
// Never write counters: handling input may do more than resetting three floats.
// See docs/research/STALL_2026_09_22.md for the upstream AntiAfkKick comparison.
public sealed class IdleGuard
{
    public readonly record struct Timers(float Afk, float ContentInput, float Input)
    {
        public float Highest => Math.Max(this.Afk, Math.Max(this.ContentInput, this.Input));
        public bool IsValid => float.IsFinite(this.Afk) && float.IsFinite(this.ContentInput)
                               && float.IsFinite(this.Input) && this.ContentInput >= 0 && this.Input >= 0;

        // Afk may be negative while AFK. Only counters above the trigger need to fall.
        public bool DroppedSince(Timers before) =>
            (before.Afk <= IdleThresholdSeconds || this.Afk < before.Afk)
            && (before.ContentInput <= IdleThresholdSeconds || this.ContentInput < before.ContentInput)
            && (before.Input <= IdleThresholdSeconds || this.Input < before.Input);

        public override string ToString() => $"afk={this.Afk:F1}s content={this.ContentInput:F1}s input={this.Input:F1}s";
    }

    public const float IdleThresholdSeconds = 30;
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HoldFor = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ObserveAfter = TimeSpan.FromSeconds(1);
    private readonly Action<string> log;
    private readonly Func<Timers?> read;
    private readonly Func<nint> findWindow;
    private readonly Func<nint, bool, bool> sendKey;
    private readonly Func<bool> controlHeld;
    private DateTime nextCheckUtc;
    private DateTime releaseAtUtc;
    private DateTime retryReleaseAtUtc;
    private DateTime observeAtUtc;
    private nint pressedWindow;
    private Timers? beforePress;

    // Platform seams keep tests independent of native pointers and OS input.
    public IdleGuard(Action<string> log, Func<Timers?>? read = null,
        Func<nint>? findWindow = null, Func<nint, bool, bool>? sendKey = null,
        Func<bool>? controlHeld = null)
    {
        this.log = log;
        this.read = read ?? ReadGameTimers;
        this.findWindow = findWindow ?? FindGameWindow;
        this.sendKey = sendKey ?? SendControl;
        this.controlHeld = controlHeld ?? (() => (GetAsyncKeyState(0x11) & 0x8000) != 0);
    }

    public bool Enabled { get; set; } = true;
    public int NudgesThisSession { get; private set; }
    public float HighestSeenSeconds { get; private set; }
    public Timers? LastTimers { get; private set; }
    public bool? LastNudgeObservedReset { get; private set; }
    public bool IsKeyDown => this.pressedWindow != 0;
    public string Status { get; private set; } = "Off";

    // Framework thread. Call even when disabled so a pending release can finish.
    public void Tick(bool autoPlayEnabled, bool inMatch, DateTime nowUtc)
    {
        var active = this.Enabled && autoPlayEnabled && inMatch;
        if (this.IsKeyDown)
        {
            if (nowUtc < this.retryReleaseAtUtc)
                return;
            if (active && nowUtc < this.releaseAtUtc)
                return;
            if (!this.Release())
            {
                this.retryReleaseAtUtc = nowUtc + ObserveAfter;
                return;
            }
            this.observeAtUtc = nowUtc + ObserveAfter;
        }

        if (!active)
        {
            this.beforePress = null;
            this.nextCheckUtc = default;
            this.Status = !this.Enabled ? "Off" : "Idle (no unattended match)";
            return;
        }

        if (this.beforePress is { } before)
        {
            if (nowUtc < this.observeAtUtc)
                return;
            var after = this.ReadTimers();
            this.LastNudgeObservedReset = after is { } timers ? timers.DroppedSince(before) : null;
            this.Status = this.LastNudgeObservedReset switch
            {
                true => "Activity observed after Control press",
                false => "Control sent, but idle counters did not fall",
                _ => "Control sent; timer reading unavailable",
            };
            this.log($"[IdleGuard] {this.Status}: before [{before}], after [{after?.ToString() ?? "unavailable"}].");
            this.beforePress = null;
        }

        if (nowUtc < this.nextCheckUtc)
            return;
        this.nextCheckUtc = nowUtc + CheckEvery;
        var seen = this.ReadTimers();
        if (seen is null)
        {
            this.Status = "Unavailable (no valid input timer readings)";
            return;
        }
        if (seen.Value.Highest <= IdleThresholdSeconds)
        {
            this.Status = $"Armed: {seen.Value}";
            return;
        }
        if (this.controlHeld())
        {
            this.Status = "Waiting for the held Control key to be released";
            return;
        }
        var window = this.findWindow();
        if (window == 0)
        {
            this.Status = "Unavailable (no game window for this process)";
            return;
        }

        // A timeout can leave delivery uncertain: even a failed down needs an up.
        this.pressedWindow = window;
        this.retryReleaseAtUtc = default;
        this.releaseAtUtc = nowUtc + HoldFor;
        this.LastNudgeObservedReset = null;
        if (!this.sendKey(window, true))
        {
            this.Release();
            this.Status = "Control key-down delivery failed; release requested";
            this.log($"[IdleGuard] {this.Status} ({seen.Value}).");
            return;
        }
        this.beforePress = seen;
        this.observeAtUtc = DateTime.MaxValue;
        this.NudgesThisSession++;
        this.Status = $"Control press #{this.NudgesThisSession} sent; awaiting timer observation";
        this.log($"[IdleGuard] {this.Status} ({seen.Value}).");
    }

    // Also used on logout/unload. Native release falls back to an asynchronous message.
    public void Reset()
    {
        if (!this.Release())
            return;
        this.beforePress = null;
        this.nextCheckUtc = default;
        this.Status = this.Enabled ? "Idle (no unattended match)" : "Off";
    }

    private bool Release()
    {
        if (!this.IsKeyDown)
            return true;
        if (!this.sendKey(this.pressedWindow, false))
        {
            const string pending = "Control release pending; no further presses will be sent";
            if (this.Status != pending)
                this.log($"[IdleGuard] {pending}.");
            this.Status = pending;
            return false;
        }
        this.pressedWindow = 0;
        return true;
    }

    private Timers? ReadTimers()
    {
        this.LastTimers = this.read() is { IsValid: true } timers ? timers : null;
        if (this.LastTimers is { } seen)
            this.HighestSeenSeconds = Math.Max(this.HighestSeenSeconds, seen.Highest);
        return this.LastTimers;
    }

    private static unsafe Timers? ReadGameTimers()
    {
        var ui = UIModule.Instance();
        if (ui == null)
            return null;
        var timers = ui->GetInputTimerModule();
        return timers == null ? null : new Timers(timers->AfkTimer, timers->ContentInputTimer, timers->InputTimer);
    }

    private static nint FindGameWindow()
    {
        nint window = 0;
        while ((window = FindWindowExW(0, window, "FFXIVGAME", null)) != 0)
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId == Environment.ProcessId)
                return window;
        }
        return 0;
    }

    private static bool SendControl(nint window, bool down)
    {
        // Revalidate delayed key-up ownership; a destroyed window needs no release.
        GetWindowThreadProcessId(window, out var processId);
        if (processId != Environment.ProcessId)
            return !down;
        const uint keyDown = 0x100, keyUp = 0x101;
        const int leftControl = 0xA2;
        // Same Control messages as the upstream plugin. Cross-thread sends are bounded;
        // same-thread sends invoke the window procedure directly, without a wait loop.
        const uint abortIfHung = 0x02, errorOnExit = 0x20;
        if (SendMessageTimeoutW(window, down ? keyDown : keyUp, leftControl, 0,
                abortIfHung | errorOnExit, 50, out _) != 0)
            return true;
        if (!down)
            PostMessageW(window, keyUp, leftControl, 0);
        // Enqueueing is not completion. Keep automation blocked until a later
        // synchronous release succeeds; the queued up also covers plugin teardown.
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindowExW(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nint key, nint data,
        uint flags, uint timeoutMs, out nuint result);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nint key, nint data);
}
