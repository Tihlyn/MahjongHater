using System.Text;
using System.Text.Json;
using MahjongHater.Core;
using MahjongHater.Core.Learning;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Replay;
using MahjongHater.Core.Simulation;
using MahjongHater.Core.State;
using Xunit;
using static MahjongHater.Tests.Policy.PolicyFixtures;

namespace MahjongHater.Tests.Learning;

// Core/Learning had no tests; the exporter manifest bug (docs/research/EXPERIMENTAL_ASSESSMENT.md)
// is the kind of seam these cover: encoder contract, dataset layout + manifest, model guards,
// policy fallback, and the evaluation harness on a synthetic corpus.
public sealed class LearningTests
{
    private static readonly SimulationRules Rules = new();

    // Synthetic Tenhou trace. Turn order is validated by the parser, so a second seat-0
    // turn needs seats 1-3 to draw and discard in between; hands use distinct kinds (no
    // quads) because the exporter drops rows whose legal actions include a kan.
    private const string TwoTurns = "<T82/><D0/><U122/><E52/><V126/><F104/><W130/><G21/><T134/><D4/>";

    private static int TileId(int seat, int j) => 4 * ((13 * seat + j) % 34) + (13 * seat + j) / 34;

    private static byte[] Trace(string events = TwoTurns, string ranks = "16,17,18,19")
    {
        var hands = string.Join(" ", Enumerable.Range(0, 4).Select(s => $"hai{s}=\"{string.Join(',', Enumerable.Range(0, 13).Select(j => TileId(s, j)))}\""));
        return Encoding.UTF8.GetBytes($"<mjloggm><GO type=\"169\"/><UN dan=\"{ranks}\" n0=\"NAME\"/>"
            + $"<INIT seed=\"0,0,0,1,1,135\" ten=\"250,250,250,250\" oya=\"0\" {hands}/>"
            + events + "<RYUUKYOKU sc=\"250,0,250,0,250,0,250,0\" owari=\"250,0,250,0,250,0,250,0\"/></mjloggm>");
    }

    [Fact]
    public void Encoder_has_fixed_shape_and_marks_hand_draw_dora_and_riichi_planes()
    {
        var state = Seat(Snap("123m456m4578p447s1z"), 2, "4s1m", riichi: true, riichiIndex: 1) with { DoraIndicators = [Tile.Parse("3p")] };
        var x = LearningFeatures.Encode(state);
        Assert.Equal(LearningFeatures.Count, x.Length);
        Assert.Equal(72 * 34, LearningFeatures.Count);
        int Kind(string t) => TileHelpers.ToIndex(Tile.Parse(t));
        Assert.Equal(0.5f, x[0 * 34 + Kind("4s")]);             // two copies × 0.25
        Assert.Equal(1f, x[2 * 34 + Kind("1z")]);                // drawn tile = last in hand
        Assert.Equal(0.2f, x[3 * 34 + Kind("3p")]);              // indicator
        Assert.Equal(0.2f, x[4 * 34 + Kind("4p")]);              // dora itself
        var c = 8 + 2 * 6;
        Assert.Equal(0.25f, x[c * 34 + Kind("4s")]);             // seat 2 pond
        Assert.Equal(1f, x[(c + 4) * 34 + Kind("1m")]);          // riichi tile
        Assert.All(x, v => Assert.True(float.IsFinite(v)));
        Assert.Equal(x, LearningFeatures.Encode(state));          // deterministic
        // Look-ahead planes: a 14-tile hand has a per-discard shanten plane and a broadcast
        // current-shanten plane; the legacy encoding is the same first 64 planes.
        Assert.True(x[68 * 34] > 0 && x[65 * 34 + Kind("1z")] > 0);
        var legacy = LearningFeatures.Encode(state, LearningFeatures.LegacyVersion);
        Assert.Equal(64 * 34, legacy.Length);
        Assert.Equal(legacy, x.Take(64 * 34));
        Assert.Throws<ArgumentException>(() => LearningFeatures.Encode(state, "other"));
        // Storage form: the 36 broadcast planes collapse to one value each and expand back exactly.
        var compact = LearningFeatures.Compact(x);
        Assert.Equal(LearningFeatures.CompactCount, compact.Length);
        Assert.Equal(x, LearningFeatures.Expand(compact));
        Assert.Equal(x[32 * 34], compact[32 * 34]);                 // first global (our score)
        Assert.Equal(x[68 * 34], compact[32 * 34 + 32 + 4 * 34]);   // current shanten plane
    }

