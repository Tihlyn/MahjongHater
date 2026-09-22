# Evaluation runs

Recorded runs of `Precompute learn-eval` / `learn-fit-tenpai` (see `EVALUATION.md`). Corpora
are Tenhou Phoenix archives from <https://tenhou.net/ranking.html> (`mjlog_pf4-20_n*.zip`),
imported with acting-player rank ≥ 7 dan and the Doman rule profile; the project owner
reports Tenhou raised no concerns about this use. Raw archives, corpora, datasets and models
live under `artifacts/` (gitignored); SHA-256 of the archives:

| archive | games in zip | imported (≥ 7 dan, East-South) | decisions | sha256 |
|---|---:|---:|---:|---|
| n24 (2023-07 → 2024-11) | 1 696 | 1 431 | 676 571 | c3bb2032…ab72a5f |
| n28 (→ 2025-10) | 3 181 | 1 681 | 785 327 | 9a24748d…7fc24a5 |
| n29 (→ 2026-01) | 4 560 | 3 832 | 1 881 154 | 97d7836b…db22bc |
| n30 (→ 2026-05) | 11 574 | 9 563 | 4 534 857 | 6dc6bacd…0aa05c |
| combined (`corpus-all`, deduplicated) | 21 011 | 16 479 | 7 864 306 | |

Rejected files are East-only games or games whose acting players are all below 7 dan.

## Run 1 — n24 test split, heuristics only (2026-09-21, before the tenpai refit)

172 held-out games, 83 752 decisions. `heuristic` = defense v2 with the analyzer's
within-shanten ordering, `heuristic-ev` = point-EV ordering, `legacy` = v1.3.

| | heuristic | heuristic-ev | legacy |
|---|---:|---:|---:|
| agreement, all | 49.8 % | 46.1 % | 50.7 % |
| agreement, vs-riichi (n=14 931) | 55.3 % | 57.1 % | 51.6 % |
| agreement, quiet (n=58 924) | 48.6 % | 43.5 % | 50.2 % |
| agreement, tenpai (n=10 072) | 64.0 % | 63.9 % | 67.6 % |
| chosen-tile deal-in, all (human 0.94 %) | 0.79 % | 0.61 % | 1.09 % |
| chosen-tile deal-in, vs-riichi (human 2.18 %) | 1.91 % | 1.55 % | 2.83 % |

Conclusions: the point-EV ordering is the most defensive but imitates human efficiency
worst → analyzer ordering stays the default (`RankByPointEv = false`). The danger tables are
well calibrated on Phoenix (vs-riichi all kinds: Brier 0.045, ECE 0.005, mean 4.5 % vs
5.0 % observed). The tenpai estimate was badly off for non-riichi seats: closed 10.7 %
predicted vs 2.5 % observed (ECE 0.082), open 44 % vs 34 %.

## Refit — `learn-fit-tenpai` on the n24 train split

1 469 838 non-riichi opponent rows, 8.24 % tenpai. Log-loss 0.2287 → 0.1812, Brier
0.0652 → 0.0526. Fitted: intercept −4.861, per discard 0.229, per open meld 1.197, early
outside −0.085, late middle 0.137 (now the `PolicyWeights` defaults). Reliability by discard
count (predicted / observed): 0-6 2.3 / 1.3 %, 7-10 9.6 / 11.8 %, 11-14 22.5 / 24.3 %,
15-18 42.4 / 35.6 %.

## Run 2 — n24 test split after the refit

| | heuristic | heuristic-ev | legacy |
|---|---:|---:|---:|
| agreement, all | **51.0 %** | 48.5 % | 51.0 % |
| agreement, vs-riichi | **57.5 %** | 60.2 % | 52.0 % |
| agreement, vs-open (n=9 897) | 51.0 % | 47.4 % | 52.6 % |
| agreement, quiet | 49.3 % | 45.7 % | 50.4 % |
| agreement, tenpai | 67.4 % | 67.4 % | 67.8 % |
| agreement, 1-shanten (n=24 907) | **57.8 %** | 54.9 % | 55.0 % |
| agreement, late (n=14 067) | **57.0 %** | 57.2 % | 55.3 % |
| chosen-tile deal-in, all (human 0.94 %) | 0.84 % | 0.65 % | 1.08 % |
| chosen-tile deal-in, vs-riichi (human 2.18 %) | **1.94 %** | 1.51 % | 2.66 % |
| chosen-tile deal-in, late (human 2.61 %) | 2.35 % | 1.88 % | 3.49 % |
| tenpai calibration, closed (ECE) | 0.013 | | (0.082 before) |
| tenpai calibration, open (ECE) | 0.066 | | (0.102 before) |

