using System.Diagnostics;
using MahjongHater.Core.Learning;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Simulation;

// Duplicate-format A/B of live policies against simulated opponents (docs/research/DEFENSE_V3_2026_10_18.md).
internal static class ArenaCommands
{
    // novice: worst-case novices; learned: the Phoenix-trained network; mixed: both, alternating
    // seats; live: each opponent a casual novice one time in three (the 2026-10-18 human match),
    // else the network.
    public const string Usage = "arena <seeds> <novice|learned|mixed|live> <model.json|-> [arms=heuristic-V2,heuristic-V3] [threads] [hands=4|8] [first-seed=1]";

    public static bool Handles(string command) => command == "arena";

    public static int Run(string[] args, CancellationToken ct)
    {
        if (args.Length < 4) throw new ArgumentException(Usage);
        var seedCount = int.Parse(args[1]);
        var field = args[2];
        var model = args[3] != "-" ? LearnedModel.Load(args[3]) : null;
        var arms = (args.Length > 4 ? args[4] : "heuristic-V2,heuristic-V3").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var threads = args.Length > 5 ? int.Parse(args[5]) : Math.Max(1, Environment.ProcessorCount - 1);
        var rules = new SimulationRules { HandsInMatch = args.Length > 6 ? int.Parse(args[6]) : 4 };
        var first = args.Length > 7 ? int.Parse(args[7]) : 1;
        if (field is not ("novice" or "learned" or "mixed" or "live")) throw new ArgumentException("opponents must be novice, learned, mixed or live");
        if ((field != "novice" || arms.Any(a => a.StartsWith("guarded", StringComparison.Ordinal))) && model is null)
            throw new ArgumentException("learned opponents and guarded arms need a model");
        if (model is not null && (model.Rules.HandsInMatch != rules.HandsInMatch))
            Console.Error.WriteLine($"note: the model was trained for {model.Rules.HandsInMatch}-hand matches; the arena plays {rules.HandsInMatch}.");
        var seeds = Enumerable.Range(first, seedCount).ToArray();

        IArenaSeat Opponent(int seed, int seat) => MakeOpponent(field, model, seed, seat).Seat;

        Console.WriteLine($"arena: {seedCount} seeds x 4 seats, {rules.HandsInMatch}-hand matches, opponents={field}, threads={threads}");
        // Up to four arms: every arm against each earlier one; more: against the first only.
        var played = new List<(string Arm, IReadOnlyList<ArenaSeatResult> Results)>();
        foreach (var arm in arms)
        {
            IArenaSeat Candidate() => MakeCandidate(arm, model).Seat;
            var clock = Stopwatch.StartNew();
            var results = ArenaRunner.Duplicate(rules, seeds, Candidate, Opponent, threads,
                n => { if (n % 20 == 0) Console.Error.Write($"\r{arm}: {n}/{seeds.Length * 4}"); }, ct);
            Console.Error.WriteLine();
            Console.WriteLine(Format(arm, results, clock.Elapsed));
            foreach (var (earlier, baseline) in arms.Length <= 4 ? played : played.Take(1))
                Console.WriteLine($"{"",-14} vs {earlier}: {Paired(baseline, results, r => r.Placement, "placement", "0.000")}  {Paired(baseline, results, r => r.Score, "score", "0")}");
            played.Add((arm, results));
        }

        return 0;
    }

    // The opponent at a physical seat and its true type (novice, casual, learned).
    internal static (IArenaSeat Seat, string Type) MakeOpponent(string field, LearnedModel? model, int seed, int seat)
    {
        var casual = field == "live" && new Random(unchecked(seed * 7919 + seat)).NextDouble() < 1 / 3d;
        var learned = field == "learned" || field == "mixed" && seat % 2 == 1 || field == "live" && !casual;
        if (casual)
            return (NoviceSeat.Casual(unchecked(seed * 31 + seat)), "casual");
        if (!learned)
            return (new NoviceSeat(unchecked(seed * 31 + seat)), "novice");
        var w = PolicyWeights.Default;
        return (new PolicySeat("learned", new LearnedPolicy(new DecisionPolicy(opponents: new LearnedOpponentModel(model!, w),
            calls: new LearnedCallPolicy(model!, w), weights: w), model!, w)), "learned");
    }

