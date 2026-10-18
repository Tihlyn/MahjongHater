using System.Globalization;
using System.Text;
using MahjongHater.Core;
using MahjongHater.Core.Learning;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Simulation;

// Arena calibration: plays the candidate like `arena` and, at each of its discards, compares
// what its opponent model believes (tenpai, danger per tile, value, P(novice)) with the hidden
// hands, plus its own tenpai win estimate with how the hand ended. Read-only: no policy change.
internal static class CalibrateCommands
{
    public const string Usage = "calibrate <seeds> <novice|learned|mixed|live> <model.json|-> <arm> [threads] [rows.csv] [hands=4] [first-seed=1]";

    public static bool Handles(string command) => command == "calibrate";

    // One opponent seat at one of our discards.
    private sealed record SeatRow(string Type, string State, int Turn, int Hand, double Tenpai, bool ShapeTenpai, bool RonTenpai,
        double Value, double TrueValue, double Novice, (double Danger, bool Hit)[] Tiles);

    // Our first tenpai discard of a hand; resolved when the hand ends.
    private sealed record WinRow(int Novices, int Wall, int Ukeire, bool Riichi, bool Open, double Predicted, SimGame Game, int Seat);

    public static int Run(string[] args, CancellationToken ct)
    {
        if (args.Length < 5) throw new ArgumentException(Usage);
        var seedCount = int.Parse(args[1], CultureInfo.InvariantCulture);
        var field = args[2];
        var model = args[3] != "-" ? LearnedModel.Load(args[3]) : null;
        var arm = args[4];
        var threads = args.Length > 5 ? int.Parse(args[5], CultureInfo.InvariantCulture) : Math.Max(1, Environment.ProcessorCount - 1);
        var csv = args.Length > 6 && args[6] != "-" ? args[6] : null;
        var rules = new SimulationRules { HandsInMatch = args.Length > 7 ? int.Parse(args[7], CultureInfo.InvariantCulture) : 4 };
        var first = args.Length > 8 ? int.Parse(args[8], CultureInfo.InvariantCulture) : 1;
        if (field is not ("novice" or "learned" or "mixed" or "live")) throw new ArgumentException("opponents must be novice, learned, mixed or live");
        if ((field != "novice" || arm.StartsWith("guarded", StringComparison.Ordinal)) && model is null)
            throw new ArgumentException("learned opponents and guarded arms need a model");

        var jobs = Enumerable.Range(first, seedCount).SelectMany(seed => Enumerable.Range(0, 4).Select(seat => (Seed: seed, Seat: seat))).ToArray();
        var seats = new List<SeatRow>();
        var wins = new List<(WinRow Row, bool Won, bool Ron)>();
        var done = 0;
        Parallel.For(0, jobs.Length, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, i =>
        {
            var (seed, us) = jobs[i];
            var (candidate, opponents, weights) = ArenaCommands.MakeCandidate(arm, model);
            var types = new string[4];
            var table = new IArenaSeat[4];
            for (var s = 0; s < 4; s++)
            {
                if (s == us) { table[s] = candidate; types[s] = "us"; continue; }
                (table[s], types[s]) = ArenaCommands.MakeOpponent(field, model, seed, s);
            }

            var localSeats = new List<SeatRow>();
            var localWins = new List<WinRow>();
            SimGame? current = null;
            var hand = 0;
            var tenpaiSeen = false;
            ArenaRunner.Play(rules, seed, table, ct, (game, legal, action) =>
            {
                if (game.Actor != us || game.Phase != SimPhase.Turn || action.Kind is not (SimActionKind.Discard or SimActionKind.Riichi))
                    return;
                // PolicySeat skips the policy (and the model update) on forced moves and wins.
                if (legal.Count == 1 || legal.Any(a => a.Kind is SimActionKind.Tsumo or SimActionKind.Ron))
                    return;
                if (!ReferenceEquals(game, current)) { current = game; hand++; tenpaiSeen = false; }
                var ourTiles = game.Players[us].Hand.Select(TileHelpers.ToIndex).Distinct().ToArray();
                for (var o = 0; o < 4; o++)
                    if (o != us)
                        localSeats.Add(Observe(game, o, (o - us + 4) % 4, types[o], hand, ourTiles, opponents));
                if (!tenpaiSeen && OurTenpai(game, us, action, weights) is { } row)
                {
                    tenpaiSeen = true;
                    localWins.Add(row with { Novices = types.Count(t => t is "novice" or "casual") });
                }
            });

            var resolved = localWins.Select(w => (w, w.Game.Result!.Winners.Contains(w.Seat), w.Game.Result!.End is HandEnd.Ron or HandEnd.TripleRon)).ToList();
            lock (seats)
            {
                seats.AddRange(localSeats);
                wins.AddRange(resolved.Select(r => (r.w, r.Item2, r.Item2 && r.Item3)));
            }

            var n = Interlocked.Increment(ref done);
            if (n % 20 == 0) Console.Error.Write($"\r{arm}: {n}/{jobs.Length}");
        });
        Console.Error.WriteLine();

        Console.WriteLine($"calibrate: {seedCount} seeds x 4 seats, {rules.HandsInMatch}-hand matches, opponents={field}, arm={arm}, {seats.Count} seat rows");
        Report(seats, wins);
        if (csv is not null)
            WriteCsv(csv, seats);
        return 0;
    }

