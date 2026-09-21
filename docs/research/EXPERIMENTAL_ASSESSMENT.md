# Assessment of the `experimental` branch — 2026-09-21

Scope: the uncommitted working tree of `experimental` as cloned into `experimentalv2`
(`e375963`), i.e. the hybrid learned-policy / calibrated-opponent-model / offline-search design
inspired by Bakuuchi and NAGA, on top of the defense v2 work (`docs/DEFENSE_PLAN.md`). Everything
below was checked by building, running the tests, and driving the offline pipeline on the
existing artifacts; nothing is taken from the docs alone.

## Verdict in one paragraph

The branch has a real, runnable foundation — a complete four-player Doman rules engine with
information-set MCTS, a resumable generation runner that already produced a 10 000-match /
79 993-record baseline database, a Tenhou `mjlog` importer with provenance and game-disjoint
splits, a feature encoder shared between C# and PyTorch, a CPU-only CNN trainer, C# inference
with a parity check, and plugin wiring behind three opt-in toggles. It is honest about its
limits in the docs. What it does **not** have yet is evidence of playing strength: no model had
ever been trained (the exporter/trainer handshake was broken until this assessment fixed it),
the exact-hash database has **0 % coverage** on real positions, the search labels are shallow and
correlated, and the learned opponent model is uncalibrated against anything but a 20-game
sample. The next step is not more compute; it is a held-out evaluation loop and a few thousand
authorized games.

## What was verified

| Check | Result |
|---|---|
| `dotnet test` (v2 worktree) | 388 / 388 pass (314 defense + 74 new: 32 simulator rule tests, 6 generation, 8 replay, 17 precomputed, plus golden positions) |
| `tools/Precompute` build | succeeds (Release) |
| Baseline generation run (`artifacts/simulator-win-x64/runs/first`) | 10 000 matches, 79 993 records, 7 rejections, median 53 visits on the chosen action, **median best-vs-runner-up gap 278 points** |
| Tenhou pilot (`artifacts/tenhou-competitive`) | 20 Phoenix games, all ranks ≥ 7 dan, 8 599 public decisions, provenance + SHA-256 recorded; **0 exact hits** against the baseline database |
| `learn-data` on the pilot corpus | 8 535 rows (6 774 / 442 / 1 319 by whole game), 2.9 s — **but the manifest lacked `Schema/Features/RowFloats`** (bug, fixed in `69186c3`) |
| `train.py` (8 epochs, CPU) | 6.5 s; test top-1 imitation **38 %**, policy NLL 1.83 vs uniform-legal 2.36; tenpai Brier 0.060, ECE 0.04 on 1 326 validation targets |
| `learn-check` parity | 8 vectors, max |PyTorch − C#| = 6.7e-6 |
| `LearnedPolicy` in-process on the 4 book positions | 13–68 ms per decision; 2 / 4 agree (both defense positions); never declares riichi (riichi is ~1 % of the rows) |
| Learning module unit tests | **none** (`Core/Learning/*` has no tests; the manifest bug is the consequence) |

## Architecture as built

```
Tenhou mjlog ──replay-import──▶ ReplayCorpus ──learn-data──▶ train/val/test .f32 ──train.py──▶ learned_policy.json
                                     │                              ▲                                │
                                     └──replay-search──▶ search run ─┘ (Q labels, optional)          ▼
self-play ──sim-run──▶ run dir ──sim-pack──▶ SimulationDatabase (exact SHA-256 key) ──▶ SimulationLookupPolicy
                                                                                          │
plugin: LearnedPolicy(LearnedOpponentModel) ──fallback──▶ DecisionPolicy (defense v2) ◀───┘
```

- **Simulator** (`Core/Simulation/RiichiSimulator.cs`, 384 dense lines): 136 physical tiles, all kan
  types, furiten variants, kuikae, abortive draws, nagashi, pao, honba/deposits, Doman profile.
  32 rule tests including tile/point conservation over full matches. Not differentially validated
  against a reference engine or client traces.
- **Search** (`InformationSetSearch.cs`): particle beliefs (tenpai-consistent for riichi seats,
  swap-conditioned to the opponent model), UCT over the acting player's information sets, guideline
  or `DecisionPolicy` rollouts to the end of the hand, point-change utility with an optional
  rank-change term. 256 iterations × 16 particles is a smoke budget, not a label budget.
- **Retrieval**: exact key = incremental state hash + verification digest; an "abstract" key
  exists but is research-only. Exact retrieval is structurally hopeless for real play (0 / 8 599).
- **Learning**: 64×34 public-tile feature planes (hand, draw, dora, seen, per-seat ponds with
  order, melds, riichi tile, post-riichi safe tiles, 24 global scalars); a 2-conv + dense CNN
  with 355 outputs (74 policy, 3 tenpai, 102 conditional ron, 102 ron points, 74 search Q);
  Platt calibration for tenpai/wait heads fitted on validation only; masked losses; C# inference
  with schema/dimension guards and a 64 MB cap.
