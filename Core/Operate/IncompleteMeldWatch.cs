using MahjongHater.Core.State;

namespace MahjongHater.Core.Operate;

// General state activity must not conceal a persistent reconstruction failure.
internal sealed class IncompleteMeldWatch
{
    private DateTime? since;
    private bool reported;

    public bool Observe(StateSnapshot state, DateTime now)
    {
        if (state.Us.MeldsVerified || state.Phase is GamePhase.NotInGame or GamePhase.Dealing or GamePhase.RoundEnd)
        {
            this.Reset();
            return false;
        }
        this.since ??= now;
        if (this.reported || now - this.since.Value < TimeSpan.FromSeconds(5)) return false;
        this.reported = true;
        return true;
    }

    public void Reset() { this.since = null; this.reported = false; }
}
