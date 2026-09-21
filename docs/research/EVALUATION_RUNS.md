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
| n30 (→ 2026-05) | 11 574 | (importing) | | 6dc6bacd…0aa05c |

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
