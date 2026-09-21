using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Precomputed;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Simulation;

public sealed record SearchOptions
{
    public int Iterations { get; init; } = 256;
    public int Particles { get; init; } = 16;
    public int MinimumVisits { get; init; } = 8;
    public int MaximumTreeNodes { get; init; } = 4096;
    public int BeliefSwapBudget { get; init; } = 12000;
    public double Exploration { get; init; } = 1.4;
    public double PlacementWeight { get; init; }
    public SimulationPolicyMode RolloutPolicy { get; init; } = SimulationPolicyMode.Guideline;
    public void Validate()
    {
        if (this.Iterations < 1 || this.Particles is < 1 or > 1024 || this.MinimumVisits < 1 || this.MaximumTreeNodes < 1
            || this.BeliefSwapBudget < 1 || !double.IsFinite(this.Exploration) || this.Exploration < 0
            || !double.IsFinite(this.PlacementWeight) || Math.Abs(this.PlacementWeight) > 100000)
            throw new ArgumentException("Invalid information-set search options.");
    }
}

public sealed record ActionStatistics(SimAction Action, int Visits, double MeanUtility, double UtilityM2, double MeanScore,
    int Wins, int DealIns, int Draws, int Shanten, int Ukeire, string[] Waits);
public sealed record SimulationRecord(string Profile, StateIdentity Exact, string AbstractKey, string Snapshot,
    int Seed, int Iterations, int Particles, ActionStatistics[] Actions);

public static class StrategicAbstraction
{
    // Experimental grouping for research/coverage reports. Exact structural hand,
    // physical melds/dora/actions stay fixed; contextual signals are discretized.
    // This is a feature key, never a distance between cryptographic hashes.
    public static string Key(StateSnapshot s, IReadOnlyList<OpponentBelief> beliefs, IReadOnlyList<SimAction> legal, string profile)
    {
        var visible = new int[34];
        foreach (var t in s.Hand.Concat(s.SeenForAnalyzer()).Concat(s.OurMelds.SelectMany(m => m.Tiles)).Concat(s.DoraIndicators))
            visible[TileHelpers.ToIndex(t)]++;
        var features = JsonSerializer.Serialize(new
        {
            Version = "strategy-buckets-v1", profile,
            Hand = s.Hand.Select(t => t.ToString()).Order(StringComparer.Ordinal).ToArray(),
            Melds = s.OurMelds.Select(m => $"{m.Type}:{m.IsOpen}:{string.Join(",", m.Tiles.Order())}").Order().ToArray(),
            Dora = s.DoraIndicators.Select(t => t.ToString()).ToArray(), s.RoundWind, s.SeatWind, s.DealerSeat, s.OurRiichi,
            s.Ruleset, s.HandNumber, Wall = s.WallRemaining / 8, Turn = s.Turn / 4, s.Honba, s.RiichiSticks,
            Scores = s.Seats.Select(p => (p.Score - s.Us.Score) / 4000).ToArray(),
            Seen = visible.Select(n => Math.Min(4, n)).ToArray(),
            Threats = beliefs.Select(b => new { b.Seat, Tenpai = (int)(b.Tenpai * 4), Value = (int)(b.Value / 2000),
                Danger = s.Hand.Distinct().Order().Select(t => (int)(b.Danger[TileHelpers.ToIndex(t)] * 20)).ToArray() }).ToArray(),
            Actions = legal.Select(a => a.Key).Order(StringComparer.Ordinal).ToArray(),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(features)));
    }
}

public sealed class InformationSetSearch(PolicyWeights? weights = null)
{
    private readonly PolicyWeights weights = weights ?? PolicyWeights.Default;

