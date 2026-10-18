using System.Globalization;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

// Our own tenpai win rate on the tables we play, learned from our first tenpai of each hand
// and whether that hand was ours. HandValue.WinProbability's K is fitted on Phoenix-trained
// tables; tables that fold less let a tenpai win much more often (arena, our riichi: 47 % won
// against the network, 78 % against three novices, 2026-10-18). Who folds is hard to read
// seat by seat in a 4-hand match (about three discards after a riichi per opponent), but our
// own results are always seen, so the population's factor on K is fitted on them (MAP on
// log factor, normal prior), closed and open hands apart. Live it is persisted across
// sessions; in the arena it comes from warm-up matches on other seeds.
public sealed class WinReveals
{
    private const double PriorLogFactor = 0.5;

    // One hand: open or closed, the estimate's hazard (K × ukeire × turns left / unseen) at
    // our first tenpai, and whether we won the hand.
    public sealed record Row(bool Open, double Hazard, bool Won);

    private readonly object gate = new();
    private readonly List<Row> rows = [];
    private readonly bool learn;
    private readonly double[] factor = [1, 1];
    private (bool Open, double Hazard)? pending;
    private int ourDiscards;

    // `learn = false` keeps the fit fixed (arena evaluation).
    public WinReveals(IEnumerable<Row>? rows = null, bool learn = true)
    {
        this.learn = learn;
        if (rows is not null)
            this.rows.AddRange(rows);
        this.Fit();
    }

    // Live: each new row, to be persisted.
    public Action<Row>? RowAdded { get; set; }

    public IReadOnlyList<Row> Rows
    {
        get { lock (this.gate) return this.rows.ToArray(); }
    }

    public double Factor(bool open)
    {
        lock (this.gate) return this.factor[open ? 1 : 0];
    }

    // Once per snapshot: our river getting shorter is a new hand; an unresolved tenpai of the
    // last one (its end was not seen) is dropped.
    public void Observe(StateSnapshot state)
    {
        lock (this.gate)
        {
            var discards = state.Us.Discards.Count;
            if (discards < this.ourDiscards)
                this.pending = null;
            this.ourDiscards = discards;
        }
    }

    // We are discarding into tenpai; only the hand's first tenpai counts.
    public void Tenpai(bool open, double hazard)
    {
        lock (this.gate)
            this.pending ??= (open, hazard);
    }

    public void HandEnded(bool won)
    {
        Row? added = null;
        lock (this.gate)
        {
            if (this.pending is { } p && this.learn)
            {
                added = new Row(p.Open, p.Hazard, won);
                this.rows.Add(added);
                this.Fit();
            }

            this.pending = null;
            this.ourDiscards = 0;
        }

        if (added is not null)
            this.RowAdded?.Invoke(added);
    }

    public static string ToCsv(Row r) => string.Create(CultureInfo.InvariantCulture, $"{(r.Open ? 1 : 0)},{r.Hazard:0.#####},{(r.Won ? 1 : 0)}");

    public static Row? FromCsv(string line)
    {
        var f = line.Split(',');
        return f.Length >= 3 && f[0] is "0" or "1" && double.TryParse(f[1], CultureInfo.InvariantCulture, out var hazard) && hazard >= 0 && f[2] is "0" or "1"
            ? new Row(f[0] == "1", hazard, f[2] == "1") : null;
    }

    // Log posterior is concave in log factor: a ternary search on [-2, 2] is enough.
    private void Fit()
    {
        for (var kind = 0; kind < 2; kind++)
        {
            var open = kind == 1;
            var group = this.rows.Where(r => r.Open == open && r.Hazard > 0).ToList();
            double Score(double u)
            {
                var f = Math.Exp(u);
                var s = -u * u / (2 * PriorLogFactor * PriorLogFactor);
                foreach (var r in group)
                {
                    var hazard = r.Hazard * f;
                    s += r.Won ? Math.Log(Math.Max(1e-12, 1 - Math.Exp(-hazard))) : -hazard;
                }

                return s;
            }

            double lo = -2, hi = 2;
            for (var i = 0; i < 80 && group.Count > 0; i++)
            {
                var a = lo + (hi - lo) / 3;
                var b = hi - (hi - lo) / 3;
                if (Score(a) < Score(b)) lo = a; else hi = b;
            }

            this.factor[kind] = group.Count > 0 ? Math.Exp((lo + hi) / 2) : 1;
        }
    }
}
