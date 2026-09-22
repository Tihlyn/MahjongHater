# Is the v2 learned policy a release candidate? — assessment before the `all90` run

Written 2026-09-22 while the 90 000-game export/training run (`all90`) is in progress, from
the data in `EVALUATION_RUNS.md` (runs 7, 8, 8b) and `STRENGTH_COMPARISON.md`. The question
to decide once `all90` finishes: **stop chasing imitation points and treat the model as the
candidate for the next release, or keep optimising.**

## 1. Where the numbers stand

All held-out; nothing below was tuned on the test games. "Model" = `model-res`
(8×160 residual, 11.4 M rows, best epoch 8).

| what | model | heuristic (v1.3 → defense v2) | earlier best (v1 c96/h256) | published reference |
|---|---:|---:|---:|---|
| discard / riichi imitation, top-1 | **73.0 %** | 52 % agreement | 69.1 % | Suphx supervised 76.7 %; the CNN Suphx cites 68.8 % |
| riichi choice (declare or not) | **81.7 %** | 45.0 % | (under-declared: 62.9 % of human riichis) | Suphx riichi 85.7 % |
| claim windows (pass / chi / pon / kan) | **91.9 %** top-1; gate 90.6 % agreement at the human call rate | 81.1 %, over-calls (23.7 % vs 16.7 %) | not learned | Suphx chow 95.0 %, pong 91.9 % |
| tenpai (waiting) model, AUC closed / open | **0.871 / 0.808**, ECE 0.003 / 0.018 | refit logistic 0.848 / 0.772 | 0.852 / 0.782 | Bakuuchi 0.777 (mixed) |
| winning-tile (danger) model, AUC vs riichi | 0.799 (ECE 0.0025) | Houou tables **0.806** (ECE 0.0056) | 0.782 | Bakuuchi: no AUC published |
| chosen-tile deal-in vs riichi (humans 2.05 %) | **1.70 %** (learned-guarded) | 2.06 % (v2), 2.85 % (legacy) | 1.52 % on a different test set | — |
| chosen-tile deal-in, all (humans 0.93 %) | **0.78 %** | 0.89 % | — | — |
| final placement, NLL (all / all-last) | 1.124 / 0.680 | rank-by-score baseline 1.27 / 0.875 | not learned | Suphx: GRU reward predictor, no number |
| runtime cost | 8.6–9.6 ms per position, 85 MB artifact, ~1.5 s load | — | 0.5 ms | — |

Read across a row: on every component the learned stack is at or above the previous best,
and on the three prediction models it is at or above the published Bakuuchi numbers. The
gap to Suphx's *supervised* stage is 3.7 points on discards, 4 on riichi, 3 on chi, none on
pon. Suphx's remaining strength (10 dan vs the 8–9 dan of NAGA/Bakuuchi) came from
reinforcement learning on 44 GPUs, not from those points.

## 2. What `all90` can change, and what it cannot

The last run was **data-limited**: validation loss bottomed at epoch 8 and rose while
training loss kept falling. The imitation curve so far is close to log-linear in data:

| rows | top-1 | note |
|---:|---:|---|
| 0.52 M | 65.2 % | v1 features, 2-layer net |
| 3.0 M | 69.1 % | v1 features, c96/h256 |
| 11.4 M | 73.0 % | v2 features, residual 8×160 |
| 54 M (`all90`) | **≈ 75–77 % expected** | + dropout, cosine, same net |

Beyond `all90` the affordable levers are each worth under a point: a deeper net (10×192,
only once data stops being the limit), a second seed / averaging, longer schedules. The
remaining 100 k → 107 k games are noise. In other words `all90` is where the imitation
curve meets its knee on this hardware; the honest expectation is Suphx-supervised
territory on discards and calls, and then diminishing returns.

What no amount of imitation changes: the network copies Phoenix players' *frequencies*; it
does not evaluate positions. That is what path E (runtime EV) and RL would add, and neither
is measurable without a strength test.

## 3. What is not measured yet

Every number above is prediction quality on logs. **Playing strength has not been
measured.** The gap between "candidate" and "ship" is exactly that, and it is one
experiment away:

- The plugin now tags every hand in `hand_results.csv` with the policy in charge
  (`learned-guarded` / `V2` / `Legacy`); `tools/ab_summary.py` groups by it. A batch of
  matches with the learned toggle on and a batch with it off, same population (human or
  NPC), gives win rate, deal-in rate, deal-in while an opponent is in riichi, riichi rate,
  mean point delta and placement per policy. Be honest about its power: per-hand deal-in is
  a ~10 % event, so with 100 hands per arm only a difference of about 8 points is
  distinguishable from noise (1 000 hands per arm: ~2.5 points); mean point delta has a
  standard deviation of several thousand points per hand and is coarser still. Live play
  therefore answers "does it play sensibly and not regress badly", not "is it 2 % better".
- The simulator has no `Learned` seat mode yet; a matched-seed A/B there is the only test
  with the power to see a small strength difference (thousands of matches, no human
  variance) and needs about a day of work.

Other things a candidate carries that a log metric does not show:

- **Rule transfer.** The corpus is Tenhou hanchan (kuitan on, red fives, Tenhou scoring
  details); the plugin plays Doman rules through the same `RulesetOptions`. Yaku and payment
  differences are handled by our own `YakuDetector`/scoring, but the *humans' choices* were
  made under Tenhou rules and point values. Expected to transfer; not proven.
- **Placement stakes** (`PlacementStakesWeight = 1`) drive push/fold from the placement head
  in every hand with scores. The head beats the rank baseline in NLL, not in top-1; the
  stakes factor uses probability differences, which is the right quantity, but it has never
  been watched in play. It is one weight away from off (`PlacementStakesWeight = 0` restores
  the transcribed all-last factors).
- **Load stall**: the 85 MB artifact deserialises synchronously at plugin start (~1.5 s).
  Cosmetic; can move to a background task.
- **Quick Match** has no model until the `-HandsInMatch 4` run; the plugin refuses to load
  the hanchan model for it and falls back to the heuristic, which is fine but is a different
  player.
- The artifact's `Status` string is `experimental-unvalidated`; it is informational only.

## 4. Recommendation

Treat `all90` as the candidate if it clears three bars, then stop optimising imitation:

1. **Trainer metrics not worse than `model-res` on any head** (top-1, reaction, tenpai
   Brier, ron Brier, placement NLL) and `learn-check` parity passes.
2. **Harness on the test split** (`learn-eval`, 1 500 games) shows learned-guarded at or
   above run 8 on agreement and at or below on chosen-tile deal-in vs riichi, with the call
   rate within two points of the human 16.7 %.
3. **Live A/B not worse**: a few hundred hands per arm (learned-guarded vs V2 heuristic,
   human room), deal-in rate and deal-in-vs-riichi not clearly higher, win rate and mean
   delta not clearly lower, judged with the noise above (differences of a few points are
   not a signal either way), plus a read of the reasons the overlay gives for calls and
   folds — that is the check that the imitation transferred to Doman rules.

If all three hold: train the Quick Match model with the same settings, bump
`Directory.Build.props` to 2.0.0, tag, ship both artifacts with the release. Keep in scope
after the release only what a live signal asks for: a push/fold problem → path E; a call
problem → the gate threshold (validation sweep takes minutes); nothing → nothing.

Explicitly out of scope unless the A/B says otherwise: deeper networks, more archives,
reinforcement learning, search-labelled Q heads, ensembling. Each is a fraction of a
point of imitation or an unmeasurable strength claim, and the measurement that would
justify them is the same live A/B.