    public SimulationRecord Train(SimulationObservation observation, SearchOptions options, int seed, CancellationToken ct = default)
    {
        options.Validate();
        var random = new Random(seed);
        var sampler = new BeliefSampler(this.weights);
        var particles = new SimGame[options.Particles];
        for (var i = 0; i < particles.Length; i++)
            particles[i] = sampler.Sample(observation, random, ct, options.BeliefSwapBudget);
        var rootLegal = RiichiSimulator.Legal(particles[0]);
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        var rootKey = SimulationObservation.Observe(particles[0], rootLegal).InformationKey();
        var root = new Node(rootLegal);
        nodes.Add(rootKey, root);
        var iterations = Math.Max(options.Iterations, checked(options.MinimumVisits * rootLegal.Count));
        var policy = new SimulationPolicy(options.RolloutPolicy, this.weights);
        var startScore = observation.Snapshot.Us.Score;
        var startingRank = Array.IndexOf(MatchRunner.Rank(observation.Players.Select(p => p.Score).ToArray(), observation.InitialDealer), 0);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            var game = particles[random.Next(particles.Length)].Clone();
            BeliefSampler.ReshuffleHiddenWall(game, random);
            var path = new List<(Node Node, Edge Edge)>();
            var rollout = false;
            var first = true;
            while (game.Phase != SimPhase.Ended)
            {
                ct.ThrowIfCancellationRequested();
                var legal = RiichiSimulator.Legal(game);
                var view = SimulationObservation.Observe(game, legal);
                SimAction selected;
                if (game.Actor == 0 && !rollout)
                {
                    var key = first ? rootKey : view.InformationKey();
                    if (!nodes.TryGetValue(key, out var node))
                    {
                        if (nodes.Count >= options.MaximumTreeNodes)
                        {
                            rollout = true;
                            selected = policy.Choose(view, legal, ct);
                            RiichiSimulator.Apply(game, selected, validate: false);
                            continue;
                        }
                        node = new Node(legal);
                        nodes.Add(key, node);
                    }
                    if (!node.Edges.Select(e => e.Action).ToHashSet().SetEquals(legal))
                        throw new InvalidOperationException("Legal actions changed inside an information set.");
                    var minimum = first ? options.MinimumVisits : 1;
                    var edge = node.Edges.Where(e => e.Visits < minimum).OrderBy(e => e.Visits).FirstOrDefault()
                        ?? node.Edges.MaxBy(e => e.Mean / 48000 + options.Exploration * Math.Sqrt(Math.Log(Math.Max(1, node.Visits)) / e.Visits))!;
                    path.Add((node, edge));
                    selected = edge.Action;
                    rollout = edge.Visits == 0;
                    first = false;
                }
                else
                    selected = policy.Choose(view, legal, ct);
                RiichiSimulator.Apply(game, selected, validate: false);
            }
            RiichiSimulator.ValidateConservation(game, observation.Players.Sum(p => p.Score) + 1000 * observation.Snapshot.RiichiSticks);
            var score = game.Players[0].Score - startScore;
            var rank = Array.IndexOf(MatchRunner.Rank(game.Players.Select(p => p.Score).ToArray(), game.InitialDealer), 0);
            var utility = score + options.PlacementWeight * (startingRank - rank);
            foreach (var (node, edge) in path)
            {
                node.Visits++;
                edge.Visits++;
                var delta = utility - edge.Mean;
                edge.Mean += delta / edge.Visits;
                edge.M2 += delta * (utility - edge.Mean);
                edge.MeanScore += (score - edge.MeanScore) / edge.Visits;
                if (game.Result!.End is HandEnd.Ron or HandEnd.Tsumo && game.Result.Winners.Contains(0))
                    edge.Wins++;
                if (game.Result.End == HandEnd.Ron && game.PendingSeat == 0)
                    edge.DealIns++;
                if (game.Result.End is not (HandEnd.Ron or HandEnd.Tsumo))
                    edge.Draws++;
            }
        }
        var state = observation.Snapshot;
        var beliefs = BeliefState.Capture(state, new OpponentModel(this.weights));
        var profile = observation.Rules.Profile(this.weights, options.PlacementWeight);
        return new SimulationRecord(profile, IncrementalStateKey.Create(state, beliefs, profile), StrategicAbstraction.Key(state, beliefs, rootLegal, profile),
            SnapshotJson.Serialize(state), seed, iterations, particles.Length, root.Edges.Select(e => Statistics(e, state)).ToArray());
    }

    private static ActionStatistics Statistics(Edge edge, StateSnapshot state)
    {
        var hand = state.Hand.ToList();
        if (edge.Action.Kind is SimActionKind.Discard or SimActionKind.Riichi)
            hand.Remove(Tile.Parse(edge.Action.Tile!));
        var shanten = Shanten.Calculate(hand, state.OurMelds.Count);
        var waits = hand.Count + 3 * state.OurMelds.Count == 13 && shanten == 0 ? Shanten.GetUsefulTiles(hand, state.OurMelds.Count) : [];
        var visible = new int[34];
        foreach (var t in state.Hand.Concat(state.SeenForAnalyzer()).Concat(state.OurMelds.SelectMany(m => m.Tiles)).Concat(state.DoraIndicators))
            visible[TileHelpers.ToIndex(t)]++;
        var ukeire = hand.Count + 3 * state.OurMelds.Count == 13
            ? Shanten.GetUsefulTiles(hand, state.OurMelds.Count).Sum(t => Math.Max(0, 4 - visible[TileHelpers.ToIndex(t)])) : 0;
        return new ActionStatistics(edge.Action, edge.Visits, edge.Mean, edge.M2, edge.MeanScore, edge.Wins, edge.DealIns, edge.Draws,
            shanten, ukeire, waits.Select(t => t.ToString()).ToArray());
    }
    private sealed class Node(IReadOnlyList<SimAction> legal)
    {
        public Edge[] Edges { get; } = legal.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => new Edge(a)).ToArray();
        public int Visits;
    }
    private sealed class Edge(SimAction action)
    {
        public SimAction Action { get; } = action;
        public int Visits;
        public double Mean, M2, MeanScore;
        public int Wins, DealIns, Draws;
    }
}
