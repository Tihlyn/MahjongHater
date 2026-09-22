using FFXIVClientStructs.FFXIV.Client.UI;

namespace MahjongHater.Core.Operate;

// Duties eject a player who gives no input for about five minutes, which ends an unattended
// run mid-match. The auto player's actions are ATK event dispatches inside the addon, and the
// client's idle timers never see them.
//
// The first attempt (2026-09-22) sent a synthetic F19 through SendInput and did not work: the
// inactivity warning still appeared, so the client does not count synthetic input for these
// timers. This version does what NightmareXIV's AntiAfkKick does and writes the timers
// themselves. `UIModule.InputTimerModule` holds three seconds-since-input counters —
// `AfkTimer` (the auto-AFK flag), `ContentInputTimer` (the duty kick, compared against
// `InstanceContentAfkTimeLimit`, 300 s) and `InputTimer` — and zeroing them is exactly what
// real input does. No synthetic keystroke, no focus requirement, nothing sent to other windows.
//
// Scope is deliberately narrow: only while auto play is running a match, so ordinary AFK
// behaviour is untouched the rest of the time.
public sealed class IdleGuard
{
    // What the timers read before they were cleared; the overlay shows the high-water mark as
    // the proof that the guard is doing something.
    public readonly record struct Timers(float Afk, float ContentInput, float Input)
    {
        public float Highest => Math.Max(this.Afk, Math.Max(this.ContentInput, this.Input));
    }

    private readonly Action<string> log;
    private readonly Func<Timers?> clear;
    private bool loggedFirstClear;

    // The seam is the platform: read the three timers and zero them, or null when the module
    // is not reachable (outside the game, between zones). Tests supply their own.
    public IdleGuard(Action<string> log, Func<Timers?>? clear = null)
    {
        this.log = log;
        this.clear = clear ?? ClearGameTimers;
    }

    public bool Enabled { get; set; } = true;

    public int ClearsThisSession { get; private set; }

    // Highest value any timer reached before a clear: with the guard working this stays a few
    // seconds, and anything approaching the 300 s duty limit means it is not.
    public float HighestSeenSeconds { get; private set; }

    public string Status { get; private set; } = "Off";

    // `inMatch` is AutoPlayer.InMatch (the Emj addon is open). Framework thread only.
    public void Tick(bool autoPlayEnabled, bool inMatch, DateTime nowUtc)
    {
        if (!this.Enabled || !autoPlayEnabled || !inMatch)
        {
            this.Status = !this.Enabled ? "Off" : "Idle (no unattended match)";
            return;
        }

        if (this.clear() is not { } seen)
        {
            this.Status = "Unavailable (no input timer module)";
            return;
        }

        this.ClearsThisSession++;
        this.HighestSeenSeconds = Math.Max(this.HighestSeenSeconds, seen.Highest);
        this.Status = $"Idle timers held at zero (highest seen {this.HighestSeenSeconds:F0} s over {this.ClearsThisSession} ticks)";
        if (!this.loggedFirstClear)
        {
            this.loggedFirstClear = true;
            this.log($"[IdleGuard] Holding the duty idle timers at zero while auto play runs "
                     + $"(afk {seen.Afk:F0} s, content {seen.ContentInput:F0} s, input {seen.Input:F0} s at the first clear).");
        }
    }

    public void Reset()
    {
        this.loggedFirstClear = false;
        this.Status = this.Enabled ? "Idle (no unattended match)" : "Off";
    }

    private static unsafe Timers? ClearGameTimers()
    {
        var ui = UIModule.Instance();
        if (ui == null)
            return null;
        var timers = ui->GetInputTimerModule();
        if (timers == null)
            return null;
        var seen = new Timers(timers->AfkTimer, timers->ContentInputTimer, timers->InputTimer);
        timers->AfkTimer = 0;
        timers->ContentInputTimer = 0;
        timers->InputTimer = 0;
        return seen;
    }
}