    // "guarded-V3" or "guarded-V3:NoviceBadWaitFactor=1;PushEvCapWeight=0" (PolicyWeights overrides):
    // the seat, the opponent model it reads the table with, and its weights.
    internal static (IArenaSeat Seat, IOpponentModel Opponents, PolicyWeights Weights) MakeCandidate(string arm, LearnedModel? model)
    {
        var head = arm.Split(':', 2);
        var (kind, version) = head[0].Split('-') is [var k, var v] ? (k, v) : throw new ArgumentException($"arm '{arm}' must look like heuristic-V3 or guarded-V2");
        var w = WithOverrides(PolicyWeights.Default with { DefenseModel = Enum.Parse<DefenseModel>(version) }, head.Length > 1 ? head[1] : "");
        switch (kind)
        {
            case "heuristic":
                var heuristic = new OpponentModel(w);
                return (new PolicySeat(arm, new DecisionPolicy(opponents: heuristic, weights: w)), heuristic, w);
            case "guarded":
                var guarded = new LearnedOpponentModel(model!, w, useLearnedDanger: false);
                return (new PolicySeat(arm, new DecisionPolicy(opponents: guarded, discards: new LearnedDiscardPolicy(model!, w),
                    calls: new LearnedCallPolicy(model!, w), weights: w)), guarded, w);
            default:
                throw new ArgumentException($"unknown arm kind '{kind}'");
        }
    }

    private static PolicyWeights WithOverrides(PolicyWeights w, string overrides)
    {
        foreach (var pair in overrides.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var (name, value) = pair.Split('=') is [var n, var v] ? (n, v) : throw new ArgumentException($"override '{pair}' must be Name=value");
            var property = typeof(PolicyWeights).GetProperty(name) ?? throw new ArgumentException($"PolicyWeights has no '{name}'");
            var parsed = property.PropertyType.IsEnum ? Enum.Parse(property.PropertyType, value)
                : Convert.ChangeType(value, property.PropertyType, System.Globalization.CultureInfo.InvariantCulture);
            w = w with { };
            property.SetValue(w, parsed);   // init-only setters are still settable by reflection on a fresh copy
        }

        return w;
    }

    // Same seeds and seats in the same order for every arm: the difference per match removes
    // the wall luck both arms shared.
    private static string Paired(IReadOnlyList<ArenaSeatResult> a, IReadOnlyList<ArenaSeatResult> b, Func<ArenaSeatResult, double> f, string what, string format)
    {
        var d = a.Zip(b, (x, y) => f(y) - f(x)).ToArray();
        var mean = d.Average();
        var se = Math.Sqrt(d.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, d.Length - 1) / d.Length);
        return $"Δ{what} {mean.ToString(format)} ± {se.ToString(format)} (z {(se > 0 ? mean / se : 0):0.0})";
    }

    private static string Format(string arm, IReadOnlyList<ArenaSeatResult> r, TimeSpan elapsed)
    {
        double Mean(Func<ArenaSeatResult, double> f) => r.Average(f);
        double Se(Func<ArenaSeatResult, double> f)
        {
            var m = Mean(f);
            return Math.Sqrt(r.Sum(x => (f(x) - m) * (f(x) - m)) / Math.Max(1, r.Count - 1) / r.Count);
        }

        var hands = (double)r.Sum(x => x.Hands);
        var dealIns = r.Sum(x => x.DealIns);
        return $"{arm,-14} matches={r.Count} placement={Mean(x => x.Placement):0.000}±{Se(x => x.Placement):0.000} "
               + $"score={Mean(x => x.Score):0}±{Se(x => x.Score):0} 1st={r.Count(x => x.Placement == 1) / (double)r.Count:P1} "
               + $"4th={r.Count(x => x.Placement == 4) / (double)r.Count:P1} win={r.Sum(x => x.Wins) / hands:P1} "
               + $"dealin={dealIns / hands:P1} dealinCost={(dealIns > 0 ? r.Sum(x => x.DealInPoints) / (double)dealIns : 0):0} "
               + $"winPoints={(r.Sum(x => x.Wins) > 0 ? r.Sum(x => x.WinPoints) / (double)r.Sum(x => x.Wins) : 0):0} "
               + $"riichi={r.Sum(x => x.Riichis) / hands:P1} ({elapsed.TotalMinutes:0.0} min)";
    }
}