    [Fact]
    public void Action_index_separates_red_fives_and_riichi()
    {
        var discard = SimAction.Make(SimActionKind.Discard, Tile.Parse("5p"));
        var red = SimAction.Make(SimActionKind.Discard, Tile.Parse("0p"));
        var riichi = SimAction.Make(SimActionKind.Riichi, Tile.Parse("5p"));
        Assert.NotEqual(LearningFeatures.ActionIndex(discard), LearningFeatures.ActionIndex(red));
        Assert.Equal(LearningFeatures.ActionIndex(discard) + 37, LearningFeatures.ActionIndex(riichi));
        Assert.Equal(LearningFeatures.PassAction, LearningFeatures.ActionIndex(SimAction.Make(SimActionKind.Pass)));
        Assert.Equal(-1, LearningFeatures.ActionIndex(SimAction.Make(SimActionKind.Pass), LearningFeatures.LegacyVersion));
        Assert.InRange(LearningFeatures.ActionIndex(riichi), 0, LearningFeatures.LegacyActions - 1);
        // Chi shapes by the position of the called tile; pon/kan by kind.
        Assert.Equal(LearningFeatures.ChiLowAction, LearningFeatures.ActionIndex(SimAction.Make(SimActionKind.Chi, Tile.Parse("4p"), TestTiles.Parse("56p"))));
        Assert.Equal(LearningFeatures.ChiMiddleAction, LearningFeatures.ActionIndex(SimAction.Make(SimActionKind.Chi, Tile.Parse("5p"), TestTiles.Parse("46p"))));
        Assert.Equal(LearningFeatures.ChiHighAction, LearningFeatures.ActionIndex(SimAction.Make(SimActionKind.Chi, Tile.Parse("6p"), TestTiles.Parse("45p"))));
        Assert.Equal(LearningFeatures.PonAction, LearningFeatures.ActionIndex(SimAction.Make(SimActionKind.Pon, Tile.Parse("5z"), TestTiles.Parse("55z"))));
        Assert.Equal(LearningFeatures.OpenKanAction, LearningFeatures.ActionIndex(SimAction.Make(SimActionKind.OpenKan, Tile.Parse("5z"), TestTiles.Parse("555z"))));
        Assert.InRange(LearningFeatures.AddedKanAction, 0, LearningFeatures.Actions - 1);
    }

    [Fact]
    public void Dataset_export_writes_a_complete_manifest_and_fixed_rows()
    {
        using var temp = new TemporaryDirectory();
        var input = Path.Combine(temp.Path, "raw");
        Directory.CreateDirectory(input);
        for (var i = 0; i < 6; i++)
            File.WriteAllBytes(Path.Combine(input, $"game{i}.xml"), Trace(events: $"<T{82 + 4 * i}/><D{4 * i}/><U122/><E52/>"));
        var corpusPath = Path.Combine(temp.Path, "corpus");
        var imported = ReplayCorpus.Import(input, corpusPath, Rules, 0);
        Assert.Empty(imported.Rejected);
        var corpus = new ReplayCorpus(corpusPath);
        Assert.Equal(6, corpus.Count + imported.Duplicates);
        Assert.True(corpus.Count >= 4);

        var dataset = Path.Combine(temp.Path, "dataset");
        LearningDataset.Export(corpus, dataset);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataset, "manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal(LearningDataset.Schema, root.GetProperty("Schema").GetInt32());
        Assert.Equal(LearningFeatures.Version, root.GetProperty("Features").GetString());
        Assert.Equal(LearningFeatures.Channels, root.GetProperty("Channels").GetInt32());
        Assert.Equal(LearningFeatures.Actions, root.GetProperty("Actions").GetInt32());
        Assert.True(root.GetProperty("Compact").GetBoolean());
        Assert.Equal(LearningDataset.CompactRowFloats, root.GetProperty("RowFloats").GetInt32());
        Assert.Equal(LearningFeatures.CompactCount, root.GetProperty("FeatureFloats").GetInt32());
        Assert.Equal(corpus.Fingerprint, root.GetProperty("Corpus").GetString());
        var rows = root.GetProperty("Rows");
        var total = 0;
        foreach (var split in new[] { "train", "validation", "test" })
        {
            var n = rows.GetProperty(split).GetInt32();
            total += n;
            Assert.Equal(n * LearningDataset.CompactRowFloats * 4L, new FileInfo(Path.Combine(dataset, split + ".f32")).Length);
        }

        Assert.True(total > 0);
        Assert.Equal(total + root.GetProperty("Skipped").GetInt32(), corpus.Manifest.Games.Sum(g => g.Decisions));
    }