Conclusions: defense v2 now matches legacy's overall human agreement while agreeing far more
often against a riichi and cutting its counterfactual deal-in rate below the Phoenix humans'
own. Remaining gaps to humans: quiet-board efficiency at 2+-shanten (44 % vs 45 %) and
open-hand situations (51 % vs 53 %), where the open-seat tenpai estimate is now slightly
under-confident (29 % vs 34 %).

## Run 3 — first real model: `dataset-n24` (all 1 431 games), c48/h128, 12 epochs

`learn-data` 671 k rows (519 k train), `train.py --channels 48 --hidden 128`, 5 min on CPU
(≈ 10 s/epoch once the memmap is warm). Validation loss 1.39 → 1.155 (plateau from epoch 10).
Test: policy top-1 **65.2 %**, NLL 0.979 (uniform-legal 2.363), tenpai Brier 0.050,
PyTorch/C# parity 5.7e-6.

## Run 4 — n24 test split with the run-3 model, all policies

| | heuristic | legacy | learned | hybrid | hybrid-tenpai | **learned-guarded** |
|---|---:|---:|---:|---:|---:|---:|
| agreement, all | 51.0 % | 51.0 % | 63.6 % | 54.8 % | 50.7 % | **61.6 %** |
| agreement, human-riichi (n=1 331) | 85.6 % | 80.3 % | 58.1 % | 86.9 % | 85.7 % | 71.9 % |
| agreement, vs-riichi | 57.5 % | 52.0 % | 61.6 % | 58.2 % | 57.3 % | 60.6 % |
| agreement, quiet | 49.3 % | 50.4 % | 64.7 % | 54.0 % | 49.2 % | 63.3 % |
| agreement, tenpai | 67.4 % | 67.8 % | 73.2 % | 69.3 % | 67.0 % | 65.4 % |
| agreement, 2+-shanten | 44.1 % | 45.4 % | 61.3 % | 48.5 % | 44.1 % | 60.9 % |
| chosen-tile deal-in, vs-riichi (human 2.18 %) | 1.94 % | 2.66 % | 2.08 % | 2.42 % | 1.93 % | **1.53 %** |
| chosen-tile deal-in, vs-open (human 2.22 %) | 2.19 % | 3.11 % | 2.61 % | 2.56 % | 2.12 % | 1.89 % |
| chosen-tile deal-in, late (human 2.61 %) | 2.35 % | 3.49 % | 2.54 % | 2.69 % | 2.32 % | 2.00 % |
| tenpai calibration closed / open (ECE) | 0.013 / 0.066 | | 0.0045 / 0.043 (learned head) | | | |
| danger calibration vs-riichi in-hand (Brier) | 0.0648 (tables) | | 0.0658 (learned head) | | | |

Policies: `learned` = `LearnedPolicy` (pure imitation, learned opponent model); `hybrid` =
`DecisionPolicy` with the full learned opponent model; `hybrid-tenpai` = learned tenpai head,
table danger; `learned-guarded` = `LearnedDiscardPolicy` (imitation ordering, discard + riichi
mass summed per tile) under the danger budget with `hybrid-tenpai` opponents.

Conclusions:
- Pure imitation reproduces human efficiency (64 % on quiet boards, 73 % at tenpai) and human
  risk (2.08 % deal-in), and under-declares riichi (riichi is ~1.6 % of rows).
- The learned tenpai head beats the refit logistic (closed ECE 0.0045 vs 0.013); the learned
  danger head does **not** beat the Houou tables (in-hand Brier 0.0658 vs 0.0648) and the
  full hybrid deals in more (2.42 %) — keep the tables for danger.
