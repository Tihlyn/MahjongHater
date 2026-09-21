using System.Text.Json;
using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Precomputed;
using MahjongHater.Core.State;
using Xunit;

namespace MahjongHater.Tests.Policy;

public sealed class PrecomputedPolicyTests
{
    private static StateSnapshot State() => PolicyFixtures.Snap();
    private static readonly string Profile = BeliefState.Profile(PolicyWeights.Default);

    private static StateIdentity Identity(StateSnapshot state) => IncrementalStateKey.Create(state,
        BeliefState.Capture(state, new OpponentModel()), Profile);

    private static PolicyArtifact Artifact(StateSnapshot state, params StoredDiscard[] actions) =>
        new(PolicyTable.Schema, BeliefState.ModelVersion, Profile, 1, 112, 2, [new(Identity(state), actions)]);

    private static StoredDiscard[] Actions(StateSnapshot state) => DiscardActionSpace.Generate(state)
        .Select((t, i) => new StoredDiscard(t.ToString(), 8, i * 100, 10, 1, 8, [])).ToArray();

    [Fact]
    public void Incremental_hash_matches_rebuild_through_events_and_reset()
    {
        var state = State();
        var changed = PolicyFixtures.Seat(state, discards: "1m9s", riichi: true, riichiIndex: 1);
        var key = new IncrementalStateKey();
        foreach (var s in new[] { state, changed, changed with { WallRemaining = 45 }, StateSnapshot.Empty, state, changed })
        {
            var beliefs = BeliefState.Capture(s, new OpponentModel());
            Assert.Equal(IncrementalStateKey.Create(s, beliefs, Profile), key.Update(s, beliefs, Profile));
        }
    }

    [Fact]
    public void Identity_ignores_hand_order_sequence_and_diagnostics_but_tracks_physical_red_tiles()
    {
        var state = State();
        Assert.Equal(Identity(state), Identity(state with { Hand = state.Hand.Reverse().ToArray(), Sequence = 900,
            Notes = ["different diagnostic"], RawStateCode = 99, CallOptions = ["translated label"] }));
        var red = state.Hand.Select(t => t == Tile.Parse("5m") ? Tile.Parse("0m") : t).ToArray();
        Assert.NotEqual(Identity(state), Identity(state with { Hand = red }));
    }

    [Fact]
    public void Identity_and_dispatch_track_context_that_changes_decisions()
    {
        var state = State();
        StateSnapshot[] changes =
        [
            state with { RoundWind = Wind.South }, state with { SeatWind = Wind.East },
            state with { DealerSeat = 2 }, state with { WallRemaining = 40 },
            state with { Honba = 1 }, state with { RiichiSticks = 1 }, state with { HandNumber = 4 },
            state with { Ruleset = new RulesetOptions(false, 4) }, state with { LayoutHealthy = false },
            state with { DoraIndicators = [Tile.Parse("3m")] }, state with { UraDoraIndicators = [Tile.Parse("4m")] },
            state with { Legal = LegalAction.Discard | LegalAction.Riichi }, state with { OurRiichi = true },
            state with { DrawnTile = Tile.Parse("3m") }, state with { Phase = GamePhase.SelfDeclare },
            state with { CallTile = Tile.Parse("3m"), CallFromSeat = 3 },
            state with { OurMelds = [Meld.MakePon(Tile.Parse("2z"), true)] },
            state with { CallShapes = [Meld.MakeChi(Tile.Parse("1m"), Tile.Parse("2m"), Tile.Parse("3m"))] },
        ];
        foreach (var changed in changes)
        {
            Assert.NotEqual(IncrementalStateKey.Create(state), IncrementalStateKey.Create(changed));
            Assert.NotEqual(AnalysisService.ComputeFingerprint(state), AnalysisService.ComputeFingerprint(changed));
        }
        SeatState[] seatChanges =
        [
            state.Seats[1] with { Score = 30000 }, state.Seats[1] with { Riichi = true },
            state.Seats[1] with { DiscardCount = 4 }, state.Seats[1] with { DiscardsVerified = false },
            state.Seats[1] with { RiichiDiscardIndex = 0 }, state.Seats[1] with { DiscardOrder = [2] },
            state.Seats[1] with { Discards = [Tile.Parse("5m")] },
            state.Seats[1] with { Melds = [Meld.MakeKan(Tile.Parse("2z"), MeldType.Ankan)] },
        ];
        foreach (var seat in seatChanges)
        {
            var seats = state.Seats.ToArray();
            seats[1] = seat;
            var changed = state with { Seats = seats };
            Assert.NotEqual(Identity(state), Identity(changed));
            Assert.NotEqual(AnalysisService.ComputeFingerprint(state), AnalysisService.ComputeFingerprint(changed));
        }
    }