    [Fact]
    public void Model_rejects_wrong_dimensions_and_runs_a_valid_artifact()
    {
        var artifact = SyntheticArtifact(channels: 2, hidden: 3);
        var model = new LearnedModel(artifact);
        var output = model.Predict(Snap("123m456m4578p447s1z"));
        Assert.Equal(model.Outputs, output.Length);
        Assert.Equal(2 * LearningFeatures.Actions + LearnedModel.OpponentOutputs, output.Length);
        Assert.Equal(LearningFeatures.Version, model.FeatureVersion);
        Assert.All(output, v => Assert.True(float.IsFinite(v)));
        Assert.InRange(model.Tenpai(0), 0, 1);
        Assert.InRange(model.Points(0.5f), 0, 128000);
        Assert.True(model.Supports(Snap("")));
        Assert.False(model.Supports(Snap("") with { Ruleset = new RulesetOptions(true, 4) }));

        Assert.Throws<InvalidDataException>(() => new LearnedModel(artifact with { Schema = 1 }));      // no Conv1/Conv2
        Assert.Throws<InvalidDataException>(() => new LearnedModel(artifact with { Schema = 3 }));
        Assert.Throws<InvalidDataException>(() => new LearnedModel(artifact with { Features = "other" }));
        Assert.Throws<InvalidDataException>(() => new LearnedModel(artifact with { Features = LearningFeatures.LegacyVersion }));   // stem width mismatch
        Assert.Throws<InvalidDataException>(() => new LearnedModel(artifact with { Blocks = [new ResidualBlock(artifact.Stem!, artifact.Stem!)] }));
        // A legacy schema-1 artifact (two plain convs, v1 planes, 74 actions) still loads.
        var legacy = SyntheticArtifact(2, 3, legacy: true);
        var legacyModel = new LearnedModel(legacy);
        Assert.Equal(2 * LearningFeatures.LegacyActions + LearnedModel.OpponentOutputs, legacyModel.Predict(Snap("123m456m4578p447s1z")).Length);
        Assert.Throws<InvalidDataException>(() => new LearnedModel(legacy with { Features = LearningFeatures.Version }));
        Assert.Throws<InvalidDataException>(() => new LearnedModel(artifact with { Output = artifact.Output with { Outputs = 10, Weight = new float[30], Bias = new float[10] } }));
        var nan = (float[])artifact.Dense.Weight.Clone();
        nan[0] = float.NaN;
        Assert.Throws<InvalidDataException>(() => new LearnedModel(artifact with { Dense = artifact.Dense with { Weight = nan } }));
    }

    [Fact]
    public void Learned_policy_only_handles_closed_ordinary_draws_and_falls_back_otherwise()
    {
        var model = new LearnedModel(SyntheticArtifact(2, 3));
        var policy = new LearnedPolicy(new DecisionPolicy(), model);
        var closed = Snap("123m456m4578p447s1z", LegalAction.Discard | LegalAction.Riichi);
        Assert.NotEmpty(LearnedPolicy.Legal(closed));
        var choice = policy.Choose(closed, CancellationToken.None);
        Assert.True(choice.IsDiscard);
        Assert.Contains(choice.Steps, s => s.Stage == "learned");

        var open = closed with { OurMelds = [Meld.MakePon(Tile.Parse("5z"), true)], Hand = TestTiles.Parse("123m456m4578p1z") };
        Assert.Empty(LearnedPolicy.Legal(open));
        var fallback = policy.Choose(open, CancellationToken.None);
        Assert.DoesNotContain(fallback.Steps, s => s.Stage == "learned");
    }

