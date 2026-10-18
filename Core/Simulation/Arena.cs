using MahjongHater.Core.Policy;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Simulation;

// One seat at an arena table: anything that turns an observation into a legal action.
public interface IArenaSeat
{
    string Name { get; }

    SimAction Choose(SimulationObservation observation, IReadOnlyList<SimAction> legal, CancellationToken ct);
}

// A live-plugin policy (DecisionPolicy, learned-guarded, ...) at the table. Wins are always
// taken, exactly as a confirmed game offer is live.
public sealed class PolicySeat(string name, IPolicy policy) : IArenaSeat
{
    public string Name { get; } = name;

    public SimAction Choose(SimulationObservation observation, IReadOnlyList<SimAction> legal, CancellationToken ct)
    {
        if (legal.Count == 1)
            return legal[0];
        if (legal.FirstOrDefault(a => a.Kind is SimActionKind.Tsumo or SimActionKind.Ron) is { } win)
            return win;
        var choice = policy.Choose(observation.Snapshot, ct);
        return SimulationPolicy.Match(choice, legal)
               ?? legal.FirstOrDefault(a => a.Kind == SimActionKind.Pass)
               ?? legal.First(a => a.Kind == SimActionKind.Discard);
    }
}

// The FF14 Novice-queue opponent as seen on 2026-10-18 (early middles 50-83 %): plays for
// tile efficiency only, never folds, takes any call that keeps a yaku, declares riichi on any
// wait, and throws a random tile part of the time.
public sealed class NoviceSeat(int seed, double randomDiscard = 0.35, double callRate = 0.75) : IArenaSeat
{
    private readonly Random random = new(seed);
    private readonly HandAnalyzer analyzer = new();
    private readonly CallPolicy calls = new();

    public string Name => "novice";

    public SimAction Choose(SimulationObservation observation, IReadOnlyList<SimAction> legal, CancellationToken ct)
    {
        if (legal.Count == 1)
            return legal[0];
        if (legal.FirstOrDefault(a => a.Kind is SimActionKind.Tsumo or SimActionKind.Ron) is { } win)
            return win;
        var state = observation.Snapshot;
        var pass = legal.FirstOrDefault(a => a.Kind == SimActionKind.Pass);
        if (legal.Any(a => a.Kind is SimActionKind.Pon or SimActionKind.Chi) && pass is not null)
        {
            if (this.random.NextDouble() < callRate && this.calls.Viable(state, ct).FirstOrDefault() is { } call
                && SimulationPolicy.Match(new ActionChoice(call.Kind, state.CallTile, call.Meld, "", [], []), legal) is { } matched)
                return matched;
            return pass;
        }

        var discards = legal.Where(a => a.Kind == SimActionKind.Discard).ToList();
        if (discards.Count == 0)
            return pass ?? legal[0];
        string tile;
        if (this.random.NextDouble() < randomDiscard)
            tile = discards[this.random.Next(discards.Count)].Tile!;
        else
        {
            var result = this.analyzer.Analyze(PolicyInput.MakeHand(state), PolicyInput.Context(state), ct);
            tile = result.IsValid && result.BestDiscard is { } best ? best.ToString() : discards[0].Tile!;
        }

        if (legal.FirstOrDefault(a => a.Kind == SimActionKind.Riichi && a.Tile == tile) is { } riichi)
            return riichi;
        return discards.FirstOrDefault(a => a.Tile == tile) ?? discards[0];
    }
}

// What one seat did over a match.
public sealed record ArenaSeatResult(string Name, int Score, int Placement, int Hands, int Wins, int WinPoints,
    int DealIns, int DealInPoints, int TsumoLosses, int Riichis);

