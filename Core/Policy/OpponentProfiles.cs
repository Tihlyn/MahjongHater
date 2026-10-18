using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

// How each opponent has played this match so far, from its rivers alone. The signal is the
// share of middle tiles (3-7) among a seat's first six discards: Phoenix players throw 16.4 %
// (28 088 seat-hands; 3+ of 6 in under 10 % of hands), a random discarder ~44 %. On
// 2026-10-18 the human Novice-queue opponents threw 50-83 %, the NPCs 17-33 %.
//
// A novice-like seat's discards say little about its hand: the early-outside read is off and
// a flush is read from calls only. Suji keeps its furiten protection (the game enforces it),
// but non-ryanmen waits are commoner, so suji/honor danger gets a provisional factor that
// dealin_calibration.csv (profile column) is there to fit.
public sealed class OpponentProfiles
{
    public const double PhoenixEarlyMiddle = 0.164;
    private const int PriorDiscards = 12;
    private const int EarlyDiscards = 6;

    private readonly int[] middle = new int[4];
    private readonly int[] counted = new int[4];
    private readonly int[] lastDiscards = new int[4];
    private readonly int[] handMiddle = new int[4];
    private readonly int[] handCounted = new int[4];

    // Call with each snapshot. A seat whose river got shorter started a new hand; its previous
    // hand's early discards are banked. A fresh East 1 with everyone on 25 000 is a new match.
    public void Observe(StateSnapshot state)
    {
        var freshMatch = state.RoundWind == Wind.East && state.HandNumber <= 1
                         && state.Seats.All(s => s.Score == 25000 && s.Discards.Count == 0);
        if (freshMatch)
        {
            // Before banking: the last match's unfinished hand must not leak into this one.
            this.Reset();
            return;
        }

        foreach (var seat in state.Seats)
        {
            if (seat.Seat is < 1 or > 3)
                continue;
            var s = seat.Seat;
            if (seat.Discards.Count < this.lastDiscards[s])
            {
                this.middle[s] += this.handMiddle[s];
                this.counted[s] += this.handCounted[s];
            }

            this.lastDiscards[s] = seat.Discards.Count;
            var early = seat.Discards.Take(EarlyDiscards).ToList();
            this.handCounted[s] = early.Count;
            this.handMiddle[s] = early.Count(t => !t.IsHonor && t.Number is >= 3 and <= 7);
        }
    }

    // Posterior mean of the early-middle share, prior at the Phoenix rate.
    public double EarlyMiddleRate(int seat) =>
        (this.middle[seat] + this.handMiddle[seat] + PhoenixEarlyMiddle * PriorDiscards)
        / (this.counted[seat] + this.handCounted[seat] + PriorDiscards);

    public int Observed(int seat) => this.counted[seat] + this.handCounted[seat];

    public bool NoviceLike(int seat, PolicyWeights w) => this.EarlyMiddleRate(seat) >= w.NoviceEarlyMiddleRate;

    public string Describe(int seat, PolicyWeights w) =>
        this.Observed(seat) < EarlyDiscards ? "unprofiled"
        : this.NoviceLike(seat, w) ? $"novice-like ({this.EarlyMiddleRate(seat):P0} early middles)"
        : $"steady ({this.EarlyMiddleRate(seat):P0} early middles)";

    public void Reset()
    {
        Array.Clear(this.middle);
        Array.Clear(this.counted);
        Array.Clear(this.lastDiscards);
        Array.Clear(this.handMiddle);
        Array.Clear(this.handCounted);
    }
}