    [Fact]
    public void Learned_discard_ordering_keeps_analyzer_metrics_and_sums_to_one()
    {
        var model = new LearnedModel(SyntheticArtifact(2, 3));
        var state = Snap("123m456m4578p447s1z", LegalAction.Discard | LegalAction.Riichi);
        var opponents = Model(state);
        var heuristic = new HeuristicDiscardPolicy().Rank(state, opponents, CancellationToken.None);
        var learned = new LearnedDiscardPolicy(model).Rank(state, opponents, CancellationToken.None);
        Assert.Equal(heuristic.Count, learned.Count);
        Assert.Equal(1, learned.Sum(c => c.Score), 6);
        foreach (var c in learned)
        {
            var h = heuristic.Single(x => x.Tile.Equals(c.Tile));
            Assert.Equal(h.ShantenAfter, c.ShantenAfter);
            Assert.Equal(h.Ukeire, c.Ukeire);
            Assert.Contains("imitation", c.Note);
        }
        Assert.True(learned[0].Score >= learned[^1].Score);
        // Outside the network's action space the heuristic ordering is returned untouched.
        var open = state with { OurMelds = [Meld.MakePon(Tile.Parse("5z"), true)], Hand = TestTiles.Parse("123m456m4578p1z") };
        var openOpponents = Model(open);
        Assert.Equal(new HeuristicDiscardPolicy().Rank(open, openOpponents, CancellationToken.None).Select(c => c.Tile),
            new LearnedDiscardPolicy(model).Rank(open, openOpponents, CancellationToken.None).Select(c => c.Tile));
    }

    [Fact]
    public void Learned_opponent_model_zeroes_known_safe_tiles_and_falls_back_when_unsupported()
    {
        var model = new LearnedModel(SyntheticArtifact(2, 3));
        var opponents = new LearnedOpponentModel(model);
        var state = Seat(Snap("123m456m4578p447s1z"), 1, "4s1m", riichi: true, riichiIndex: 1);
        opponents.Update(state);
        Assert.Equal(1, opponents.TenpaiProbability(1));
        Assert.Equal(0, opponents.Danger(Tile.Parse("4s"), 1));          // genbutsu
        Assert.InRange(opponents.Danger(Tile.Parse("5p"), 1), 0, 1);
        Assert.Equal("learned", opponents.Explain(Tile.Parse("5p"), 1).Why);
        var tablesOnly = new LearnedOpponentModel(model, useLearnedDanger: false);
        tablesOnly.Update(state);
        Assert.Equal(opponents.TenpaiProbability(1), tablesOnly.TenpaiProbability(1));
        Assert.NotEqual("learned", tablesOnly.Explain(Tile.Parse("5p"), 1).Why);
        Assert.Equal(new OpponentModel().Danger(Tile.Parse("5p"), 1), 0, 6);   // sanity: fresh model has no state
        // A rule profile the model was not trained for: the table model answers instead.
        opponents.Update(state with { Ruleset = new RulesetOptions(true, 4) });
        Assert.NotEqual("learned", opponents.Explain(Tile.Parse("5p"), 1).Why);
        Assert.Contains("non-suji", opponents.Explain(Tile.Parse("5p"), 1).Class);
    }

    [Fact]
    public void Evaluation_harness_scores_every_policy_on_a_synthetic_corpus()
    {
        using var temp = new TemporaryDirectory();
        var input = Path.Combine(temp.Path, "raw");
        Directory.CreateDirectory(input);
        for (var i = 0; i < 4; i++)
            File.WriteAllBytes(Path.Combine(input, $"game{i}.xml"), Trace(events: TwoTurns.Replace("<D4/>", $"<D{4 * (i + 1)}/>")));
        var corpusPath = Path.Combine(temp.Path, "corpus");
        var imported = ReplayCorpus.Import(input, corpusPath, Rules, 0);
        Assert.Empty(imported.Rejected);
        var corpus = new ReplayCorpus(corpusPath);
        var model = new LearnedModel(SyntheticArtifact(2, 3));
        var report = LearningEvaluation.Run(corpus, model, PolicyWeights.Default, new EvaluationOptions { Split = "all", Threads = 2 });
        Assert.Equal(corpus.Count, report.Games);
        Assert.True(report.Decisions > 0);
        // Turn decisions go to "all"; claim-window reactions to "reaction" (both count in Decisions).
        var reactions = report.Agreement.Values.First().TryGetValue("reaction", out var cell) ? cell.Decisions : 0;
        Assert.True(reactions > 0);
        foreach (var name in new[] { LearningEvaluation.Heuristic, LearningEvaluation.HeuristicEv, LearningEvaluation.Legacy, LearningEvaluation.Learned, LearningEvaluation.Hybrid, LearningEvaluation.HybridTenpai, LearningEvaluation.Guarded })
        {
            var all = report.Agreement[name]["all"];
            Assert.Equal(report.Decisions, all.Decisions + reactions);
            Assert.Equal(reactions, report.Agreement[name]["reaction"].Decisions);
            Assert.InRange(all.Agreement, 0, 1);
            Assert.InRange(all.PolicyDealInRate, 0, 1);
        }

        Assert.Contains("tenpai/heuristic", report.Calibration.Keys);
        Assert.Contains("tenpai/learned", report.Calibration.Keys);
        var text = LearningEvaluation.Format(report);
        Assert.Contains("agreement with the human action", text);
        var json = Path.Combine(temp.Path, "report.json");
        LearningEvaluation.Write(json, report);
        Assert.True(new FileInfo(json).Length > 100);
    }

