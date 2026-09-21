# Where we stand against Bakuuchi, NAGA and Suphx — and what we can afford

Written 2026-09-21 against the primary sources (Mizukami & Tsuruoka, CIG 2015; the Suphx
paper, arXiv 2003.13590; Dwango's NAGA article as summarised in
`MAHJONG_AI_APPROACH.md`). Our numbers are from `EVALUATION_RUNS.md` (n24 held-out split)
unless stated. Hardware assumed: this box — 16 cores, 32 GB (≈ 24 GB usable), no GPU.

## 1. The reference systems, as published

| | Bakuuchi (2015 paper → 2019 commercial) | NAGA (2019 article) | Suphx (2020) |
|---|---|---|---|
| Decision core | Logistic-regression opponent models (**waiting**, **winning tiles**, **hand score**) + Monte-Carlo simulation of "one-player mahjong" with opponents abstracted by the models | Four CNNs (discard / call / riichi / kan) trained on Phoenix logs; decisions largely follow the networks | Five CNNs (discard / riichi / chow / pong / kong), supervised then RL |
| Inputs | Hand-crafted feature vectors (score model: 26 889 features) | Tile planes (34 × channels) | 34 × 838 planes (discard), 34 × 958 (others), of which **100+ look-ahead planes** (DFS over own draws/discards: "discarding tile X can reach a 12 000-point hand replacing 3 tiles…") |
| Network | — (linear models) | CNN | 3×1 conv 256 → **50 residual blocks** (two 3×1 conv 256 each) → 1×1 conv; no pooling |
| Training data | Houou logs 2009-02 → 2013-12 | Phoenix logs | **15 M** state-action pairs (discard), 5 M (riichi), 10 M (chow, pong), 4 M (kong) |
| Beyond imitation | MC simulation at runtime using the predicted waiting / winning-tile / score | — | Policy-gradient RL: 1.5 M self-play games per agent on 44 GPUs × 2 days; **global reward predictor** (2-layer GRU on top-player logs); **oracle guiding**; runtime **pMCPA** |
| Opponent-model quality | Waiting AUC **0.777** (expert human 0.778); score MSE 0.37 (expert 0.40) | — | — |
| Imitation accuracy | (a later CNN cited by Suphx: discard 68.8 %, chow 90.4 %, pong 88.2 %) | — | Discard **76.7 %**, riichi 85.7 %, chow 95.0 %, pong 91.9 %, kong 94.0 % |
| Strength | Rating 1718 on Tenhou (paper); later 9 dan, stable rank 6.59 over 30 516 games | 8 dan, stable rank 6.64 over 9 649 games (10 dan reported in 2024) | 10 dan, stable rank **8.74** over 5 760 games; top humans 7.46 |
| Per-hand win / deal-in (expert room, Suphx Table 5) | 23.1 % / 12.2 % | 22.7 % / 11.4 % | 22.8 % / **10.1 %** |

Two things stand out. Suphx is ~2 dan above the other two mostly because of RL and its runtime
adaptation, but its supervised policy alone is already 8 points more accurate than the earlier
CNN, and the paper attributes much of the state representation's power to the look-ahead
features. Bakuuchi got to 9 dan with *linear* opponent models plus simulation — the structure
we already have.

## 2. Where our best effort stands

