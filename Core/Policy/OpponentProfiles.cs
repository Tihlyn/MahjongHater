using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

// Which kind of player each opponent is this match: steady (plays like Tenhou Phoenix) or
// novice-like, as a posterior P(novice) from its rivers.
//
// Why two types and not per-player rates: within one Phoenix game a player's first half
// barely predicts the second (split-half r 0.05 for calls, early middles and folding, 0.00
// for riichi; tools/seat_style_baseline.py), so per-player rates are noise at 8-16 hands.
// What does differ live is the population a player comes from: on 2026-10-18 one human in
// three threw 36 % early middles (one hand 6 of 6, which Phoenix players do in 0.1 % of
// hands), the other two 5 % and 19 %.
//
// Evidence per hand, as tempered likelihood ratios novice : steady:
//  * middle tiles (3-7) among the first six discards. Steady: the Phoenix histogram. Novice:
//    beta-binomial, mean 35 % (the live novice-like player), concentration 2 — novices vary
//    hand to hand (that player had three hands with none and one with six);
//  * after another seat's riichi (ours included), the share of its discards that were
//    genbutsu to it, in fifths, once three were made. Steady: Phoenix. Novice: provisional,
//    centred near 35 % (the same player's live share); hand_rivers.jsonl is there to fit it.
// Prior 25 %, temper 0.7: Phoenix player-games read as novice 2.1 % of the time after 8 hands
// and 0 % after 16; the live novice-like player 82 %, the two steady ones 3 % and 17 %.
//
// A novice-like seat's discards say little about its hand: the early-outside read is off and
// a flush is read from calls only. Suji keeps its furiten protection (the game enforces it),
// but non-ryanmen waits are commoner, so suji/honor danger gets a provisional factor that
// dealin_calibration.csv (profile column) is there to fit.
public sealed class OpponentProfiles
{
    public const double PhoenixEarlyMiddle = 0.164;
    public const double Prior = 0.25;
    public const double Temper = 0.7;
    private const int EarlyDiscards = 6;
    private const int FoldMinDiscards = 3;

    // P(k middles among the first six | type), k = 0..6.
    private static readonly double[] SteadyEarly = [0.421, 0.3099, 0.1736, 0.0691, 0.0209, 0.0045, 0.001];
    private static readonly double[] NoviceEarly = BetaBinomial(6, 0.35, 2);
    // P(genbutsu share after a riichi falls in fifth b | type), b = 0..4.
    private static readonly double[] SteadyFold = [0.0455, 0.1316, 0.1719, 0.3111, 0.3399];
    private static readonly double[] NoviceFold = [0.25, 0.30, 0.22, 0.13, 0.10];

    private readonly double[] banked = new double[4];        // tempered log-likelihood ratio of finished hands
    private readonly double[] handEvidence = new double[4];   // the current hand's, recomputed every snapshot
    private readonly int[] hands = new int[4];
    private readonly bool[] handCounted = new bool[4];
    private readonly int[] lastDiscards = new int[4];
    private readonly int[] middle = new int[4];
    private readonly int[] counted = new int[4];
    private readonly int[] handMiddle = new int[4];
    private readonly int[] handEarly = new int[4];