    [Fact]
    public void Placement_head_is_optional_and_answers_score_counterfactuals()
    {
        var plain = new LearnedModel(SyntheticArtifact(2, 3));
        Assert.False(plain.HasPlacementHead);
        Assert.Null(plain.Placement(Snap("123m456m4578p447s1z")));
        var artifact = SyntheticArtifact(2, 3, placement: true);
        var model = new LearnedModel(artifact);
        Assert.True(model.HasPlacementHead);
        Assert.Equal(model.Outputs + 4, model.Predict(Snap("123m456m4578p447s1z")).Length);
        var state = Snap("123m456m4578p447s1z") with { Seats = StateSnapshot.Empty.Seats.Select(s => s with { Score = 25000 }).ToArray() };
        var now = model.Placement(state)!;
        Assert.Equal(1, now.Sum(), 6);
        Assert.All(now, p => Assert.InRange(p, 0, 1));
        var win = model.Placement(state, [12000, -12000, 0, 0])!;
        Assert.NotEqual(now, win);
        Assert.Throws<ArgumentException>(() => model.Placement(state, [1, 2]));
        // The counterfactual must not evict the snapshot's own prediction.
        Assert.Equal(model.Predict(state), model.Predict(state));
        var opponents = new LearnedOpponentModel(model);
        opponents.Update(state);
        Assert.NotNull(((IPlacementModel)opponents).Placement(state, new int[4]));
    }

    [Fact]
    public void Learned_call_policy_answers_claim_windows_and_defers_elsewhere()
    {
        var model = new LearnedModel(SyntheticArtifact(2, 3));
        var policy = new LearnedCallPolicy(model);
        // 13 tiles, a yakuhai pair, pon offered: a claim window the network scores.
        var offered = Snap("123m456p67s1155z9s", LegalAction.Pon) with
        {
            DrawnTile = null, CallTile = Tile.Parse("5z"), CallFromSeat = 3, Phase = GamePhase.CallPrompt,
        };
        Assert.True(LearnedCallPolicy.IsClaimWindow(offered));
        var decision = policy.Evaluate(offered, Model(offered), CancellationToken.None);
        Assert.Contains("Learned", decision.Reason.Display);
        if (decision.Accept) Assert.Equal(ActionKind.Pon, decision.Kind);
        // Never accepts a call the heuristic would not make (no yaku-preserving meld).
        var weights = PolicyWeights.Default with { LearnedCallPassThreshold = 1.01 };
        var eager = new LearnedCallPolicy(model, weights);
        var heuristic = new CallPolicy().Evaluate(offered, Model(offered), CancellationToken.None);
        var eagerDecision = eager.Evaluate(offered, Model(offered), CancellationToken.None);
        Assert.True(!eagerDecision.Accept || heuristic.Accept);
        // Not a claim window (own turn): pure heuristic, no learned note.
        var turn = Snap("123m456m4578p447s1z", LegalAction.Discard);
        Assert.False(LearnedCallPolicy.IsClaimWindow(turn));
        Assert.DoesNotContain("Learned", policy.Evaluate(turn, Model(turn), CancellationToken.None).Reason.Display);
        // A legacy model has no reaction actions: heuristic answer on claim windows too.
        var legacyPolicy = new LearnedCallPolicy(new LearnedModel(SyntheticArtifact(2, 3, legacy: true)));
        Assert.DoesNotContain("Learned", legacyPolicy.Evaluate(offered, Model(offered), CancellationToken.None).Reason.Display);
    }

