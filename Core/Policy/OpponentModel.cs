using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

public sealed class OpponentModel : IOpponentModel
{
    private readonly PolicyWeights weights;
    private readonly TileDangerModel danger;
    private readonly OpponentProfiles profiles;
    private readonly TenpaiReveals? reveals;
    private readonly WinReveals? wins;
    private Model model = new(new double[4], new DangerEstimate[4, 34], new double[4], new bool[4], -1, [-1, -1, -1, -1]);

    // `reveals`: tenpai corrected by what draws showed (applied with PolicyWeights.TenpaiReveals;
    // recorded either way, so a live store keeps learning while the switch is off). `wins`: our
    // own tenpai results on these tables (PolicyWeights.WinReveals), the same way.
    public OpponentModel(PolicyWeights? weights = null, TileDangerModel? danger = null, TenpaiReveals? reveals = null, WinReveals? wins = null)
    {
        this.weights = weights ?? PolicyWeights.Default;
        this.danger = danger ?? new TileDangerModel(weights: this.weights);
        this.profiles = new OpponentProfiles(this.weights.ProfileDecay);
        this.reveals = reveals;
        this.wins = wins;
    }

    public void Update(StateSnapshot state)
    {
        var visible = new int[34];
        foreach (var tile in state.Hand.Concat(state.SeenForAnalyzer())
                     .Concat(state.OurMelds.SelectMany(m => m.Tiles)).Concat(state.DoraIndicators))
            visible[TileHelpers.ToIndex(tile)]++;

        var probabilities = new double[4];
        var dangers = new DangerEstimate[4, 34];
        var values = new double[4];
        var riichi = new bool[4];
        bool[]? novice = null;
        double[]? noviceWeight = null;
        var v3 = this.weights.DefenseModel == DefenseModel.V3;
        if (this.weights.DefenseModel != DefenseModel.Legacy)
            this.profiles.Observe(state);
        this.reveals?.Observe(state);
        this.wins?.Observe(state);
        if (v3)
        {
            novice = [false, this.profiles.NoviceLike(1, this.weights), this.profiles.NoviceLike(2, this.weights), this.profiles.NoviceLike(3, this.weights)];
            if (this.weights.ProfileBlend)
                noviceWeight = [0, this.profiles.NoviceProbability(1), this.profiles.NoviceProbability(2), this.profiles.NoviceProbability(3)];
        }

        var views = this.weights.DefenseModel != DefenseModel.Legacy
            ? TileDangerModel.BuildViews(state, visible, novice, noviceWeight, readMixture: v3 && this.weights.ReadMixture) : null;
        foreach (var seat in state.Seats.Where(s => s.Seat is >= 1 and <= 3))
        {
            probabilities[seat.Seat] = TenpaiEstimator.WithRiverTells(TenpaiEstimator.Estimate(seat, this.weights), seat, state, this.weights);
            if (this.reveals is not null)
                probabilities[seat.Seat] = this.reveals.Adjust(seat.Seat, probabilities[seat.Seat], seat, 1 - this.Steadiness(seat.Seat), this.weights.TenpaiReveals);
            riichi[seat.Seat] = seat.Riichi;

            if (views is not null)
            {
                values[seat.Seat] = ThreatValue.Points(seat, state, this.weights) * ThreatValue.SituationFactor(seat, state, this.weights)
                                    * ThreatValue.OpenScale(seat, this.weights);
            }
            else
            {
                var dora = seat.Melds.SelectMany(m => m.Tiles).Sum(t =>
                    state.DoraIndicators.Count(i => TileHelpers.SameKind(TileDangerModel.DoraOf(i), t))
                    + (t.IsRedFive ? 1 : 0));
                values[seat.Seat] = this.weights.OpponentBaseValue
                    + this.weights.OpponentMeldValue * seat.Melds.Count
                    + (seat.Riichi ? this.weights.OpponentRiichiValue : 0)
                    + this.weights.OpponentDoraValue * dora;
            }
            for (var kind = 0; kind < 34; kind++)
            {
                var tile = TileHelpers.FromIndex(kind);
                dangers[seat.Seat, kind] = views is not null
                    ? this.danger.Estimate(tile, views[seat.Seat], visible, state.DoraIndicators)
                    : this.LegacyDanger(tile, seat, state, visible);
            }

            if (this.weights.YakumanHandling && views is not null)
                foreach (var (kind, name) in views[seat.Seat].YakumanTiles)
                    dangers[seat.Seat, kind] = this.YakumanDanger(dangers[seat.Seat, kind], kind, name, seat, state, visible, values[seat.Seat]);
            if (this.weights.DefenseModel == DefenseModel.V3 && views is not null)
            {
                if (KokushiRead(seat) && TileDangerModel.KokushiPossible(visible))
                {
                    probabilities[seat.Seat] = Math.Max(probabilities[seat.Seat], OpenDangerTable.Default.KokushiReadTenpai(seat.Discards.Count) ?? 0);
                    foreach (var kind in KokushiKinds)
                        dangers[seat.Seat, kind] = this.KokushiDanger(dangers[seat.Seat, kind], kind, seat, state, visible, values[seat.Seat]);
                }
            }
        }

        // The seat to defend against first: a riichi (the dealer's first), else the
        // highest tenpai-weighted value once it clears the threat floor.
        var primary = -1;
        var best = 0d;
        for (var seat = 1; seat <= 3; seat++)
        {
            var weight = (riichi[seat] ? 10.0 : probabilities[seat] >= this.weights.ThreatTenpaiFloor ? probabilities[seat] : 0)
                         * values[seat] * (seat == state.DealerSeat ? 1.4 : 1);
            if (weight > best)
            {
                best = weight;
                primary = seat;
            }
        }

        var liveSuji = new[] { -1, views?[1]?.LiveSuji ?? -1, views?[2]?.LiveSuji ?? -1, views?[3]?.LiveSuji ?? -1 };
        // Publish the complete table together; queries never see a half-updated model.
        Volatile.Write(ref this.model, new Model(probabilities, dangers, values, riichi, primary, liveSuji));
    }

