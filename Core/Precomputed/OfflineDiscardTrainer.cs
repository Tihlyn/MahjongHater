using MahjongHater.Core.Policy;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Precomputed;

public sealed record TrainingOptions(int Iterations = 2048, int Horizon = 3, int Seed = 1, double Exploration = 1.4);

// Offline only. UCT decision nodes branch on sampled observations (our next draw).
// First visits expand the tree and use a guideline rollout; later visits select
// by UCB. Returns and second moments are backed up along the selected path.
// This proxy does NOT simulate full hidden hands, opponent calls or declarations.
public sealed class OfflineDiscardTrainer
{
    private readonly PolicyWeights weights;

    public OfflineDiscardTrainer(PolicyWeights? weights = null) => this.weights = weights ?? PolicyWeights.Default;

    public PolicyArtifact Train(IEnumerable<StateSnapshot> situations, TrainingOptions options, CancellationToken ct = default)
    {
        if (options.Iterations < 112 || options.Horizon is < 1 or > 16
            || !double.IsFinite(options.Exploration) || options.Exploration < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Use at least 112 iterations, horizon 1..16 and finite nonnegative exploration.");
        var profile = BeliefState.Profile(this.weights);
        var entries = new Dictionary<StateIdentity, PolicyEntry>();
        foreach (var state in situations)
        {
            ct.ThrowIfCancellationRequested();
            if (!DiscardActionSpace.Supports(state))
                throw new ArgumentException("Training requires a healthy, verified closed-hand draw with only discard legal.", nameof(situations));
            var belief = BeliefState.Capture(state, new OpponentModel(this.weights));
            var identity = IncrementalStateKey.Create(state, belief, profile);
            if (entries.ContainsKey(identity))
                continue;
            var search = new Search(state, belief, this.weights, options, ct);
            entries.Add(identity, new PolicyEntry(identity, search.Run()));
        }
        ct.ThrowIfCancellationRequested();
        if (entries.Count == 0)
            throw new ArgumentException("The training corpus contains no supported situations.", nameof(situations));
        return new PolicyArtifact(PolicyTable.Schema, BeliefState.ModelVersion, profile, options.Seed,
            options.Iterations, options.Horizon, entries.Values.ToArray()) { Exploration = options.Exploration };
    }

    private sealed class Search
    {
        private readonly StateSnapshot root;
        private readonly IReadOnlyList<OpponentBelief> belief;
        private readonly PolicyWeights weights;
        private readonly TrainingOptions options;
        private readonly CancellationToken ct;
        private readonly Random random;
        private readonly Dictionary<string, Node> nodes = new(StringComparer.Ordinal);
        private readonly int[] initialUnseen;

        public Search(StateSnapshot root, IReadOnlyList<OpponentBelief> belief, PolicyWeights weights, TrainingOptions options, CancellationToken ct)
        {
            this.root = root;
            this.belief = belief;
            this.weights = weights;
            this.options = options;
            this.ct = ct;
            this.random = new Random(options.Seed);
            // 37 physical types: ordinary 34 kinds plus the three red fives.
            // Prototype assumes one red five in each suit, matching the current game.
            this.initialUnseen = Enumerable.Repeat(4, 34).Concat(new[] { 1, 1, 1 }).ToArray();
            foreach (var kind in new[] { 4, 13, 22 })
                this.initialUnseen[kind]--;
            foreach (var tile in root.Hand.Concat(root.SeenForAnalyzer()).Concat(root.DoraIndicators))
                if (--this.initialUnseen[Physical(tile)] < 0)
                    throw new ArgumentException("Snapshot contains more visible copies than the tile set permits.");
            if (root.WallRemaining is < 1 or > 70 || root.WallRemaining > this.initialUnseen.Sum())
                throw new ArgumentException("Invalid remaining wall.");
        }

        public StoredDiscard[] Run()
        {
            var hand = this.root.Hand.Order().ToList();
            var node = this.GetNode(hand, this.initialUnseen, this.root.WallRemaining);
            for (var i = 0; i < this.options.Iterations; i++)
            {
                this.ct.ThrowIfCancellationRequested();
                this.Visit(hand, (int[])this.initialUnseen.Clone(), this.root.WallRemaining, this.options.Horizon, isRoot: true);
            }
            return node.Edges.Select(edge => new StoredDiscard(edge.Tile.ToString(), edge.Visits, edge.Mean,
                edge.Visits > 1 ? edge.M2 / (edge.Visits - 1) : 0, edge.Shanten, edge.Ukeire,
                edge.Waits.Select(t => t.ToString()).ToArray())).ToArray();
        }

        private double Visit(List<Tile> hand, int[] unseen, int wall, int depth, bool isRoot = false)
        {
            this.ct.ThrowIfCancellationRequested();
            var node = this.GetNode(hand, unseen, wall);
            // Give every root alternative enough actual samples for runtime coverage.
            var floor = isRoot ? 8 : 1;
            var edge = node.Edges.Where(e => e.Visits < floor).OrderBy(e => e.Visits)
                .ThenByDescending(e => e.Prior).ThenBy(e => e.Tile).FirstOrDefault()
                ?? node.Edges.MaxBy(e => e.Mean / 48000
                    + this.options.Exploration * Math.Sqrt(Math.Log(Math.Max(1, node.Visits)) / e.Visits)
                    + 0.05 * e.Prior / (1 + e.Visits))!;
            var firstVisit = edge.Visits == 0;
            var value = this.Step(hand, unseen, wall, depth, edge.Tile, rollout: firstVisit);
            edge.Visits++;
            var delta = value - edge.Mean;
            edge.Mean += delta / edge.Visits;
            edge.M2 += delta * (value - edge.Mean);
            node.Visits++;
            return value;
        }

        private double Step(List<Tile> hand, int[] unseen, int wall, int depth, Tile discard, bool rollout)
        {
            this.ct.ThrowIfCancellationRequested();
            double loss = 0;
            foreach (var b in this.belief)
                if (this.random.NextDouble() < b.Tenpai * b.Danger[TileHelpers.ToIndex(discard)])
                    loss += b.Value + 300 * this.root.Honba;
            if (loss > 0)
                return -Math.Min(48000, loss);

            var kept = hand.ToList();
            kept.Remove(discard);
            // WallRemaining is after our draw. Three other draws precede our next one.
            if (wall <= 3)
                return 0; // No noten settlement is modeled in this first proxy.
            if (depth <= 1)
                return this.LeafValue(kept, unseen, wall);

            var nextDraw = this.Draw(unseen);
            if (nextDraw is null)
                return 0;
            kept.Add(nextDraw.Value);
            kept.Sort();
            var points = this.WinningPoints(kept, nextDraw.Value, wall == 4);
            if (points > 0)
                return Math.Min(48000, points + 300 * this.root.Honba + 1000 * this.root.RiichiSticks);
            if (!rollout)
                return this.Visit(kept, unseen, wall - 4, depth - 1);
            var best = this.GetNode(kept, unseen, wall - 4).Edges.MaxBy(e => e.Prior)!;
            return this.Step(kept, unseen, wall - 4, depth - 1, best.Tile, rollout: true);
        }

        private Node GetNode(List<Tile> hand, int[] unseen, int wall)
        {
            var key = string.Join(",", hand.Select(Physical).Order()) + ":" + string.Join(",", unseen) + ":" + wall;
            if (this.nodes.TryGetValue(key, out var node))
                return node;
            var counts = new int[34];
            foreach (var tile in hand)
                counts[TileHelpers.ToIndex(tile)]++;
            var available = Availability(unseen);
            var evaluated = Shanten.EvaluateDiscards(counts, 0, available, this.ct).ToDictionary(e => TileHelpers.ToIndex(e.Discard));
            var edges = hand.Distinct().Order().Select(tile =>
            {
                var e = evaluated[TileHelpers.ToIndex(tile)];
                var danger = this.belief.Sum(b => b.Tenpai * b.Danger[TileHelpers.ToIndex(tile)] * b.Value);
                // Guidelines steer expansion/rollout without inventing observations.
                var prior = -e.ShantenAfter + e.Ukeire / 200.0 - danger / 48000 - (tile.IsRedFive ? 0.02 : 0);
                var waits = e.ShantenAfter == 0 ? TileHelpers.AllTileTypes.Where(t =>
                    (e.UsefulKindsMask & (1UL << TileHelpers.ToIndex(t))) != 0).ToArray() : [];
                return new Edge(tile, e.ShantenAfter, e.Ukeire, waits, prior);
            }).ToArray();
            node = new Node(edges);
            this.nodes.Add(key, node);
            return node;
        }

        private double LeafValue(List<Tile> hand, int[] unseen, int wall)
        {
            var shanten = Shanten.Calculate(hand);
            var available = Availability(unseen);
            var ukeire = Shanten.GetUsefulTiles(hand).Sum(t => available[TileHelpers.ToIndex(t)]);
            var dora = hand.Sum(t => (t.IsRedFive ? 1 : 0) + this.root.DoraIndicators.Count(i => TileHelpers.SameKind(TileDangerModel.DoraOf(i), t)));
            var chance = HandValue.WinProbability(shanten, ukeire, 0, wall, this.weights);
            return Math.Clamp(chance * HandValue.EstimatePoints(this.root, dora, this.weights), 0, 48000);
        }

        private int WinningPoints(List<Tile> tiles, Tile draw, bool lastDraw)
        {
            if (Shanten.Calculate(tiles) >= 0)
                return 0;
            var hand = new Hand { WinningTile = draw, WinMethod = WinMethod.Tsumo,
                SeatWind = this.root.SeatWind, RoundWind = this.root.RoundWind, IsHaitei = lastDraw };
            hand.ClosedTiles.AddRange(tiles);
            hand.AkadoraCount = tiles.Count(t => t.IsRedFive);
            hand.DoraCount = tiles.Sum(t => this.root.DoraIndicators.Count(i => TileHelpers.SameKind(TileDangerModel.DoraOf(i), t)));
            var detector = new YakuDetector(this.root.Ruleset);
            var best = 0;
            foreach (var d in HandDecomposer.GetWinningDecompositions(hand))
            {
                this.ct.ThrowIfCancellationRequested();
                var yaku = detector.Detect(hand, d.Melds, d.Pair, d.Wait);
                if (yaku.Sum(y => y.IsYakuman ? 13 : y.Han) < Math.Max(1, this.weights.MinHanDoman))
                    continue;
                var fu = FuCalculator.Calculate(hand, d.Melds, d.Pair, d.Wait, 4);
                best = Math.Max(best, new ScoringEngine().Calculate(hand, yaku, fu, this.root.DealerSeat == 0).TotalTsumoPayment);
            }
            return best;
        }

        private Tile? Draw(int[] unseen)
        {
            var count = unseen.Sum();
            if (count == 0)
                return null;
            var pick = this.random.Next(count);
            for (var i = 0; i < unseen.Length; i++)
            {
                pick -= unseen[i];
                if (pick >= 0)
                    continue;
                unseen[i]--;
                return i < 34 ? TileHelpers.FromIndex(i) : new Tile((TileSuit)(i - 34), 5, true);
            }
            throw new InvalidOperationException("Unseen tile counts are inconsistent.");
        }

        private static int Physical(Tile tile) => tile.IsRedFive ? 34 + (int)tile.Suit : TileHelpers.ToIndex(tile);
        private static int[] Availability(int[] unseen)
        {
            var counts = unseen.Take(34).ToArray();
            for (var suit = 0; suit < 3; suit++)
                counts[suit * 9 + 4] += unseen[34 + suit];
            return counts;
        }

        private sealed class Node(Edge[] edges)
        {
            public Edge[] Edges { get; } = edges;
            public int Visits { get; set; }
        }

        private sealed class Edge(Tile tile, int shanten, int ukeire, Tile[] waits, double prior)
        {
            public Tile Tile { get; } = tile;
            public int Shanten { get; } = shanten;
            public int Ukeire { get; } = ukeire;
            public Tile[] Waits { get; } = waits;
            public double Prior { get; } = prior;
            public int Visits { get; set; }
            public double Mean { get; set; }
            public double M2 { get; set; }
        }
    }
}