    [Fact]
    public void Beliefs_and_policy_profile_are_part_of_identity()
    {
        var state = State();
        var beliefs = BeliefState.Capture(state, new OpponentModel()).ToArray();
        var before = IncrementalStateKey.Create(state, beliefs, Profile);
        beliefs[0] = beliefs[0] with { Tenpai = 0.9 };
        Assert.NotEqual(before, IncrementalStateKey.Create(state, beliefs, Profile));
        Assert.NotEqual(before, IncrementalStateKey.Create(state, beliefs, "new policy"));
    }

    [Fact]
    public void Complete_hit_selects_highest_utility_without_running_fallback()
    {
        var state = State();
        var artifact = Artifact(state, Actions(state));
        var fallback = new CountingPolicy();
        var policy = new PrecomputedPolicy(fallback, new PolicyTable(artifact, Profile));
        var result = policy.Choose(state, default);
        Assert.Equal(ActionKind.Discard, result.Kind);
        Assert.Equal(Tile.Parse(artifact.Entries[0].Actions[^1].Tile), result.Tile);
        Assert.Equal(0, fallback.Calls);
        Assert.Equal(DiscardActionSpace.Generate(state).Count, result.Candidates.Count);
        Assert.Contains("Exact belief-state hit", result.Steps[0].Display);
        Assert.NotNull(result.Hand);
    }

    [Fact]
    public void Miss_insufficient_coverage_samples_or_illegal_actions_fall_back()
    {
        var state = State();
        var valid = Artifact(state, Actions(state));
        PolicyArtifact[] artifacts =
        [
            valid with { Entries = [] },
            Artifact(state, Actions(state)[1..]),
            Artifact(state, Actions(state).Select(a => a with { Visits = 7 }).ToArray()),
            Artifact(state, Actions(state).Select((a, i) => i == 0 ? a with { Tile = "7z", MeanUtility = 48000 } : a).ToArray()),
            valid with { Entries = [valid.Entries[0] with { State = valid.Entries[0].State with { Verification = new string('0', 64) } }] },
        ];
        foreach (var artifact in artifacts)
        {
            var fallback = new CountingPolicy();
            var policy = new PrecomputedPolicy(fallback, new PolicyTable(artifact, Profile));
            Assert.Equal(ActionKind.Pass, policy.Choose(state, default).Kind);
            Assert.Equal(1, fallback.Calls);
        }
    }

    [Fact]
    public void Non_discard_actions_and_unverified_states_always_use_existing_policy()
    {
        var state = State();
        var fallback = new CountingPolicy();
        var policy = new PrecomputedPolicy(fallback, new PolicyTable(Artifact(state, Actions(state)), Profile));
        foreach (var legal in new[] { LegalAction.Ron, LegalAction.Tsumo, LegalAction.Chi, LegalAction.Discard | LegalAction.Riichi,
                     LegalAction.Discard | LegalAction.AnKan, LegalAction.None })
        {
            Assert.Empty(DiscardActionSpace.Generate(state with { Legal = legal }));
            Assert.Equal(ActionKind.Pass, policy.Choose(state with { Legal = legal }, default).Kind);
        }
        Assert.Empty(DiscardActionSpace.Generate(state with { OurRiichi = true }));
        Assert.Empty(DiscardActionSpace.Generate(state with { DrawnTile = null }));
        Assert.Empty(DiscardActionSpace.Generate(state with { LayoutHealthy = false }));
        Assert.Empty(DiscardActionSpace.Generate(state with { WallRemaining = 0 }));
        Assert.Empty(DiscardActionSpace.Generate(state with { Hand = state.Hand.Take(13).ToArray() }));
    }