public static class ArenaRunner
{
    // A full match with one policy per physical seat (MatchRunner's loop and settlement).
    public static ArenaSeatResult[] Play(SimulationRules rules, int seed, IReadOnlyList<IArenaSeat> seats, CancellationToken ct = default)
    {
        if (seats.Count != 4)
            throw new ArgumentException("An arena table needs four seats.");
        var random = new Random(seed);
        var initialDealer = random.Next(4);
        var dealer = initialDealer;
        var scores = Enumerable.Repeat(rules.StartingScore, 4).ToArray();
        int sticks = 0, honba = 0, round = 0;
        var wins = new int[4]; var winPoints = new int[4]; var dealIns = new int[4]; var dealInPoints = new int[4];
        var tsumoLosses = new int[4]; var riichis = new int[4];
        for (var handIndex = 0; handIndex < rules.MaxHandsPerMatch; handIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var game = RiichiSimulator.Deal(rules, random, dealer, round, scores, honba, sticks, initialDealer);
            while (game.Phase != SimPhase.Ended)
            {
                var legal = RiichiSimulator.Legal(game);
                var action = seats[game.Actor].Choose(SimulationObservation.Observe(game, legal), legal, ct);
                RiichiSimulator.Apply(game, action, validate: true);
            }

            var result = game.Result!;
            for (var s = 0; s < 4; s++)
                if (game.Players[s].Riichi)
                    riichis[s]++;
            if (result.End is HandEnd.Ron or HandEnd.TripleRon or HandEnd.Tsumo)
            {
                foreach (var w in result.Winners)
                {
                    wins[w]++;
                    winPoints[w] += result.Deltas[w];
                }

                // The payer: the non-winner who lost most once their own riichi stick is put back.
                var losers = Enumerable.Range(0, 4).Where(s => !result.Winners.Contains(s))
                    .Select(s => (Seat: s, Paid: -(result.Deltas[s] + (game.Players[s].Riichi ? 1000 : 0)))).ToList();
                if (result.End == HandEnd.Tsumo)
                    foreach (var (s, paid) in losers.Where(l => l.Paid > 0))
                        tsumoLosses[s]++;
                else if (losers.MaxBy(l => l.Paid) is { Paid: > 0 } payer)
                {
                    dealIns[payer.Seat]++;
                    dealInPoints[payer.Seat] += payer.Paid;
                }
            }

            scores = game.Players.Select(p => p.Score).ToArray();
            sticks = game.RiichiSticks;
            var win = result.End is HandEnd.Ron or HandEnd.Tsumo or HandEnd.TripleRon;
            var top = MatchRunner.Rank(scores, initialDealer)[0];
            var final = scores.Any(s => s < 0) || round >= rules.HandsInMatch - 1
                && (!result.DealerRepeats || top == dealer && (win || result.Tenpai[dealer]));
            if (final)
            {
                scores[top] += 1000 * sticks;
                var order = MatchRunner.Rank(scores, initialDealer);
                return Enumerable.Range(0, 4).Select(s => new ArenaSeatResult(seats[s].Name, scores[s], Array.IndexOf(order, s) + 1,
                    handIndex + 1, wins[s], winPoints[s], dealIns[s], dealInPoints[s], tsumoLosses[s], riichis[s])).ToArray();
            }

            honba = win && !result.DealerRepeats ? 0 : honba + 1;
            if (!result.DealerRepeats)
            {
                dealer = (dealer + 1) % 4;
                round++;
            }
        }

        throw new InvalidOperationException("Arena match hand limit reached.");
    }

    // Duplicate format: every deal seed is played four times with the candidate rotated
    // through the seats and fresh opponents elsewhere, so wall luck cancels between arms
    // that share the seeds. Returns the candidate's result for every (seed, seat).
    public static IReadOnlyList<ArenaSeatResult> Duplicate(SimulationRules rules, IReadOnlyList<int> seeds,
        Func<IArenaSeat> candidate, Func<int, int, IArenaSeat> opponent, int threads, Action<int>? progress = null, CancellationToken ct = default)
    {
        var jobs = seeds.SelectMany(seed => Enumerable.Range(0, 4).Select(seat => (Seed: seed, Seat: seat))).ToArray();
        var results = new ArenaSeatResult[jobs.Length];
        var done = 0;
        Parallel.For(0, jobs.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ct }, i =>
        {
            var (seed, seat) = jobs[i];
            var table = Enumerable.Range(0, 4).Select(s => s == seat ? candidate() : opponent(seed, s)).ToArray();
            results[i] = Play(rules, seed, table, ct)[seat];
            progress?.Invoke(Interlocked.Increment(ref done));
        });
        return results;
    }
}
