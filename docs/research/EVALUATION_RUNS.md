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

3.03 M training rows (36 GB); ≈ 22 min/epoch on 12 threads. Epoch 1 validation loss 1.118
(already below run 3's best 1.155). Full run and its test-split evaluation: pending.