| Component | Ours (measured) | Reference | Gap |
|---|---|---|---|
| Imitation policy, discard top-1 | **65.2 %** (c48/h128, 519 k rows); c96/h256 on 3 M rows in progress (epoch 1 val-loss already better) | 76.7 % Suphx; 68.8 % earlier CNN | 3–11 pts: data volume (15 M vs 0.5–3 M), no look-ahead planes, 2 conv layers vs 50 residual blocks |
| Riichi decision | heuristic `RiichiPolicy` 85.6 % of human riichis; learned head under-declares (58 %) | 85.7 % (Suphx riichi model) | at parity via the rules; the network needs riichi-weighted training or its own head |
| Calls (chi / pon / kan) | heuristic only; not in the learned action space or the dataset | 92–95 % learned | whole missing component |
| Waiting / tenpai model | logistic refit: AUC **0.867** closed / 0.757 open, ECE 0.013 / 0.066; learned head: AUC 0.851 / 0.745, ECE 0.0045 / 0.043 | AUC 0.777 (Bakuuchi, mixed non-riichi seats) | same class; ours measured on 82 k Phoenix rows (run 6) |
| Winning-tile (danger) model | Houou tables: AUC **0.80** all kinds / 0.73 in-hand, Brier 0.064; learned head AUC 0.77 / 0.69 | Bakuuchi: 34 logistic models (no AUC published) | tables beat the learned head on discrimination; keep them |
| Hand-score model | rule-based yaku reading + fixed riichi averages; learned points head MAE ≈ 2 950 pts | MSE 0.37 (normalised) | not comparable; ours is coarse |
| Search / simulation | offline info-set MCTS (labels only); none at runtime | Bakuuchi: runtime MC; Suphx: pMCPA | runtime EV missing; push/fold is a transcribed table |
| Placement value | all-last factors in the budget | Suphx global reward GRU | missing |
| RL | none | Suphx only | out of reach on this hardware |
| Best runtime config | `learned-guarded`: 61.6 % agreement, deal-in vs riichi **1.53 %** (humans 2.18 %) | — | defense already beyond Phoenix humans on the counterfactual metric |

The honest summary: our opponent models are in the same class as Bakuuchi's (the danger
tables are literally measured from Houou, and the learned tenpai head is well calibrated),
our imitation policy is a small-data, shallow version of NAGA/Suphx's supervised stage, and we
have nothing corresponding to Suphx's RL or Bakuuchi's runtime simulation. Nothing here is a
dan estimate; only matched-seed simulator A/B or live play can produce one.

## 3. What is affordable here, ranked by expected gain per cost

| # | Path | Cost | Why it should pay |
|---|---|---|---|
| **A** | **Look-ahead feature planes** for the network: for every discard, shanten after, ukeire, tenpai/wait count, dora kept, best-wait yaku han — all already computed by `HandAnalyzer`. Bump `LearningFeatures.Version`, re-export, retrain. | 1–2 days; export ×3 slower (~1 ms/row extra) | Suphx's stated reason for 838 input planes; it gives the CNN exactly the hand-structure information a 2-layer net cannot derive from tile counts. Largest expected imitation gain for the least code. |
| **B** | **All Phoenix archives** (n1–n23 are on the ranking page too; ~50 k games estimated → ~20 M decisions) with sampled export. | download + import ≈ 2 h unattended; export sample of 8–10 M rows | Suphx's discard model used 15 M pairs; we are at 3 M. Data is the cheapest lever we have. Training stays CPU-feasible with the streamed loader (~20 min per 3 M rows per epoch at c96). |
| **C** | **Residual blocks** in the artifact (N × [conv-conv-add]) with C# inference + parity. 4–6 blocks × 128 channels is ~10× the current compute. | 2 days code; CPU training ~1 epoch/2–3 h at 3 M rows — realistic only with fewer rows or a GPU | Depth matters (76.7 % vs 68.8 % in the literature is partly this), but it is the one item that really wants a GPU. Do A and B first; they change what C needs. |
| **D** | **Learned call heads** (chi/pon/kan yes-no with the offered meld encoded), importer emitting call decisions, `CallPolicy` consulting them. | 3–4 days | Calls are a whole decision class the network never sees; Suphx reaches 92–95 %, so they are learnable from the same data. |
| **E** | **Bakuuchi-style runtime EV** for push/fold: one-player rollouts (our draws only; opponents abstracted by tenpai × danger × value per turn) to compute EV(push tile) vs EV(fold) instead of the transcribed Fukuchi table. The simulator and all three opponent models already exist. | 3–5 days; ~50–100 ms per decision | This is exactly how the 9-dan Bakuuchi decided; it replaces a static table with a computed, opponent-aware number and can be validated with the harness deal-in metric and simulator A/B. |
| **F** | **Placement head** (final-placement distribution from public state + scores; targets already exported) feeding the all-last budget and, later, the simulator utility. | 1–2 days | Suphx's global reward predictor, in its cheapest form; the Riichi City regulars' "the bot doesn't know all-last" complaint is this. |
| — | RL self-play, oracle guiding, pMCPA | 44 GPUs × 2 days per agent in the paper | Not affordable here; also unnecessary until A–F are exhausted. |