    [Fact]
    public void Importer_records_reaction_decisions_for_seats_that_could_call()
    {
        using var temp = new TemporaryDirectory();
        var input = Path.Combine(temp.Path, "raw");
        Directory.CreateDirectory(input);
        // Seat 0 discards its drawn tile (id 82 = kind 20, 3s); no other seat holds two of that
        // kind in the synthetic hands, so only chi by seat 1 is possible when seat 1 holds
        // neighbours. Import the standard trace and check reaction rows are well-formed.
        File.WriteAllBytes(Path.Combine(input, "game0.xml"), Trace());
        var corpusPath = Path.Combine(temp.Path, "corpus");
        var imported = ReplayCorpus.Import(input, corpusPath, Rules, 0);
        Assert.Empty(imported.Rejected);
        var corpus = new ReplayCorpus(corpusPath);
        var decisions = Enumerable.Range(0, corpus.Count).SelectMany(i => corpus.Read(i).Decisions).ToList();
        Assert.NotEmpty(decisions);
        foreach (var d in decisions.Where(d => d.LegalActions.Any(a => a.Kind == SimActionKind.Pass)))
        {
            Assert.Contains(d.LegalActions, a => a.Kind is SimActionKind.Chi or SimActionKind.Pon or SimActionKind.OpenKan);
            Assert.DoesNotContain(d.LegalActions, a => a.Kind == SimActionKind.Ron);
            Assert.Contains(d.ObservedAction, d.LegalActions);
            Assert.Equal(SimPhase.DiscardResponses, d.Observation.Phase);
        }
    }

    [Fact]
    public void Tenpai_refit_runs_on_a_corpus_and_reports_both_fits()
    {
        using var temp = new TemporaryDirectory();
        var input = Path.Combine(temp.Path, "raw");
        Directory.CreateDirectory(input);
        for (var i = 0; i < 4; i++)
            File.WriteAllBytes(Path.Combine(input, $"game{i}.xml"), Trace(events: TwoTurns.Replace("<D4/>", $"<D{4 * (i + 1)}/>")));
        var corpusPath = Path.Combine(temp.Path, "corpus");
        ReplayCorpus.Import(input, corpusPath, Rules, 0);
        var result = TenpaiFit.Run(new ReplayCorpus(corpusPath), PolicyWeights.Default, "all", iterations: 200);
        Assert.True(result.Rows > 0);
        Assert.True(double.IsFinite(result.Intercept) && double.IsFinite(result.PerDiscard));
        Assert.True(result.FittedLogLoss <= result.ShippedLogLoss + 1e-9);
        Assert.Contains("TenpaiLogitIntercept", result.Initializers);
    }

    // A structurally valid artifact with small random weights (not a trained model):
    // schema 2 (stem + one residual block, v2 planes) or the legacy schema-1 shape.
    private static LearnedArtifact SyntheticArtifact(int channels, int hidden, bool legacy = false, bool placement = false)
    {
        var random = new Random(3);
        NeuralLayer Layer(int inputs, int outputs, int kernel) => new(inputs, outputs, kernel,
            Enumerable.Range(0, inputs * outputs * kernel).Select(_ => (float)(random.NextDouble() - 0.5) * 0.1f).ToArray(),
            new float[outputs]);
        if (legacy)
            return new LearnedArtifact(1, LearningFeatures.LegacyVersion, "synthetic", Rules,
                Layer(LearningFeatures.LegacyChannels, channels, 3), Layer(channels, channels, 3), Layer(channels * 34, hidden, 1),
                Layer(hidden, 2 * LearningFeatures.LegacyActions + LearnedModel.OpponentOutputs, 1),
                new ProbabilityCalibration(1, 0), new ProbabilityCalibration(1, 0), 1f, false, "synthetic");
        return new LearnedArtifact(2, LearningFeatures.Version, "synthetic", Rules, null, null, Layer(channels * 34, hidden, 1),
            Layer(hidden, 2 * LearningFeatures.Actions + LearnedModel.OpponentOutputs + (placement ? 4 : 0), 1),
            new ProbabilityCalibration(1, 0), new ProbabilityCalibration(1, 0), 1f, false, "synthetic")
        { Stem = Layer(LearningFeatures.Channels, channels, 3), Blocks = [new ResidualBlock(Layer(channels, channels, 3), Layer(channels, channels, 3))] };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string parent = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MahjongHater-LearningTests"));
        public string Path { get; }
        public TemporaryDirectory() { this.Path = System.IO.Path.Combine(this.parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(this.Path); }
        public void Dispose()
        {
            var target = System.IO.Path.GetFullPath(this.Path);
            if (!target.StartsWith(this.parent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(target, recursive: true);
        }
    }
}