    [Fact]
    public void Red_and_plain_five_are_distinct_legal_alternatives()
    {
        var state = PolicyFixtures.Snap("123m456m045p678s11z");
        var legal = DiscardActionSpace.Generate(state);
        Assert.Contains(Tile.Parse("0p"), legal);
        Assert.Contains(Tile.Parse("5p"), legal);
    }

    [Fact]
    public void Table_rejects_incompatible_duplicate_and_invalid_data()
    {
        var state = State();
        var artifact = Artifact(state, Actions(state));
        Assert.Throws<InvalidDataException>(() => new PolicyTable(artifact with { Schema = 99 }, Profile));
        Assert.Throws<InvalidDataException>(() => new PolicyTable(artifact with { Model = "different" }, Profile));
        Assert.Throws<InvalidDataException>(() => new PolicyTable(artifact, "different"));
        Assert.Throws<InvalidDataException>(() => new PolicyTable(artifact with { Entries = [artifact.Entries[0], artifact.Entries[0]] }, Profile));
        Assert.Throws<InvalidDataException>(() => new PolicyTable(Artifact(state, [Actions(state)[0], Actions(state)[0]]), Profile));
        foreach (var action in new[] { Actions(state)[0] with { MeanUtility = double.NaN }, Actions(state)[0] with { Variance = -1 },
                     Actions(state)[0] with { Tile = "9z" }, Actions(state)[0] with { Visits = 0 } })
            Assert.Throws<InvalidDataException>(() => new PolicyTable(Artifact(state, action), Profile));
    }