    public double TenpaiProbability(int seat) => Volatile.Read(ref this.model).Probabilities[CheckSeat(seat)];

    public double Danger(Tile tile, int seat) => Volatile.Read(ref this.model).Dangers[CheckSeat(seat), TileHelpers.ToIndex(tile)].Probability;

    public DangerEstimate Explain(Tile tile, int seat) => Volatile.Read(ref this.model).Dangers[CheckSeat(seat), TileHelpers.ToIndex(tile)];

    public int PrimaryThreat() => Volatile.Read(ref this.model).PrimaryThreat;

    public double Value(int seat) => Volatile.Read(ref this.model).Values[CheckSeat(seat)];

    public int LiveSuji(int seat) => Volatile.Read(ref this.model).LiveSuji[CheckSeat(seat)];

    public string Profile(int seat) => this.weights.DefenseModel == DefenseModel.V3 ? this.profiles.Describe(CheckSeat(seat), this.weights) : string.Empty;

    public bool NoviceLike(int seat) => this.weights.DefenseModel == DefenseModel.V3 && this.profiles.NoviceLike(CheckSeat(seat), this.weights);

    public double NoviceProbability(int seat) => this.weights.DefenseModel == DefenseModel.V3 ? this.profiles.NoviceProbability(CheckSeat(seat)) : 0;

    public double Steadiness(int seat) => this.weights.DefenseModel == DefenseModel.Legacy ? 1 : this.profiles.Steadiness(CheckSeat(seat), this.weights);

    public double WinRateFactor(bool open) => this.weights.WinReveals && this.wins is not null ? this.wins.Factor(open) : 1;

    public void OurTenpai(bool open, double hazard) => this.wins?.Tenpai(open, hazard);

    public double ExpectedDealInCost(Tile tile)
    {
        var current = Volatile.Read(ref this.model);
        var kind = TileHelpers.ToIndex(tile);
        var cost = 0d;
        for (var seat = 1; seat <= 3; seat++)
            cost += current.Probabilities[seat] * current.Dangers[seat, kind].Probability * current.Values[seat];
        return cost;
    }

    // The tile that finishes a visible yakuman. Phoenix replays: the third dragon into a
    // tenpai seat with two dragon sets hit 2.7 % of the time for 19 600 on average (shousangen
    // included), and Doman applies pao to whoever feeds the pon. The danger becomes cost-
    // equivalent — probability × yakuman / the seat's usual value — so the budget, betaori and
    // expected-cost ranking all see a 32 000 tile for what it is.
    private DangerEstimate YakumanDanger(DangerEstimate current, int kind, string name, SeatState seat, StateSnapshot state, int[] visible, double seatValue)
    {
        if (current.Probability <= 0 || visible[kind] >= 4)
            return current;
        var measured = OpenDangerTable.Default.YakumanTileRate() ?? this.weights.YakumanTileRate;
        var p = Math.Max(current.Probability, measured);
        var yakuman = YakumanRoutes.Points(seat.Seat == state.DealerSeat);
        var equivalent = Math.Clamp(p * yakuman / Math.Max(1000, seatValue), 0, 1);
        var pao = visible[kind] <= 2 ? ", pao if it is pon'd" : string.Empty;
        return new DangerEstimate(equivalent, this.danger.RankOf(equivalent), $"yakuman/{name}",
            $"finishes {name} ({p:P1} × {yakuman} vs ~{seatValue:0}){pao}");
    }

    private static readonly int[] KokushiKinds = [0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33];