    private static SeatRow Observe(SimGame game, int seat, int relative, string type, int hand, int[] ourTiles, IOpponentModel opponents)
    {
        var p = game.Players[seat];
        var waits = SimTiles.Waits(p.Hand, p.Melds.Select(m => m.Shape));
        var furiten = SimScoring.Furiten(p);
        var ron = new Dictionary<int, int>();
        for (var kind = 0; kind < 34 && !furiten; kind++)
            if ((waits & (1UL << kind)) != 0 && SimScoring.Win(game, seat, TileHelpers.FromIndex(kind), false) is { } win)
                ron[kind] = win.Score.RonPayment;
        var open = p.Melds.Count(m => m.Shape.IsOpen);
        var state = p.Riichi ? "riichi" : open == 0 ? "dama" : open >= 3 ? "open3+" : $"open{open}";
        var tiles = ourTiles.Select(k => (opponents.Danger(TileHelpers.FromIndex(k), relative), ron.ContainsKey(k))).ToArray();
        return new SeatRow(type, state, p.River.Count, hand, opponents.TenpaiProbability(relative), waits != 0, ron.Count > 0,
            opponents.Value(relative), ron.Count > 0 ? ron.Values.Average() : 0, opponents.NoviceProbability(relative), tiles);
    }

    // Our hand after this discard, when it is tenpai: the policy's own win estimate.
    private static WinRow? OurTenpai(SimGame game, int us, SimAction action, PolicyWeights w)
    {
        var p = game.Players[us];
        var kept = p.Hand.ToList();
        kept.Remove(Tile.Parse(action.Tile!));
        var waits = SimTiles.Waits(kept, p.Melds.Select(m => m.Shape));
        if (waits == 0)
            return null;
        var visible = new int[34];
        foreach (var t in kept.Concat(game.Dora).Concat(game.Players.SelectMany(x => x.River.Select(d => d.Tile)))
                     .Concat(game.Players.Where((_, s) => s != us).SelectMany(x => x.Melds.SelectMany(m => m.Shape.Tiles))))
            visible[TileHelpers.ToIndex(t)]++;
        var ukeire = Enumerable.Range(0, 34).Where(k => (waits & (1UL << k)) != 0).Sum(k => Math.Max(0, 4 - visible[k]));
        var wall = game.LiveWall.Count;
        return new WinRow(0, wall, ukeire, action.Kind == SimActionKind.Riichi || p.Riichi, p.Melds.Any(m => m.Shape.IsOpen),
            HandValue.WinProbability(0, ukeire, 0, wall, w), game, us);
    }

