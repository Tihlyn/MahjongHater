namespace MahjongHater.Core.State;

// Type 32 describes the win; type 29 later supplies four relative-seat transfers.
// Neither an untyped/default zero nor an unfinished animation is a settlement.
public sealed record HandSettlement(int[] SeatDeltas, int WinnerSeat, bool WinByRon, int RonVictimSeat, bool IsDraw)
{
    public bool OutcomeKnown => this.IsDraw || this.WinnerSeat >= 0 && (!this.WinByRon || this.RonVictimSeat >= 0);

    public static HandSettlement? Read(AtkFrame frame, bool? winByRon, bool isDraw)
    {
        if (frame.EventType != 29 || frame.Count < 5) return null;
        var deltas = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!frame.IsInt(i + 1)) return null;
            var points = (long)frame.Int(i + 1) * 100;
            if (points is < int.MinValue or > int.MaxValue) return null;
            deltas[i] = (int)points;
        }
        var gains = Enumerable.Range(0, 4).Where(i => deltas[i] > 0).ToArray();
        var losses = Enumerable.Range(0, 4).Where(i => deltas[i] < 0).ToArray();
        // Draw payouts can also have a single positive seat; require a win announcement.
        // Multiple positive seats are ambiguous (e.g. multiple wins), never automatically us.
        var payerCountKnown = winByRon == true ? losses.Length == 1 : losses.Length == 3;
        var winner = !isDraw && winByRon != null && gains.Length == 1 && payerCountKnown ? gains[0] : -1;
        var victim = winner >= 0 && winByRon == true && losses.Length == 1 ? losses[0] : -1;
        return new HandSettlement(deltas, winner, winByRon == true, victim, isDraw);
    }
}