    // A closed seat that has thrown 8+ tiles with at most one terminal/honor among them is
    // keeping every orphan: the river shape tools/open_hand_danger.py counts as "kokushi-read".
    public static bool KokushiRead(SeatState seat) =>
        !seat.Melds.Any(m => m.IsOpen) && seat.Discards.Count >= 8 && seat.Discards.Count(t => t.IsTerminalOrHonor) <= 1;

    // Terminals and honors into a kokushi-shaped seat: the measured hit rate (any tenpai), with
    // the kokushi share of those hands priced as a yakuman, as a cost-equivalent danger.
    private DangerEstimate KokushiDanger(DangerEstimate current, int kind, SeatState seat, StateSnapshot state, int[] visible, double seatValue)
    {
        if (current.Probability <= 0 || visible[kind] >= 4)
            return current;
        var table = OpenDangerTable.Default;
        var p = Math.Max(current.Probability, table.KokushiTileRate() ?? current.Probability);
        var share = table.KokushiShare(seat.Discards.Count) ?? 0;
        var yakuman = YakumanRoutes.Points(seat.Seat == state.DealerSeat);
        var value = share * yakuman + (1 - share) * Math.Max(1000, seatValue);
        var equivalent = Math.Clamp(p * value / Math.Max(1000, seatValue), 0, 1);
        return new DangerEstimate(equivalent, this.danger.RankOf(equivalent), "yakuman/Kokushi Musou",
            $"kokushi-shaped river ({p:P1}, {share:P0} of its tenpai hands are kokushi)");
    }

    // v1.3 constants (kept for A/B runs behind DefenseModel.Legacy): flat class rates,
    // a single suji discount, kabe only as a full blockade.
    private DangerEstimate LegacyDanger(Tile tile, SeatState seat, StateSnapshot state, int[] visible)
    {
        if (seat.Discards.Any(t => TileHelpers.SameKind(t, tile)) || PassedAfterRiichi(tile, seat, state))
            return new DangerEstimate(Math.Clamp(this.weights.GenbutsuDanger, 0, 1), DangerRank.S, "genbutsu", "legacy");

        double danger;
        if (tile.IsHonor)
        {
            danger = this.weights.HonorDanger * (4 - Math.Min(4, visible[TileHelpers.ToIndex(tile)])) / 4;
            return new DangerEstimate(Math.Clamp(danger, 0, 1), this.danger.RankOf(danger), "honor", "legacy");
        }

        danger = tile.IsTerminal ? this.weights.TerminalDanger
            : tile.Number is 2 or 8 ? this.weights.EdgeDanger : this.weights.MiddleDanger;
        bool Discarded(int number) => seat.Discards.Any(t => t.Suit == tile.Suit && t.Number == number);
        var suji = tile.Number <= 3 ? Discarded(tile.Number + 3)
            : tile.Number >= 7 ? Discarded(tile.Number - 3)
            : Discarded(tile.Number - 3) && Discarded(tile.Number + 3);
        if (suji)
            danger *= this.weights.SujiDiscount;
        if ((tile.Number > 1 && visible[TileHelpers.ToIndex(new Tile(tile.Suit, tile.Number - 1))] >= 4)
            || (tile.Number < 9 && visible[TileHelpers.ToIndex(new Tile(tile.Suit, tile.Number + 1))] >= 4))
            danger *= this.weights.KabeDiscount;
        return new DangerEstimate(Math.Clamp(danger, 0, 1), this.danger.RankOf(danger), suji ? "suji" : "non-suji", "legacy");
    }

    private static bool PassedAfterRiichi(Tile tile, SeatState seat, StateSnapshot state)
    {
        if (!seat.Riichi)
            return false;
        if (state.CallFromSeat is >= 0 and <= 3 && state.CallFromSeat != seat.Seat
            && state.CallTile is { } latest && TileHelpers.SameKind(latest, tile))
            return true;
        // No global discard timestamps are available. Round indices work only before calls skip turns.
        if (seat.RiichiDiscardIndex < 0 || state.Seats.Any(s => s.Melds.Any(m => m.IsOpen)) || state.IsOpen)
            return false;
        var declaration = seat.RiichiDiscardIndex * 4 + (seat.Seat - state.DealerSeat + 4) % 4;
        return state.Seats.Any(s => s.Discards.Where((_, index) =>
                index * 4 + (s.Seat - state.DealerSeat + 4) % 4 > declaration)
            .Any(t => TileHelpers.SameKind(t, tile)));
    }

    private static int CheckSeat(int seat) => seat is >= 1 and <= 3
        ? seat : throw new ArgumentOutOfRangeException(nameof(seat));

    private sealed record Model(double[] Probabilities, DangerEstimate[,] Dangers, double[] Values, bool[] Riichi, int PrimaryThreat, int[] LiveSuji);
}