- `learned-guarded` gets most of the imitation gain (61.6 %) with the lowest deal-in rate of
  every variant (1.53 % vs riichi, 30 % below the humans themselves). It is now what the
  plugin runs when "Experimental learned policy" is on. Its riichi agreement (72 %) is bounded
  by `RiichiPolicy`, not the network.

## Run 5 — `dataset-all-8k`: 8 000 games from the combined corpus, c96/h256

3.03 M training rows (36 GB). The first attempt was killed by system memory pressure: the
trainer memory-mapped the whole file and copied entire splits for evaluation. `train.py` now
streams shuffled chunks through a bounded buffer and accumulates metrics per batch
(`--buffer-rows`, `--memory-log`); resumed from the epoch-1 checkpoint at **1.1 GB RSS
(peak 1.8 GB)**, ≈ 20 min/epoch on 10 threads. Validation loss 1.118 → 1.084 → 1.063 over
epochs 1–3 (run-3 model's best: 1.155). Final metrics and test-split evaluation: pending.

## Run 6 — AUC of the opponent models (60 test games, run-3 model)

Added a streaming AUC to the calibration cells so the waiting model can be compared with
Bakuuchi's published 0.777.

| estimator | view | AUC | Brier | ECE | n |
|---|---|---:|---:|---:|---:|
| tenpai / refit logistic | closed | **0.867** | 0.0203 | 0.017 | 66 530 |
| tenpai / refit logistic | open | 0.757 | 0.189 | 0.064 | 16 026 |
| tenpai / learned head | closed | 0.851 | 0.0201 | 0.007 | 66 530 |
| tenpai / learned head | open | 0.745 | 0.190 | 0.051 | 16 026 |
| danger / Houou tables | vs-riichi, all kinds | **0.799** | 0.0442 | 0.005 | 190 876 |
| danger / Houou tables | vs-riichi, in-hand | 0.733 | 0.0636 | 0.010 | 56 851 |
| danger / learned head | vs-riichi, all kinds | 0.767 | 0.0448 | 0.004 | 190 876 |
| danger / learned head | vs-riichi, in-hand | 0.685 | 0.0647 | 0.006 | 56 851 |

The tables discriminate winning tiles clearly better than the learned head; the learned tenpai
head is better calibrated but slightly worse at ranking than the refit logistic. Both
waiting models are in the class of Bakuuchi's (different data and era, so not a strict
comparison). See `STRENGTH_COMPARISON.md`.

## Run 5, continued — c96/h256 finished

12 epochs (best validation loss 1.0296 at epoch 10), 45 min for the last two epochs at
1.1 GB RSS. Trainer test split (383 k rows): **policy top-1 69.1 %** (run 3: 65.2 %),
tenpai Brier 0.048. Harness evaluation on the v2 corpus: run 7.

## Corpus v2 — claim-window reactions (`corpus-all-v2`, importer `tenhou-decisions-v2`)

Same four archives re-imported with the reaction windows: 16 479 games, **9 942 310
decisions** (7 864 306 turn + 2 078 004 reactions), 9 min on 10 workers. The earlier
corpora (`tenhou-turn-decisions-v1`) no longer load; runs 1–6 stay as recorded. Runs 1–6
used the n24-only test split; from run 7 the test split is drawn from all four archives
(the split is by game hash, so the n24 test games are a subset of it).

## Run 7 — `corpus-all-v2` test split, c96/h256 (v1 features, 3 M rows)

1 500 held-out games, 902 564 decisions (713 820 turn, 188 744 claim-window reactions);
55 min on 10 threads. The model has no reaction actions, so every policy answers claim windows
with the heuristic `CallPolicy`.

