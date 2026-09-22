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
// The nudge is a real keystroke on a high function key (F19 by default). Synthetic mouse
// movement does NOT reset this client's timer — measured in play, not assumed — and F13+ is
// outside both the default keybinds and any key the game acts on, so the keystroke cannot
// trigger an action. It is sent with SendInput only while the game window has focus, since
// SendInput goes to whatever is focused; in the background it is posted to the game window
// alone, which no other application can receive but which the client may or may not count
// as activity, hence `LastNudgeReachedTheGame`.
public sealed class IdleGuard
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(150);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromMinutes(4);

    // Virtual keys F13-F24. Above F18 is what an idle FFXIV client has been observed to
    // accept while ignoring synthetic mouse movement; nothing in the game binds them.
    public const int FirstKey = 0x7C;          // F13
    public const int DefaultKey = 0x82;        // F19
    public const int LastKey = 0x87;           // F24

    private const int InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MapVkToScanCode = 0;
    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101;

    private readonly Action<string> log;
    private readonly Func<TimeSpan?> systemIdle;
    private readonly Func<int, bool> nudge;
    private DateTime lastNudgeUtc = DateTime.MinValue;
    private int key = DefaultKey;

    // The two seams are the platform: how long the machine has been idle, and sending the
    // keystroke. Tests drive the gating with their own pair.
    public IdleGuard(Action<string> log, Func<TimeSpan?>? systemIdle = null, Func<int, bool>? nudge = null)
    {
        this.log = log;
        this.systemIdle = systemIdle ?? SystemIdle;
        this.nudge = nudge ?? SendNudge;
    }

    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = DefaultInterval;

    // Which function key the nudge presses; clamped to F13-F24 so it can never be a key the
    // game acts on.
    public int Key
    {
        get => this.key;
        set => this.key = ClampKey(value);
    }

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
        this.LastNudgeReachedTheGame = this.nudge(this.Key);
        this.Status = this.LastNudgeReachedTheGame
            ? $"Nudged {this.NudgesThisSession}× with {KeyName(this.Key)} (last {nowUtc:HH:mm:ss} UTC)"
            : $"Nudged {this.NudgesThisSession}× — game in the background, {KeyName(this.Key)} posted to its window only";
        this.log($"[IdleGuard] anti-idle nudge #{this.NudgesThisSession} ({KeyName(this.Key)}) after {systemIdle.Value.TotalSeconds:F0} s idle "
                 + (this.LastNudgeReachedTheGame ? "(sent to the focused game window)" : "(posted to the background game window)"));
    }

    public void Reset()
    {
        this.lastNudgeUtc = DateTime.MinValue;
        this.Status = this.Enabled ? "Idle (no unattended match)" : "Off";
    }

    public static TimeSpan Clamp(TimeSpan interval) =>
        interval < MinimumInterval ? MinimumInterval : interval > MaximumInterval ? MaximumInterval : interval;

    public static int ClampKey(int key) => key is >= FirstKey and <= LastKey ? key : DefaultKey;

    public static string KeyName(int key) => $"F{key - FirstKey + 13}";

    // True when the keystroke went to the focused game window; false when it was posted to
    // the game window in the background (or could not be sent at all).
    private static bool SendNudge(int key)
    {
        var window = Process.GetCurrentProcess().MainWindowHandle;
        try
        {
            if (window != nint.Zero && GetForegroundWindow() == window)
            {
                // Virtual key and its scan code together, so both message-based and raw input
                // readers see a complete press.
                var scan = (ushort)MapVirtualKeyW((uint)key, MapVkToScanCode);
                Span<Input> press =
                [
                    new() { Type = InputKeyboard, Data = new InputData { Keyboard = new KeyboardInput { Vk = (ushort)key, Scan = scan } } },
                    new() { Type = InputKeyboard, Data = new InputData { Keyboard = new KeyboardInput { Vk = (ushort)key, Scan = scan, Flags = KeyEventKeyUp } } },
                ];
                return SendInput((uint)press.Length, ref MemoryMarshal.GetReference(press), Marshal.SizeOf<Input>()) == press.Length;
            }

            if (window != nint.Zero)
            {
                PostMessageW(window, WmKeyDown, key, 0);
                PostMessageW(window, WmKeyUp, key, 0);
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
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    // Unused, and required: SendInput rejects a cbSize that is not the real INPUT size, and
    // the union is sized by its mouse member.
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
        public KeyboardInput Keyboard;

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

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostMessageW")]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