- **Plugin**: `LearnedPolicy` handles closed-hand ordinary draws only (discard/riichi), everything
  else falls back to `DecisionPolicy`; `LearnedOpponentModel` overrides tenpai/danger/value for
  the defense layer with the network's heads. Both are off by default.

## Strengths

1. Engineering discipline: atomic writes, checksums, fingerprints tying datasets to corpora and
   search runs, resumable sharded generation, no privileged inputs leaking into features, explicit
   rejected-sample accounting, rule-profile checks before a model loads.
2. Honest documentation: every doc distinguishes "pipeline completes" from "labels are good", and
   states what is *not* implemented (calls/kans in the learned action space, placement value,
   distillation, Soul replays).
3. The feature encoder is the single source of truth for both sides and the parity check makes
   PyTorch→C# drift detectable.
4. Inference cost (≤ 70 ms CPU) fits the 2 s analysis budget with room for a larger network.
5. The Tenhou authorization context is recorded in provenance rather than assumed.

## Weaknesses and risks, ranked

1. **No evaluation loop.** Nothing measures playing strength or decision quality end to end:
   no held-out agreement report for `DecisionPolicy` vs human choices (the approach doc's step 1),
   no match-level A/B of learned vs heuristic policy in the simulator, no calibration comparison
   between `TenpaiEstimator` and the learned tenpai head on the same rows. Until this exists,
   every other number is a proxy.
2. **Search labels are weak.** Median 53 visits, 16 correlated particles, guideline rollouts, and a
   278-point median gap between the top two actions: the argmax is noise-dominated for most
   records. Using these as Q targets (`search Q` head) risks distilling noise; the trainer treats
   them as optional, which is right — keep them optional until a label-stability study (same
   positions, different seeds/budgets) shows agreement.
3. **Data volume.** 20 games / 6.7 k rows is two orders of magnitude short of an imitation policy
   that generalizes (NAGA-class work uses millions of decisions). Riichi and call decisions are
   too rare to learn at this scale — the model never riichis. The archive on hand has 1 696
   entries; the pilot used 20. The next unlock is importing the whole archive (and more), not
   tuning.
4. **Learned action space is narrower than the runtime.** Calls, kans, wins and open hands all
   fall back to the heuristics, so the learned policy can only change closed-hand discards; the
   defensive gains promised by "human defensive choices" apply only there.
5. **No tests for `Core/Learning`.** The manifest bug shows the cost. Needed: encode/decode
   golden vectors, dataset row layout, `LearnedModel` validation errors, `LearnedPolicy` fallback
   conditions, `LearnedOpponentModel` "known safe" masking.
6. **Placement value is missing.** Utility is hand point change (+ optional rank term). Doman's
   objective and the all-last behaviour the Riichi City regulars insist on
   (`docs/research/wwyd_sweep.md`) are not represented; the approach doc knows this.
7. **Precomputed/exact-lookup path is dead weight for live play.** 0 % coverage on real positions
   is not fixable by more generation. Keep the database as a label corpus; do not ship the lookup
   toggle as a feature.
8. **Legal/policy interplay for opponents.** The belief sampler conditions on the *heuristic*
   opponent model's tenpai estimate; when the learned opponent model replaces it at runtime, the
   offline labels and the live beliefs disagree. Regenerate or re-weight after the opponent
   model changes.

## Recommended next steps (in order)

1. **Evaluation harness first** (`Precompute learn-eval` or a test): per split, report action
   agreement of `DecisionPolicy`, `LearnedPolicy` and the learned+heuristic hybrid with human
   choices by decision type (discard / riichi / fold-vs-riichi), plus tenpai/ron calibration of
   `TenpaiEstimator`+`TileDangerModel` versus the learned heads on the same rows. Add the deal-in
   CSV rows from live play (`tools/fit_danger.py`) as a third population.
2. **Import the whole 1 696-entry archive** (and additional dates) with rank ≥ 7 dan; re-run
   `learn-data` + `train.py` at 20–40 epochs; report the same metrics. Expect top-1 to move from
   ~38 % into the 55–65 % range before the learned discard policy is worth enabling.
3. **Unit-test the learning module** (encoder goldens, dataset layout, model guards, fallbacks).
4. **Label-stability pilot** on ~200 fixed human positions: baseline budget vs 4 096 iterations /
   32 particles / 16 minimum visits, three seeds; keep search Q labels only where the argmax is
   stable. Then decide whether a deeper run is worth the two-box budget.
5. **Add the placement head** (final placement distribution from public context; Suphx-style) and
   use it in both the search utility and the push/fold budget for all-last.
6. **Simulator A/B**: matched seeds and seat rotations, heuristic vs learned vs hybrid, report
   placement, deal-in rate and point return with confidence intervals. This is the only strength
   number that should go in the README.

## Relationship to the defense v2 work

The two efforts compose rather than compete: `LearnedOpponentModel` implements `IOpponentModel`
and plugs straight into the danger budget / betaori layer, and `LearnedPolicy` falls back to
`DecisionPolicy` everywhere its action space stops. The right runtime for the next months is the
hybrid: heuristic defense (measured tables, explainable) with the learned heads replacing the
tenpai/danger estimates once their calibration beats the tables on held-out rows.
