using System.Globalization;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

// Tenpai estimates corrected by what the table has shown. An exhaustive draw reveals every
// seat's tenpai, so each draw gives one labelled row per non-riichi opponent: the model's own
// estimate at its last update of the hand, the seat's call group (closed, 1, 2, 3+ calls), how
// little it folds (1 - OpponentProfiles.Steadiness) and whether it was tenpai. On those rows a
// correction per group, logit p' = logit p + a + b × looseness, is fitted (MAP, normal priors,
// so a few rows barely move it), plus a per-seat offset from that seat's draws this match.
//
// Why: against the Phoenix-trained network's tenpai head, arena seats that call whatever keeps
// a yaku were 5 % / 14 % tenpai at one / two calls against 26 % / 55 % predicted, while
// Phoenix-like seats matched (2026-10-18, `Precompute calibrate`); live FFXIV draws showed two
// calls tenpai 49 % of the time against 86 % predicted (tenpai_calibration.csv). Wins are not
// used: only the winner is shown, and winners say nothing about how often calls end noten.
// A seat's own draws are about one per match, so most of the correction is the population's,
// learned over many matches (live: persisted; arena: warm-up matches on other seeds).
public sealed class TenpaiReveals
{
    public const int Groups = 4;
    private const double PriorA = 1.5, PriorB = 2.0, PriorSeat = 0.7;

    // One revealed seat: its group, the uncorrected estimate as a logit, its looseness, the truth.
    public sealed record Row(int Group, double Logit, double Looseness, bool Tenpai);

    private readonly object gate = new();
    private readonly List<Row> rows = [];
    private readonly bool learn;
    private readonly double[,] coefficients = new double[Groups, 2];
    private readonly (int Group, double Logit, double Looseness)?[] last = new (int, double, double)?[4];
    private readonly List<Row>[] seatRows = [[], [], [], []];
    private readonly double[] seatOffset = new double[4];

    // `learn = false` keeps the population fit fixed (arena evaluation, where matches run in
    // parallel); the per-seat offsets still follow the match.
    public TenpaiReveals(IEnumerable<Row>? rows = null, bool learn = true)
    {
        this.learn = learn;
        if (rows is not null)
            this.rows.AddRange(rows);
        this.Fit();
    }

    // Live: each new population row, to be persisted.
    public Action<Row>? RowAdded { get; set; }

    public static int Group(SeatState seat) => Math.Min(Groups - 1, seat.Melds.Count(m => m.IsOpen));

    public IReadOnlyList<Row> Rows
    {
        get { lock (this.gate) return this.rows.ToArray(); }
    }

    public (double A, double B) Coefficients(int group)
    {
        lock (this.gate) return (this.coefficients[group, 0], this.coefficients[group, 1]);
    }

    public double SeatOffset(int seat)
    {
        lock (this.gate) return this.seatOffset[seat];
    }

    // Once per snapshot, before Adjust: a fresh East 1 (no honba) with everyone on 25 000 is a
    // new match.
    public void Observe(StateSnapshot state)
    {
        if (state.RoundWind != Wind.East || state.HandNumber > 1 || state.Honba > 0 || !state.Seats.All(s => s.Score == 25000 && s.Discards.Count == 0))
            return;
        lock (this.gate)
        {
            foreach (var list in this.seatRows)
                list.Clear();
            Array.Clear(this.seatOffset);
            Array.Clear(this.last);
        }
    }

    // The corrected estimate for a seat (`apply` = false: only remember it). The uncorrected
    // estimate is what a draw would label. Riichi seats are certain and give no row.
    public double Adjust(int seat, double p, SeatState state, double looseness, bool apply)
    {
        lock (this.gate)
        {
            if (state.Riichi || p <= 0 || p >= 1)
            {
                this.last[seat] = null;
                return p;
            }

            var group = Group(state);
            var logit = Logit(p);
            this.last[seat] = (group, logit, looseness);
            if (!apply)
                return p;
            var z = logit + this.coefficients[group, 0] + this.coefficients[group, 1] * looseness + this.seatOffset[seat];
            return 1 / (1 + Math.Exp(-z));
        }
    }