| | human | heuristic | heuristic-ev | legacy | learned | hybrid | hybrid-tenpai | learned-guarded |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| agreement, all (n=713 820) | — | 51.8 % | 49.3 % | 51.8 % | **66.3 %** | 54.1 % | 51.7 % | 64.1 % |
| agreement, vs-riichi (n=127 794) | — | 56.4 % | 59.5 % | 52.6 % | 65.3 % | 56.9 % | 56.2 % | 63.1 % |
| agreement, human-riichi (n=11 218) | — | 85.5 % | 85.0 % | 79.9 % | 62.9 % | 84.0 % | 85.1 % | 76.4 % |
| agreement, tenpai (n=85 413) | — | 66.7 % | 66.9 % | 66.7 % | 74.1 % | 67.0 % | 66.1 % | 65.5 % |
| chosen-tile deal-in, all | 0.92 % | 0.90 % | 0.70 % | 1.13 % | 0.87 % | 0.89 % | 0.85 % | **0.75 %** |
| chosen-tile deal-in, vs-riichi | 2.03 % | 2.05 % | 1.62 % | 2.72 % | 1.82 % | 2.16 % | 2.04 % | **1.52 %** |
| chosen-tile deal-in, late | 2.29 % | 2.36 % | 1.91 % | 3.51 % | 2.18 % | 2.30 % | 2.20 % | **1.82 %** |
| reaction agreement (n=188 744) | — | 81.1 % (all policies: heuristic calls) |
| call rate on claim windows | 16.3 % | 23.6 % (all policies) |

Calibration (n = 4.7 M riichi rows, 1.6 M closed non-riichi seats):

| estimator | view | AUC | Brier | ECE |
|---|---|---:|---:|---:|
| tenpai / refit logistic | closed / open | 0.850 / 0.769 | 0.0230 / 0.185 | 0.014 / 0.059 |
| tenpai / learned head | closed / open | **0.852 / 0.782** | **0.0221 / 0.175** | **0.004 / 0.038** |
| danger / Houou tables | vs-riichi all-kinds / in-hand | **0.803 / 0.732** | 0.0455 / 0.0666 | 0.006 / 0.012 |
| danger / learned head | vs-riichi all-kinds / in-hand | 0.782 / 0.705 | 0.0460 / 0.0672 | 0.004 / 0.006 |

Conclusions: with 3 M rows the learned tenpai head now beats the refit logistic on ranking
*and* calibration (run 6 had it slightly behind on AUC), so `hybrid-tenpai` / `learned-guarded`
keep it; the danger tables still discriminate better than the learned head. `learned-guarded`
is the strongest runtime configuration on every defensive number (25 % fewer deal-ins than
the humans against a riichi at 63 % agreement). The two remaining gaps are exactly the ones
the v2 stack targets: riichi declaration (the network under-declares, 62.9 % of human
riichis) and calls (heuristic over-calls, 23.6 % vs 16.3 %).

## Run 8 — first v2 model (`model-res`: 8×160 residual, 11.4 M rows, RTX 3080) on the 107 k-game corpus

Data: all 30 Phoenix archives imported on the training box (106 964 hanchan games, 63.7 M
decisions, corpus `88F5E78F5159`); dataset = 24 000 of them (11.4 M train rows), 20 epochs,
best validation loss at epoch 8 (1.1257; overfitting afterwards → `--dropout 0.1` and more
games for the next run). Trainer test split (1.44 M rows): **policy top-1 73.0 %**
(v1 c96/h256: 69.1 %), **reaction top-1 91.9 %**, tenpai Brier 0.048 / ECE 0.0016,
conditional-ron Brier 0.042 / ECE 0.001, placement top-1 46.9 % (current-rank baseline
47.1 %). C# inference 8.6 ms per position. The package's `check` stage failed on this run
because that script version computed the parity vectors on the GPU, where cuDNN convolutions
default to TF32 (2.6e-3 error); recomputed in fp32 on the CPU: 3.8e-6 (fixed in `1236aa7`).

Harness, 1 500 held-out games, 887 102 decisions (702 122 turn, 184 980 reactions), 103 min
on 14 threads:

