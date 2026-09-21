# Replay evaluation harness

`Precompute learn-eval` scores the runtime policies and opponent models against human
replays. Every recorded decision is replayed as the public observation the human saw; the
hidden targets (opponent tenpai, legal ron tiles and their values) are only used to grade.
This is the evaluation loop that `docs/research/EXPERIMENTAL_ASSESSMENT.md` asked for first.

```powershell
dotnet run --project tools/Precompute -c Release -- learn-eval <replay-corpus> <report.json> [model.json|-] [split=test] [max-games] [threads]
dotnet run --project tools/Precompute -c Release -- learn-fit-tenpai <replay-corpus> [split=train] [max-games]
```

The report prints to the console and is written as JSON. Splits are the corpus's
game-disjoint `train` / `validation` / `test` (or `all`); use `test` for numbers you intend
to quote and `all` only for smoke runs.

## What it measures

**Agreement** (`Agreement[policy][category]`): did the policy choose the human's action, by
category. A decision counts in every category it matches: `all`, `human-discard` /
`human-riichi`, `riichi-available` (a riichi was legal), `riichi-choice` (declare or not,
ignoring the tile), `vs-riichi` / `vs-open` (an opponent with ≥ 2 calls) / `quiet`,
`tenpai` / `1-shanten` / `2+-shanten` (our best shanten after a discard), `early` / `mid` /
`late` (turn ≤ 6 / ≤ 11 / later), `all-last`. Claim-window reactions (importer v2
corpora) are scored separately: `reaction` (agreement = same decision: pass, or the same
call kind and chi shape), `reaction-human-pass` / `reaction-human-call`,
`reaction-chi-available` / `reaction-pon-available`, plus a **call rate on claim windows**
line for the humans and every policy so over- and under-calling show even when agreement
looks fine.

**Deal-in rate of the chosen tile**: using the hidden targets, whether the tile the policy
would have cut was a legal ron for a tenpai opponent *at that moment* — and the same for the
human's actual discard on the same positions. This is the defensive number that matters: it
is counterfactual, exact, and directly comparable across policies and the human baseline.

**Calibration** (`Calibration[estimator][view]`): Brier, log-loss, expected calibration error
(10 bins), n, base rate and mean prediction for
- `tenpai/heuristic` vs `tenpai/learned` on non-riichi seats (`closed` / `open`), and
- `danger/heuristic` vs `danger/learned` — conditional ron probability given the seat is
  tenpai — for every tile kind (`all-kinds`) and for the kinds we actually hold (`in-hand`),
  split by `vs-riichi` and `vs-tenpai-no-riichi`.

Policies: `heuristic` (defense v2, analyzer ordering), `heuristic-ev` (defense v2 with
`RankByPointEv`), `legacy` (v1.3), and with a model `learned` (`LearnedPolicy`), `hybrid`
(`DecisionPolicy` with `LearnedOpponentModel`), `hybrid-tenpai` (learned tenpai head, table
danger) and `learned-guarded` (imitation ordering under the danger budget, learned tenpai,
`LearnedCallPolicy` on claim windows — the plugin's "learned policy" toggle).

## Reading the numbers

- Agreement with Phoenix humans is an imitation proxy, not strength; a policy can be safer
  than the humans and agree less. Compare agreement *and* the chosen-tile deal-in rate.
- The deal-in rate is per decision, including decisions where nobody was tenpai; the
  `vs-riichi` row is the one to watch.
- Calibration rows for `danger/…` use the Phoenix population; the FF14 population is
  measured separately by the live `dealin_calibration.csv` (`tools/fit_danger.py`).

## Refitting the tenpai estimate

`learn-fit-tenpai` fits `TenpaiEstimator`'s logistic on every non-riichi opponent row of the
chosen split (same features as live play), prints shipped vs fitted log-loss/Brier, a
reliability table by discard count and the `PolicyWeights` initializers to paste. It is the
replay-scale twin of `tools/fit_tenpai.py`.

## Runs

See `docs/research/EVALUATION_RUNS.md` for the recorded runs and their conclusions.