    // An exhaustive draw: tenpai per relative seat (index 1..3, null when not shown).
    public void Draw(IReadOnlyList<bool?> tenpai)
    {
        var added = new List<Row>();
        lock (this.gate)
        {
            for (var seat = 1; seat <= 3 && seat < tenpai.Count; seat++)
            {
                if (tenpai[seat] is not { } truth || this.last[seat] is not { } seen)
                    continue;
                var row = new Row(seen.Group, seen.Logit, seen.Looseness, truth);
                this.seatRows[seat].Add(row);
                if (this.learn)
                {
                    this.rows.Add(row);
                    added.Add(row);
                }
            }

            Array.Clear(this.last);
            if (added.Count > 0)
                this.Fit();
            this.FitSeats();
        }

        foreach (var row in added)
            this.RowAdded?.Invoke(row);
    }

    public static string ToCsv(Row r) => string.Create(CultureInfo.InvariantCulture,
        $"{r.Group},{r.Logit:0.####},{r.Looseness:0.###},{(r.Tenpai ? 1 : 0)}");

    public static Row? FromCsv(string line)
    {
        var f = line.Split(',');
        return f.Length >= 4 && int.TryParse(f[0], CultureInfo.InvariantCulture, out var g) && g is >= 0 and < Groups
               && double.TryParse(f[1], CultureInfo.InvariantCulture, out var logit)
               && double.TryParse(f[2], CultureInfo.InvariantCulture, out var loose) && f[3] is "0" or "1"
            ? new Row(g, logit, loose, f[3] == "1") : null;
    }

    private static double Logit(double p)
    {
        p = Math.Clamp(p, 1e-4, 1 - 1e-4);
        return Math.Log(p / (1 - p));
    }

    // MAP of (a, b) per group by Newton's method.
    private void Fit()
    {
        for (var g = 0; g < Groups; g++)
        {
            double a = 0, b = 0;
            var group = this.rows.Where(r => r.Group == g).ToList();
            for (var iteration = 0; iteration < 30 && group.Count > 0; iteration++)
            {
                double ga = -a / (PriorA * PriorA), gb = -b / (PriorB * PriorB);
                double haa = -1 / (PriorA * PriorA), hab = 0, hbb = -1 / (PriorB * PriorB);
                foreach (var r in group)
                {
                    var p = 1 / (1 + Math.Exp(-(r.Logit + a + b * r.Looseness)));
                    var e = (r.Tenpai ? 1 : 0) - p;
                    var v = p * (1 - p);
                    ga += e;
                    gb += e * r.Looseness;
                    haa -= v;
                    hab -= v * r.Looseness;
                    hbb -= v * r.Looseness * r.Looseness;
                }

                var det = haa * hbb - hab * hab;
                var da = -(hbb * ga - hab * gb) / det;
                var db = -(haa * gb - hab * ga) / det;
                a += da;
                b += db;
                if (Math.Abs(da) + Math.Abs(db) < 1e-7)
                    break;
            }

            this.coefficients[g, 0] = a;
            this.coefficients[g, 1] = b;
        }

        this.FitSeats();
    }

    private void FitSeats()
    {
        for (var seat = 1; seat <= 3; seat++)
        {
            var e = 0d;
            for (var iteration = 0; iteration < 30 && this.seatRows[seat].Count > 0; iteration++)
            {
                double grad = -e / (PriorSeat * PriorSeat), hess = -1 / (PriorSeat * PriorSeat);
                foreach (var r in this.seatRows[seat])
                {
                    var p = 1 / (1 + Math.Exp(-(r.Logit + this.coefficients[r.Group, 0] + this.coefficients[r.Group, 1] * r.Looseness + e)));
                    grad += (r.Tenpai ? 1 : 0) - p;
                    hess -= p * (1 - p);
                }

                var step = -grad / hess;
                e += step;
                if (Math.Abs(step) < 1e-7)
                    break;
            }

            this.seatOffset[seat] = e;
        }
    }
}
