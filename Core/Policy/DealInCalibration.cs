using System.Globalization;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

// One ground-truth row for the danger model: a discard we made while a seat was a threat,
// the model's estimate at the time, and whether that seat ron'd it. Rows accumulate in
// dealin_calibration.csv for tools/fit_danger.py (docs/DEFENSE_PLAN.md §3.8).
public sealed record DealInSample(
    DateTime Utc,
    string Round,
    int HandNumber,
    int Turn,
    int Seat,
    bool Riichi,
    int OpenMelds,
    double TenpaiEstimate,
    Tile Tile,
    string Class,
    DangerRank Rank,
    double Predicted,
    int LiveSuji,
    bool DealtIn,
    string Population,
    string Model = "V2");

public static class DealInCalibration
{
    public const string CsvHeader = "utc,round,hand,turn,seat,riichi,openMelds,tenpai,tile,class,rank,predicted,liveSuji,dealtIn,population,model";

    public static string ToCsv(DealInSample s) => string.Join(",",
        s.Utc.ToString("O", CultureInfo.InvariantCulture),
        s.Round,
        s.HandNumber.ToString(CultureInfo.InvariantCulture),
        s.Turn.ToString(CultureInfo.InvariantCulture),
        s.Seat.ToString(CultureInfo.InvariantCulture),
        s.Riichi ? "1" : "0",
        s.OpenMelds.ToString(CultureInfo.InvariantCulture),
        s.TenpaiEstimate.ToString("F3", CultureInfo.InvariantCulture),
        s.Tile.ToString(),
        s.Class.Replace(',', ';'),
        s.Rank.ToString(),
        s.Predicted.ToString("F4", CultureInfo.InvariantCulture),
        s.LiveSuji.ToString(CultureInfo.InvariantCulture),
        s.DealtIn ? "1" : "0",
        s.Population,
        s.Model);
}

// One row per finished hand for A/B runs (tools/ab_summary.py): how it ended for us and
// what it cost, tagged with the defense model and the opponent population.
public sealed record HandResult(
    DateTime Utc,
    string Round,
    int HandNumber,
    int OurDiscards,
    string Outcome,      // win-ron | win-tsumo | dealin | tsumo-loss | other-ron | draw-tenpai | draw-noten | draw
    int Delta,
    bool OurRiichi,
    int RiichiSeats,
    string Population,
    string Model)
{
    public const string CsvHeader = "utc,round,hand,discards,outcome,delta,ourRiichi,riichiSeats,population,model";

    public string ToCsv() => string.Join(",",
        this.Utc.ToString("O", CultureInfo.InvariantCulture),
        this.Round,
        this.HandNumber.ToString(CultureInfo.InvariantCulture),
        this.OurDiscards.ToString(CultureInfo.InvariantCulture),
        this.Outcome,
        this.Delta.ToString(CultureInfo.InvariantCulture),
        this.OurRiichi ? "1" : "0",
        this.RiichiSeats.ToString(CultureInfo.InvariantCulture),
        this.Population,
        this.Model);

    public static string Classify(int winnerSeat, bool winByRon, int ronVictimSeat, bool? ourTenpaiAtDraw)
    {
        if (winnerSeat == 0)
            return winByRon ? "win-ron" : "win-tsumo";
        if (winnerSeat is >= 1 and <= 3)
            return winByRon ? (ronVictimSeat == 0 ? "dealin" : "other-ron") : "tsumo-loss";
        return ourTenpaiAtDraw switch { true => "draw-tenpai", false => "draw-noten", _ => "draw" };
    }
}

// Watches snapshots for our own discards and turns each one into DealInSamples against
// every threatening seat (riichi, or open melds / tenpai estimate above the floor), using
// the danger model as it saw the board BEFORE the discard. Finish() labels the last rows
// once the hand's result is known and returns everything recorded this hand.
public sealed class DealInRecorder
{
    private readonly OpponentModel model;
    private readonly PolicyWeights weights;
    private readonly List<DealInSample> rows = [];
    private StateSnapshot? lastDecision;   // most recent snapshot with a discard legal for us
    private int seenOurDiscards;

    public DealInRecorder(PolicyWeights? weights = null)
    {
        this.weights = weights ?? PolicyWeights.Default;
        this.model = new OpponentModel(this.weights);
    }

    public string Population { get; set; } = "human";

    public string Model { get; set; } = "V2";

    public int PendingRows => this.rows.Count;

    // Call with every in-play snapshot (any phase but RoundEnd/NotInGame).
    public void Observe(StateSnapshot state, DateTime utc)
    {
        var count = state.Us.Discards.Count;
        if (count < this.seenOurDiscards)
        {
            // New hand started without a Finish (missed round end): drop the stale rows.
            this.rows.Clear();
            this.seenOurDiscards = 0;
            this.lastDecision = null;
        }

        if (count > this.seenOurDiscards)
        {
            if (this.lastDecision is { } decision && count == this.seenOurDiscards + 1)
                this.Record(decision, state.Us.Discards[count - 1], utc);
            this.seenOurDiscards = count;
        }

        if (state.Can(LegalAction.Discard) && state.Hand.Count + 3 * state.OurMelds.Count == 14)
            this.lastDecision = state;
    }

    // Hand over: label and return the rows. `ronVictimSeat`/`winnerSeat` come from the
    // tracker's type-32 read (-1 when the hand was a draw or a tsumo).
    public IReadOnlyList<DealInSample> Finish(int winnerSeat, bool winByRon, int ronVictimSeat, Tile? ronTile)
    {
        var result = new List<DealInSample>(this.rows.Count);
        var lastTurn = this.rows.Count > 0 ? this.rows[^1].Turn : -1;
        foreach (var row in this.rows)
        {
            var dealtIn = winByRon && ronVictimSeat == 0 && row.Seat == winnerSeat && row.Turn == lastTurn
                          && (ronTile is not { } rt || TileHelpers.SameKind(rt, row.Tile));
            result.Add(row with { DealtIn = dealtIn });
        }

        this.rows.Clear();
        this.seenOurDiscards = 0;
        this.lastDecision = null;
        return result;
    }

    private void Record(StateSnapshot decision, Tile discarded, DateTime utc)
    {
        this.model.Update(decision);
        var visible = new int[34];
        foreach (var tile in decision.Hand.Concat(decision.SeenForAnalyzer())
                     .Concat(decision.OurMelds.SelectMany(m => m.Tiles)).Concat(decision.DoraIndicators))
            visible[TileHelpers.ToIndex(tile)]++;
        var views = TileDangerModel.BuildViews(decision, visible);
        foreach (var seat in decision.Seats)
        {
            if (seat.Seat == 0)
                continue;
            var tenpai = this.model.TenpaiProbability(seat.Seat);
            var open = seat.Melds.Count(m => m.IsOpen);
            if (!seat.Riichi && open == 0 && tenpai < this.weights.ThreatTenpaiFloor)
                continue;
            var estimate = this.model.Explain(discarded, seat.Seat);
            this.rows.Add(new DealInSample(utc, decision.RoundWind.ToString(), decision.HandNumber, decision.Turn,
                seat.Seat, seat.Riichi, open, tenpai, TileHelpers.Normalize(discarded), estimate.Class, estimate.Rank,
                estimate.Probability, views[seat.Seat]?.LiveSuji ?? -1, false, this.Population, this.Model));
        }
    }
}
