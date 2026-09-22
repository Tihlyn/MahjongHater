using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MahjongHater.Core.Operate;

// Duties eject a player who gives no input for about five minutes, which ends an
// unattended run mid-match. The actions AutoPlayer takes do not count: they are ATK event
// dispatches inside the addon, not input the client's idle timer ever sees. So while a
// match is running unattended this sends one real, harmless input every interval.
//
// Guards, in the order they are checked every tick:
//   * only while auto play is on and the Emj addon is open (never outside a match),
//   * only when the machine has been idle for the whole interval (GetLastInputInfo covers
//     every application, so a nudge never lands while someone is typing or clicking),
//   * one nudge per interval, and never two within MinimumInterval.
//
// The nudge itself is a one-pixel relative mouse move and its exact reverse, sent only when
// the game window has focus; nothing can be clicked, dragged or bound to it. When the game
// is in the background the fallback posts a key-up/key-down pair for F13 (unbound in the
// default keybinds) to the game window alone, so no other application can receive it —
// whether the client counts a posted message as activity is not something the plugin can
// verify, hence `LastNudgeReachedTheGame`.
public sealed class IdleGuard
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(150);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromMinutes(4);

    private const int InputMouse = 0, MouseEventMove = 0x0001;
    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101;
    private const int VkF13 = 0x7C;

    private readonly Action<string> log;
    private readonly Func<TimeSpan?> systemIdle;
    private readonly Func<bool> nudge;
    private DateTime lastNudgeUtc = DateTime.MinValue;

    // The two seams are the platform: how long the machine has been idle, and sending the
    // input. Tests drive the gating with their own pair.
    public IdleGuard(Action<string> log, Func<TimeSpan?>? systemIdle = null, Func<bool>? nudge = null)
    {
        this.log = log;
        this.systemIdle = systemIdle ?? SystemIdle;
        this.nudge = nudge ?? SendNudge;
    }

    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = DefaultInterval;

    public int NudgesThisSession { get; private set; }

    public DateTime? LastNudgeUtc => this.lastNudgeUtc == DateTime.MinValue ? null : this.lastNudgeUtc;

    // False when the last nudge had to be posted to a background window, where the client
    // may or may not treat it as activity. Informational; the overlay shows it.
    public bool LastNudgeReachedTheGame { get; private set; } = true;

    public string Status { get; private set; } = "Off";

    // `inMatch` is AutoPlayer.InMatch (the addon is open). Framework thread only.
    public void Tick(bool autoPlayEnabled, bool inMatch, DateTime nowUtc)
    {
        if (!this.Enabled || !autoPlayEnabled || !inMatch)
        {
            this.Status = !this.Enabled ? "Off" : "Idle (no unattended match)";
            return;
        }

        var interval = Clamp(this.Interval);
        var systemIdle = this.systemIdle();
        if (systemIdle is null)
        {
            this.Status = "Unavailable (cannot read the system idle time)";
            return;
        }

        if (systemIdle < interval || nowUtc - this.lastNudgeUtc < MinimumInterval)
        {
            this.Status = $"Armed: nudges after {interval.TotalSeconds:F0} s idle (idle {systemIdle.Value.TotalSeconds:F0} s)";
            return;
        }

        this.lastNudgeUtc = nowUtc;
        this.NudgesThisSession++;
        this.LastNudgeReachedTheGame = this.nudge();
        this.Status = this.LastNudgeReachedTheGame
            ? $"Nudged {this.NudgesThisSession}× (last {nowUtc:HH:mm:ss} UTC)"
            : $"Nudged {this.NudgesThisSession}× — game in the background, posted to its window only";
        this.log($"[IdleGuard] anti-idle nudge #{this.NudgesThisSession} after {systemIdle.Value.TotalSeconds:F0} s idle "
                 + (this.LastNudgeReachedTheGame ? "(mouse, game focused)" : "(key posted to the background game window)"));
    }

    public void Reset()
    {
        this.lastNudgeUtc = DateTime.MinValue;
        this.Status = this.Enabled ? "Idle (no unattended match)" : "Off";
    }

    public static TimeSpan Clamp(TimeSpan interval) =>
        interval < MinimumInterval ? MinimumInterval : interval > MaximumInterval ? MaximumInterval : interval;

    // True when the input went to the focused game window; false when it was posted to the
    // game window in the background.
    private static bool SendNudge()
    {
        var window = Process.GetCurrentProcess().MainWindowHandle;
        try
        {
            if (window != nint.Zero && GetForegroundWindow() == window)
            {
                // Relative by one pixel and back: the pointer ends where it started.
                Span<Input> moves =
                [
                    new() { Type = InputMouse, Data = new InputData { Mouse = new MouseInput { Dx = 1, Dy = 0, Flags = MouseEventMove } } },
                    new() { Type = InputMouse, Data = new InputData { Mouse = new MouseInput { Dx = -1, Dy = 0, Flags = MouseEventMove } } },
                ];
                return SendInput((uint)moves.Length, ref MemoryMarshal.GetReference(moves), Marshal.SizeOf<Input>()) == moves.Length;
            }

            if (window != nint.Zero)
            {
                PostMessageW(window, WmKeyDown, VkF13, 0);
                PostMessageW(window, WmKeyUp, VkF13, 0);
            }

            return false;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    // Milliseconds since the last real input anywhere on the machine, null when unavailable.
    private static TimeSpan? SystemIdle()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            return null;
        var elapsed = unchecked((uint)Environment.TickCount - info.TickCount);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint TickCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)]
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputData Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, ref Input inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostMessageW")]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