Recommended order: **B → A → F → E → D → C**. B and A are cheap and compound (more data,
better features); F is small and fixes a known behavioural gap; E is the one that changes
*strength* rather than imitation; D and C are larger investments whose payoff is best judged
after the first four are measured with `learn-eval` and a matched-seed simulator A/B.

## 4. Sanity anchors for the A/B stage

When the simulator A/B runs, compare against the published per-hand rates in the Tenhou
expert room: win 22.7–23.1 %, deal-in 10.1–12.2 %, first-place 25.6–29.3 %, fourth-place
18.7–22.4 %. Our own environment differs (Doman rules, our opponents are our own policies), so
these are orientation, not targets; the target is beating our previous build on matched seeds.

## 5. Status after the training-package work (2026-09-21, evening)

The project owner's order: the weakest point first (D), then affordability. Implemented on
`experimentalv2`, all measured with the same harness:

| path | state | evidence |
|---|---|---|
| **D** learned calls | importer emits every claim-window reaction (pass / chi / pon / open kan; 26 % of decisions); 82-action space (`public-tiles-v2`); `LearnedCallPolicy` gates the heuristic call with the network's pass probability | pilot (34 games): heuristic calls on **24.2 %** of windows, humans **15.8 %**, agreement 80.4 % (pass 83.7 %, call 63.0 %) — this is the gap a trained call head has to close |
| **A** look-ahead planes | eight planes: tenpai / shanten / ukeire / wait count after each discard, current shanten + ukeire, best shanten and tenpai after the offered claim | encoder + tests; effect measurable only after a real training run |
| **C** residual network | schema-2 artifact (stem + N residual blocks), C# inference vectorized (3 M-parameter net: 4.6 ms per position, was ~45 ms), per-thread memo so a decision runs the network once; parity verified 1e-6 | `train.py --blocks --channels --device cuda` with mixed precision |
| **B** all archives | `Run-Training.ps1` fetches n1–n30 (1.06 GB, ~100 k games) with SHA-256 provenance; parallel importer (9 min for 4 archives on 10 workers); float16 export at 8 600 rows/s | `docs/TRAINING_PACKAGE.md`; `artifacts/training-win-x64.zip` (31 MB) |
| F placement head, E runtime EV | not started | — |

Run 7 (`EVALUATION_RUNS.md`) puts the v1 c96/h256 net on the v2 corpus test split, 1 500
games: learned 66.3 % agreement, learned-guarded 64.1 % with chosen-tile deal-in vs riichi
1.52 % (humans 2.03 %); the learned tenpai head now beats the refit logistic (AUC 0.852 /
0.782 closed / open, ECE 0.004 / 0.038). These are the numbers the GPU model is compared to.

What the GPU box should produce first: `Run-Training.ps1` with the defaults (24 000 games ≈
14 M rows, blocks 6 × 128, 20 epochs). Judge it on `test.policy_top1` (was 69.1 % for the
v1 c96/h256 net on 3 M rows), `test.reaction_top1` (heuristic 80 %), the learned-guarded
call rate against the human 16 %, and the chosen-tile deal-in block. If reaction accuracy
clears ~88 % the call gate threshold (`LearnedCallPassThreshold`, default 0.5) can be
tuned on the validation split rather than guessed.