    [Fact]
    public void Table_round_trips_and_does_not_expose_mutable_storage()
    {
        var state = State();
        var artifact = Artifact(state, Actions(state));
        artifact.Entries[0].Actions[0] = artifact.Entries[0].Actions[0] with { Waits = ["1m"] };
        var table = new PolicyTable(artifact, Profile);
        artifact.Entries[0].Actions[0].Waits[0] = "9m";
        Assert.True(table.TryGet(Identity(state), out var rows));
        Assert.Equal("1m", rows[0].Waits[0]);
        rows[0].Waits[0] = "2m";
        Assert.True(table.TryGet(Identity(state), out rows));
        Assert.Equal("1m", rows[0].Waits[0]);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            PolicyTable.Save(path, artifact);
            Assert.True(PolicyTable.Load(path, Profile).TryGet(Identity(state), out var loaded));
            Assert.Equal(artifact.Entries[0].Actions.Length, loaded.Count);
            File.WriteAllText(path, "{broken");
            Assert.Throws<JsonException>(() => PolicyTable.Load(path, Profile));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Parallel_decisions_do_not_mix_state_hashes_or_beliefs()
    {
        var a = State();
        var b = a with { Honba = 1 };
        var first = Artifact(a, Actions(a));
        var second = Artifact(b, Actions(b).Reverse().Select((row, i) => row with { MeanUtility = i * 100 }).ToArray());
        var fallback = new CountingPolicy();
        var policy = new PrecomputedPolicy(fallback, new PolicyTable(first with { Entries = [.. first.Entries, .. second.Entries] }, Profile));
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() =>
        {
            var state = i % 2 == 0 ? a : b;
            var expected = i % 2 == 0 ? Actions(a)[^1].Tile : Actions(b)[0].Tile;
            Assert.Equal(Tile.Parse(expected), policy.Choose(state, default).Tile);
        })));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public void Cancellation_is_honored_before_lookup_or_fallback()
    {
        var state = State();
        var fallback = new CountingPolicy();
        var policy = new PrecomputedPolicy(fallback, new PolicyTable(Artifact(state, Actions(state)), Profile));
        Assert.Throws<OperationCanceledException>(() => policy.Choose(state, new CancellationToken(true)));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public void Snapshot_corpus_preserves_red_melds_rules_and_chronology()
    {
        var meld = new Meld(MeldType.Pon, [Tile.Parse("5m"), Tile.Parse("5m"), Tile.Parse("5m")], true);
        meld.Tiles[0] = Tile.Parse("0m");
        var state = State() with { OurMelds = [meld], Ruleset = new RulesetOptions(false, 4), HandNumber = 3 };
        state = PolicyFixtures.Seat(state, discards: "0p9s", riichi: true, riichiIndex: 1, melds: [meld]);
        var seats = state.Seats.ToArray();
        seats[1] = seats[1] with { DiscardOrder = [3, 7] };
        state = state with { Seats = seats };
        var restored = SnapshotJson.Deserialize(SnapshotJson.Serialize(state));
        Assert.Equal(Identity(state), Identity(restored));
        Assert.Contains(restored.OurMelds[0].Tiles, t => t.IsRedFive);
        Assert.Equal(state.Ruleset, restored.Ruleset);
    }

    [Fact]
    public void Offline_training_is_reproducible_covers_actions_and_can_be_used_at_runtime()
    {
        var state = State();
        var trainer = new OfflineDiscardTrainer();
        var options = new TrainingOptions(112, 2, 42);
        var first = trainer.Train([state, state with { Sequence = 400 }], options);
        var second = trainer.Train([state], options);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        var entry = Assert.Single(first.Entries);
        Assert.Equal(112, entry.Actions.Sum(a => a.Visits));
        Assert.All(entry.Actions, a => { Assert.True(a.Visits >= 8); Assert.True(double.IsFinite(a.MeanUtility)); Assert.True(a.Variance >= 0); });
        Assert.Contains(entry.Actions, a => a.Variance > 0);
        var fallback = new CountingPolicy();
        var result = new PrecomputedPolicy(fallback, new PolicyTable(first, Profile)).Choose(state, default);
        Assert.Equal(ActionKind.Discard, result.Kind);
        Assert.Equal(0, fallback.Calls);
        Assert.Equal(entry.Actions.Max(a => a.MeanUtility), result.Candidates[0].ExpectedValue);
    }

    [Fact]
    public void Offline_training_rejects_impossible_tiles_unsupported_states_and_bad_budgets()
    {
        var trainer = new OfflineDiscardTrainer();
        Assert.Throws<ArgumentOutOfRangeException>(() => trainer.Train([State()], new TrainingOptions(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => trainer.Train([State()], new TrainingOptions(Horizon: 0)));
        Assert.Throws<ArgumentException>(() => trainer.Train([State() with { Legal = LegalAction.Ron }], new TrainingOptions()));
        var state = State() with { DoraIndicators = Enumerable.Repeat(Tile.Parse("1m"), 4).ToArray() };
        Assert.Throws<ArgumentException>(() => trainer.Train([state], new TrainingOptions()));
        Assert.Throws<ArgumentException>(() => trainer.Train([], new TrainingOptions()));
        Assert.Throws<OperationCanceledException>(() => trainer.Train([State()], new TrainingOptions(), new CancellationToken(true)));
    }

    [Fact]
    public void Recorder_deduplicates_positions_and_preserves_advice()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            var fallback = new CountingPolicy();
            var recorder = new SnapshotRecordingPolicy(fallback, path);
            var state = State();
            Assert.Equal(ActionKind.Pass, recorder.Choose(state, default).Kind);
            recorder.Choose(state with { Sequence = 2 }, default);
            recorder.Choose(state with { Legal = LegalAction.Ron }, default);
            recorder.Choose(state with { Honba = 1 }, default);
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Equal(Identity(state), Identity(SnapshotJson.Deserialize(lines[0])));
            Assert.Equal(4, fallback.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Recorder_reports_a_write_failure_once_and_continues_advice()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            File.WriteAllText(path, "locked");
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var errors = 0;
            var fallback = new CountingPolicy();
            var recorder = new SnapshotRecordingPolicy(fallback, path, _ => errors++);
            Assert.Equal(ActionKind.Pass, recorder.Choose(State(), default).Kind);
            Assert.Equal(ActionKind.Pass, recorder.Choose(State(), default).Kind);
            Assert.Equal(1, errors);
            Assert.Equal(2, fallback.Calls);
        }
        finally { File.Delete(path); }
    }

    private sealed class CountingPolicy : IPolicy
    {
        public int Calls;
        public ActionChoice Choose(StateSnapshot state, CancellationToken ct)
        {
            Interlocked.Increment(ref this.Calls);
            return ActionChoice.Pass("fallback sentinel");
        }
    }
}