    private static void Report(List<SeatRow> rows, List<(WinRow Row, bool Won, bool Ron)> wins)
    {
        var types = rows.Select(r => r.Type).Distinct().Order().ToArray();
        string[] states = ["dama", "open1", "open2", "open3+", "riichi"];

        Console.WriteLine();
        Console.WriteLine("Tenpai (non-riichi seats): predicted vs true. shape = 13 tiles one away; ron = could ron a tile now (yaku, not furiten).");
        Console.WriteLine($"{"type",-8} {"state",-7} {"n",8} {"pred",7} {"shape",7} {"ron",7} {"brier",7}");
        foreach (var type in types)
            foreach (var state in states.Where(s => s != "riichi"))
                if (rows.Where(r => r.Type == type && r.State == state).ToList() is { Count: > 0 } g)
                    Console.WriteLine($"{type,-8} {state,-7} {g.Count,8} {g.Average(r => r.Tenpai),7:P1} {g.Average(r => r.ShapeTenpai ? 1d : 0),7:P1} "
                                      + $"{g.Average(r => r.RonTenpai ? 1d : 0),7:P1} {g.Average(r => Math.Pow(r.Tenpai - (r.ShapeTenpai ? 1 : 0), 2)),7:0.000}");

        Console.WriteLine();
        Console.WriteLine("Tenpai by turn (non-riichi seats): predicted / true shape tenpai.");
        (int Lo, int Hi)[] turns = [(0, 5), (6, 8), (9, 11), (12, 14), (15, 99)];
        Console.WriteLine($"{"type",-8} {"state",-7} " + string.Join(" ", turns.Select(t => $"{(t.Hi == 99 ? $"{t.Lo}+" : $"{t.Lo}-{t.Hi}"),15}")));
        foreach (var type in types)
            foreach (var state in new[] { "dama", "open" })
            {
                var cells = turns.Select(t =>
                {
                    var g = rows.Where(r => r.Type == type && (state == "dama" ? r.State == "dama" : r.State.StartsWith("open", StringComparison.Ordinal))
                                            && r.Turn >= t.Lo && r.Turn <= t.Hi).ToList();
                    return g.Count < 30 ? $"{"-",15}" : $"{g.Average(r => r.Tenpai),7:P0}/{g.Average(r => r.ShapeTenpai ? 1d : 0),-7:P0}";
                });
                Console.WriteLine($"{type,-8} {state,-7} " + string.Join(" ", cells));
            }

        Console.WriteLine();
        Console.WriteLine("Tenpai reliability (non-riichi, all types): predicted bucket -> true shape tenpai, by type.");
        double[] edges = [0, 0.1, 0.2, 0.3, 0.5, 0.7, 1.0001];
        Console.WriteLine($"{"bucket",-10} " + string.Join(" ", types.Select(t => $"{t,18}")));
        for (var b = 0; b < edges.Length - 1; b++)
        {
            var cells = types.Select(type =>
            {
                var g = rows.Where(r => r.Type == type && r.State != "riichi" && r.Tenpai >= edges[b] && r.Tenpai < edges[b + 1]).ToList();
                return g.Count < 30 ? $"{"-",18}" : $"{g.Average(r => r.ShapeTenpai ? 1d : 0),7:P0} (n {g.Count,6})";
            });
            Console.WriteLine($"{$"{edges[b]:0.0}-{Math.Min(1, edges[b + 1]):0.0}",-10} " + string.Join(" ", cells));
        }

        Console.WriteLine();
        Console.WriteLine("Danger given ron tenpai: predicted per tile vs dealt in (tiles in our hand).");
        Console.WriteLine($"{"type",-8} {"state",-7} {"tiles",8} {"pred",7} {"true",7} {"ratio",6}");
        foreach (var type in types)
            foreach (var state in new[] { "dama", "open", "riichi" })
            {
                var g = rows.Where(r => r.Type == type && r.RonTenpai && (state == "open" ? r.State.StartsWith("open", StringComparison.Ordinal) : r.State == state))
                    .SelectMany(r => r.Tiles).ToList();
                if (g.Count == 0) continue;
                var pred = g.Average(t => t.Danger);
                var hit = g.Average(t => t.Hit ? 1d : 0);
                Console.WriteLine($"{type,-8} {state,-7} {g.Count,8} {pred,7:P2} {hit,7:P2} {(pred > 0 ? hit / pred : 0),6:0.00}");
            }

        Console.WriteLine();
        Console.WriteLine("Danger reliability given ron tenpai: predicted bucket -> dealt in, by type.");
        double[] dangerEdges = [0, 1e-9, 0.02, 0.04, 0.07, 0.10, 0.15, 0.25, 1.0001];
        Console.WriteLine($"{"bucket",-10} " + string.Join(" ", types.Select(t => $"{t,18}")));
        for (var b = 0; b < dangerEdges.Length - 1; b++)
        {
            var cells = types.Select(type =>
            {
                var g = rows.Where(r => r.Type == type && r.RonTenpai).SelectMany(r => r.Tiles)
                    .Where(t => t.Danger >= dangerEdges[b] && t.Danger < dangerEdges[b + 1]).ToList();
                return g.Count < 100 ? $"{"-",18}" : $"{g.Average(t => t.Hit ? 1d : 0),7:P1} (n {g.Count,7})";
            });
            var label = b == 0 ? "0 (safe)" : $"{dangerEdges[b]:0.00}-{Math.Min(1, dangerEdges[b + 1]):0.00}";
            Console.WriteLine($"{label,-10} " + string.Join(" ", cells));
        }

        Console.WriteLine();
        Console.WriteLine("Expected deal-in per tile (tenpai x danger) vs dealt in, all seats.");
        foreach (var type in types)
        {
            var g = rows.Where(r => r.Type == type).SelectMany(r => r.Tiles.Select(t => (Pred: r.Tenpai * t.Danger, t.Hit))).ToList();
            var pred = g.Average(t => t.Pred);
            var hit = g.Average(t => t.Hit ? 1d : 0);
            Console.WriteLine($"{type,-8} pred {pred:P2}  true {hit:P2}  ratio {(pred > 0 ? hit / pred : 0):0.00}");
        }

        Console.WriteLine();
        Console.WriteLine("Value given ron tenpai: predicted (with placement floor and situation factor) vs the true ron value.");
        Console.WriteLine($"{"type",-8} {"state",-7} {"n",8} {"pred",7} {"true",7} {"ratio",6}");
        foreach (var type in types)
            foreach (var state in states)
                if (rows.Where(r => r.Type == type && r.State == state && r.RonTenpai).ToList() is { Count: > 0 } g)
                {
                    var pred = g.Average(r => r.Value);
                    var truth = g.Average(r => r.TrueValue);
                    Console.WriteLine($"{type,-8} {state,-7} {g.Count,8} {pred,7:0} {truth,7:0} {(truth > 0 ? pred / truth : 0),6:0.00}");
                }

        if (rows.Any(r => r.Novice > 0))
        {
            Console.WriteLine();
            Console.WriteLine("P(novice) by true type and hand of the match: mean / share above 50 %.");
            Console.WriteLine($"{"type",-8} " + string.Join(" ", Enumerable.Range(1, 5).Select(h => $"{(h == 5 ? "hand 5+" : $"hand {h}"),15}")));
            foreach (var type in types)
                Console.WriteLine($"{type,-8} " + string.Join(" ", Enumerable.Range(1, 5).Select(h =>
                {
                    var g = rows.Where(r => r.Type == type && (h == 5 ? r.Hand >= 5 : r.Hand == h)).ToList();
                    return g.Count < 30 ? $"{"-",15}" : $"{g.Average(r => r.Novice),6:P0} / {g.Average(r => r.Novice > 0.5 ? 1d : 0),-6:P0}";
                })));
        }

        Console.WriteLine();
        Console.WriteLine("Our first tenpai of a hand: predicted win (HandValue.WinProbability) vs won, by novices at the table.");
        Console.WriteLine($"{"novices",-8} {"kind",-7} {"n",7} {"pred",7} {"won",7} {"by ron",7} {"ratio",6}");
        foreach (var group in wins.GroupBy(w => (w.Row.Novices, Kind: w.Row.Riichi ? "riichi" : w.Row.Open ? "open" : "dama")).OrderBy(g => g.Key.Novices).ThenBy(g => g.Key.Kind))
        {
            var pred = group.Average(w => w.Row.Predicted);
            var won = group.Average(w => w.Won ? 1d : 0);
            Console.WriteLine($"{group.Key.Novices,-8} {group.Key.Kind,-7} {group.Count(),7} {pred,7:P1} {won,7:P1} {group.Average(w => w.Ron ? 1d : 0),7:P1} {(pred > 0 ? won / pred : 0),6:0.00}");
        }
    }

    private static void WriteCsv(string path, List<SeatRow> rows)
    {
        var text = new StringBuilder("type,state,turn,hand,tenpai,shape_tenpai,ron_tenpai,value,true_value,novice,tiles,pred_hits,hits\n");
        foreach (var r in rows)
            text.Append(string.Create(CultureInfo.InvariantCulture,
                $"{r.Type},{r.State},{r.Turn},{r.Hand},{r.Tenpai:0.####},{(r.ShapeTenpai ? 1 : 0)},{(r.RonTenpai ? 1 : 0)},{r.Value:0},{r.TrueValue:0},{r.Novice:0.####},{r.Tiles.Length},{r.Tiles.Sum(t => t.Danger):0.####},{r.Tiles.Count(t => t.Hit)}\n"));
        File.WriteAllText(path, text.ToString());
    }
}
