namespace MahjongHater.Core.Simulation;

public sealed record MatchResult(int Hands, int Decisions, int[] Scores, int[] Placement, int InitialDealer, Dictionary<HandEnd, int> Endings);

public static class MatchRunner
{
    public static MatchResult Play(SimulationRules rules, int seed, SimulationPolicy policy, CancellationToken ct = default,
        Action<SimulationObservation, IReadOnlyList<SimAction>>? onDecision = null, bool checkInvariants = true)
    {
        var random = new Random(seed);
        var initialDealer = random.Next(4);
        var dealer = initialDealer;
        var scores = Enumerable.Repeat(rules.StartingScore, 4).ToArray();
        var sticks = 0;
        var honba = 0;
        var round = 0;
        var decisions = 0;
        var endings = new Dictionary<HandEnd, int>();
        for (var handIndex = 0; handIndex < rules.MaxHandsPerMatch; handIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var game = RiichiSimulator.Deal(rules, random, dealer, round, scores, honba, sticks, initialDealer);
            while (game.Phase != SimPhase.Ended)
            {
                ct.ThrowIfCancellationRequested();
                var legal = RiichiSimulator.Legal(game);
                var observation = SimulationObservation.Observe(game, legal);
                onDecision?.Invoke(observation, legal);
                var action = policy.Choose(observation, legal, ct);
                RiichiSimulator.Apply(game, action, validate: false); // Chosen from the authoritative list just computed.
                if (checkInvariants)
                    RiichiSimulator.ValidateConservation(game, 4 * rules.StartingScore);
            }
            RiichiSimulator.ValidateConservation(game, 4 * rules.StartingScore);
            scores = game.Players.Select(p => p.Score).ToArray();
            sticks = game.RiichiSticks;
            decisions += game.DecisionCount;
            var result = game.Result!;
            endings[result.End] = endings.GetValueOrDefault(result.End) + 1;
            var win = result.End is HandEnd.Ron or HandEnd.Tsumo;
            var top = Rank(scores, initialDealer)[0];
            var final = scores.Any(s => s < 0) || round >= rules.HandsInMatch - 1 &&
                (!result.DealerRepeats || top == dealer && (win || result.Tenpai[dealer]));
            if (final)
            {
                scores[top] += 1000 * sticks;
                if (scores.Sum() != 4 * rules.StartingScore)
                    throw new InvalidOperationException("Match settlement lost points.");
                var order = Rank(scores, initialDealer);
                return new MatchResult(handIndex + 1, decisions, scores, Enumerable.Range(0, 4).Select(s => Array.IndexOf(order, s) + 1).ToArray(), initialDealer, endings);
            }
            honba = win && !result.DealerRepeats ? 0 : honba + 1;
            if (!result.DealerRepeats)
            {
                dealer = (dealer + 1) % 4;
                round++;
            }
        }
        throw new InvalidOperationException("Match hand limit reached; no truncated match was accepted as training data.");
    }

    public static int[] Rank(int[] scores, int initialDealer) => Enumerable.Range(0, 4)
        .OrderByDescending(s => scores[s]).ThenBy(s => (s - initialDealer + 4) % 4).ToArray();
}