| | human | heuristic | legacy | learned | learned-guarded |
|---|---:|---:|---:|---:|---:|
| agreement, all (n=702 122) | — | 52.3 % | 52.3 % | **69.3 %** | 66.4 % |
| agreement, human-riichi (n=10 770) | — | 86.0 % | 80.3 % | 74.2 % (v1: 62.9 %) | 85.6 % |
| agreement, riichi-choice (n=29 325) | — | 45.0 % | 45.4 % | **81.7 %** | 48.7 % |
| agreement, vs-riichi (n=123 078) | — | 56.3 % | 52.4 % | 69.8 % | 65.8 % |
| reaction agreement (n=184 980) | — | 81.1 % | 81.1 % | 81.1 % | **90.3 %** |
| reaction, human passed (n=154 563) | — | 84.5 % | 84.5 % | 84.5 % | 97.8 % |
| reaction, human called (n=30 417) | — | 63.6 % | 63.6 % | 63.6 % | 52.5 % |
| call rate on claim windows | 16.4 % | 23.7 % | 23.7 % | 23.7 % | 10.7 % |
| chosen-tile deal-in, vs-riichi | 2.05 % | 2.06 % | 2.85 % | 1.91 % | **1.70 %** |
| chosen-tile deal-in, all | 0.93 % | 0.89 % | 1.12 % | 0.86 % | 0.78 % |

Placement head (learned top-1 / NLL vs current-rank top-1): all 46.8 % / 1.124 vs 47.4 %;
South 3+ 62.3 % / 0.884 vs 63.0 %; all-last 72.3 % / 0.680 vs 73.2 %. Top-1 does not beat
the "finish where you stand" rule, but the probabilities do: a rank baseline calibrated to its
own accuracy has NLL 1.27 overall and 0.875 at all-last, so the head carries information beyond
the standing — which is what the placement-stakes factor consumes.

Calibration: learned tenpai head AUC **0.871 / 0.808** closed / open (refit logistic 0.848 /
0.772), ECE 0.003 / 0.018; learned danger head now level with the Houou tables (vs-riichi
all-kinds AUC 0.799 vs 0.806, vs-tenpai-no-riichi **0.802 vs 0.792**) and better calibrated
(ECE 0.0025 vs 0.0056).

Conclusions: (1) the learned call gate is now the under-caller: with `LearnedCallPassThreshold
= 0.5` plus the heuristic's veto, learned-guarded calls on 10.7 % of windows against the human
16.4 %, and declines half of the calls humans made — the threshold and the veto need tuning on
the validation split (next); (2) riichi declaration is fixed in the raw policy (81.7 % of
riichi choices right) but learned-guarded still takes the heuristic's riichi decision — worth
switching once the call gate is tuned; (3) with the danger head level with the tables,
`useLearnedDanger` becomes a fair A/B candidate.

## Run 8b — learned call gate sweep (validation split, 300 games, 37 833 claim windows)

`learn-eval … validation 300 14 <weights.json> reactions-only` with the `model-res` model.
Two gate changes were needed first: the pass threshold had no effect above 0.5 (the gate also
required the call to be the argmax, which pass ≥ 0.5 already implies) — it is now the pass
probability alone; and `LearnedCallTrust = 1` lets the network take any yaku-viable meld of
its kind/shape (`CallPolicy.Viable`) instead of only calls that lower shanten. Humans call on
16.7 % of these windows; the heuristic on 23.7 % (agreement 81.0 %, human-call agreement
63.0 %).

| threshold | trust | agreement | human passed | human called | call rate |
|---:|---:|---:|---:|---:|---:|
| 0.5 | 0 | 90.2 % | 97.8 % | 52.2 % | 10.7 % |
| 0.7 | 0 | 89.9 % | 96.5 % | 57.2 % | 12.8 % |
| 0.9 | 0 | 88.1 % | 93.5 % | 61.1 % | 15.9 % |
| 0.5 | 1 | **91.1 %** | 97.1 % | 61.1 % | 12.8 % |
| 0.6 | 1 | 90.9 % | 96.2 % | 64.6 % | 14.2 % |
| **0.7** | **1** | 90.6 % | 95.1 % | **68.1 %** | **15.7 %** |
| 0.8 | 1 | 89.7 % | 93.4 % | 71.3 % | 17.8 % |
| 0.9 | 1 | 87.4 % | 90.0 % | 74.4 % | 21.2 % |

Adopted: **0.7 / trust 1** — the call rate matches the humans (15.7 % vs 16.7 %) with the
network's decisions on human-call windows (68.1 %) above the heuristic's, at a 0.5-pt cost in
overall agreement against the most conservative setting. Trust never opens a yakuless hand.