    // Call with each snapshot. A seat whose river got shorter started a new hand; its previous
    // hand's evidence is banked. A fresh East 1 with everyone on 25 000 is a new match.
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
                this.banked[s] += this.handEvidence[s];
                this.hands[s] += this.handCounted[s] ? 1 : 0;
                this.middle[s] += this.handMiddle[s];
                this.counted[s] += this.handEarly[s];
                this.handEvidence[s] = 0;
                this.handCounted[s] = false;
            }

            this.lastDiscards[s] = seat.Discards.Count;
            var early = seat.Discards.Take(EarlyDiscards).ToList();
            this.handEarly[s] = early.Count;
            this.handMiddle[s] = early.Count(IsMiddle);

            var evidence = 0d;
            var counted = false;
            if (early.Count == EarlyDiscards)
            {
                evidence += Math.Log(NoviceEarly[this.handMiddle[s]] / SteadyEarly[this.handMiddle[s]]);
                counted = true;
            }

            if (FoldShare(seat, state) is { } share)
            {
                var fifth = Math.Min(4, (int)(5 * share));
                evidence += Math.Log(NoviceFold[fifth] / SteadyFold[fifth]);
                counted = true;
            }

            this.handEvidence[s] = Temper * evidence;
            this.handCounted[s] = counted;
        }
    }

    public double NoviceProbability(int seat)
    {
        var logit = Math.Log(Prior / (1 - Prior)) + this.banked[seat] + this.handEvidence[seat];
        return 1 / (1 + Math.Exp(-logit));
    }

    public bool NoviceLike(int seat, PolicyWeights w) => this.NoviceProbability(seat) >= w.NoviceProbabilityThreshold;

    // Hands that gave evidence (six discards, or three after a riichi), this one included.
    public int Observed(int seat) => this.hands[seat] + (this.handCounted[seat] ? 1 : 0);

    // Raw early-middle share, for the log line.
    public double EarlyMiddleRate(int seat)
    {
        var n = this.counted[seat] + this.handEarly[seat];
        return n == 0 ? PhoenixEarlyMiddle : (double)(this.middle[seat] + this.handMiddle[seat]) / n;
    }

    public string Describe(int seat, PolicyWeights w) =>
        this.Observed(seat) == 0 ? "unprofiled"
        : $"{(this.NoviceLike(seat, w) ? "novice-like" : "steady")} (P(novice) {this.NoviceProbability(seat):P0}, {this.EarlyMiddleRate(seat):P0} early middles)";

    public void Reset()
    {
        Array.Clear(this.banked);
        Array.Clear(this.handEvidence);
        Array.Clear(this.hands);
        Array.Clear(this.handCounted);
        Array.Clear(this.lastDiscards);
        Array.Clear(this.middle);
        Array.Clear(this.counted);
        Array.Clear(this.handMiddle);
        Array.Clear(this.handEarly);
    }

    private static bool IsMiddle(Tile t) => !t.IsHonor && t.Number is >= 3 and <= 7;

    // Share of the seat's discards after the first other riichi that were genbutsu to it (the
    // riichi seat's own discards or anything thrown after the declaration); null with fewer
    // than three such discards, when the seat declared first, or without global discard order.
    internal static double? FoldShare(SeatState seat, StateSnapshot state)
    {
        if (seat.DiscardOrder.Count != seat.Discards.Count)
            return null;
        var riichi = state.Seats.Where(r => r.Seat != seat.Seat && r.Riichi && r.RiichiDiscardIndex >= 0
                                            && r.DiscardOrder.Count == r.Discards.Count && r.RiichiDiscardIndex < r.Discards.Count)
            .OrderBy(r => r.DiscardOrder[r.RiichiDiscardIndex]).FirstOrDefault();
        if (riichi is null)
            return null;
        var at = riichi.DiscardOrder[riichi.RiichiDiscardIndex];
        if (seat.Riichi && seat.RiichiDiscardIndex >= 0 && seat.RiichiDiscardIndex < seat.DiscardOrder.Count
            && seat.DiscardOrder[seat.RiichiDiscardIndex] < at)
            return null;
        var all = state.Seats.Where(r => r.DiscardOrder.Count == r.Discards.Count)
            .SelectMany(r => r.Discards.Select((t, i) => (Kind: TileHelpers.ToIndex(t), Order: r.DiscardOrder[i]))).ToList();
        var own = riichi.Discards.Select(TileHelpers.ToIndex).ToHashSet();
        int n = 0, safe = 0;
        for (var i = 0; i < seat.Discards.Count; i++)
        {
            var order = seat.DiscardOrder[i];
            if (order <= at)
                continue;
            var kind = TileHelpers.ToIndex(seat.Discards[i]);
            n++;
            if (own.Contains(kind) || all.Any(d => d.Kind == kind && d.Order > at && d.Order < order))
                safe++;
        }

        return n >= FoldMinDiscards ? (double)safe / n : null;
    }

    private static double[] BetaBinomial(int n, double mean, double concentration)
    {
        double a = mean * concentration, b = (1 - mean) * concentration;
        static double LogBeta(double x, double y) => LogGamma(x) + LogGamma(y) - LogGamma(x + y);
        var p = new double[n + 1];
        for (var k = 0; k <= n; k++)
            p[k] = Math.Exp(LogChoose(n, k) + LogBeta(k + a, n - k + b) - LogBeta(a, b));
        return p;
    }

    private static double LogChoose(int n, int k) => LogGamma(n + 1) - LogGamma(k + 1) - LogGamma(n - k + 1);

    // Lanczos (g = 7); exact to ~1e-15 for the positive arguments used here.
    private static double LogGamma(double x)
    {
        double[] c = [0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7];
        if (x < 0.5)
            return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        x -= 1;
        var sum = c[0];
        for (var i = 1; i < 9; i++)
            sum += c[i] / (x + i);
        var t = x + 7.5;
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(sum);
    }
}
