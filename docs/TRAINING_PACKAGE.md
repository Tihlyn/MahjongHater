# MahjongHater training package (Windows, CUDA)

A self-contained pipeline that turns Tenhou Phoenix archives into a trained
`learned_policy.json` for the plugin: **fetch → import → export → train → parity check →
evaluate**. Built by `tools/publish_training.ps1`; nothing on the target box needs the
repository, the .NET SDK or the game client.

What the model learns (feature version `public-tiles-v2`, artifact schema 2):

- **Discard / riichi imitation** of 7-dan+ Phoenix players (74 actions), with eight
  look-ahead planes (shanten, ukeire, tenpai and wait count after every discard, best
  outcome after a claim) — path A of `research/STRENGTH_COMPARISON.md`.
- **Call decisions** — pass / pon / open kan / chi shape on every claim window a Phoenix
  player faced (path D, the weakest component of the current plugin: the heuristic calls on
  24 % of windows, humans on 16 %).
- **Opponent heads**: tenpai per seat, conditional winning tiles, hand value.
- **Final placement** (path F): P(1st..4th) for the acting seat from the public state and
  scores; the plugin's push/fold budget uses it as placement stakes (what a win or a deal-in
  does to our placement) instead of the fixed all-last factors.
- Network: 3×1 conv stem → N residual blocks → dense → heads (path C); float16 dataset so the
  full archive set fits on disk (path B).

Two match lengths: the archives hold hanchan (Full Match) and tonpuusen (Quick Match) games
and the plugin only loads a model trained on the configured length, so run the pipeline
once per length (`-HandsInMatch 8`, the default, and `-HandsInMatch 4 -RunName quick`).
Quick Match games are ~10 % of the archives (n28: 690 of 3 181).

## Requirements on the compute box

- Windows 10/11 x64, PowerShell 5.1 or 7.
- Python 3.10–3.13 (64-bit) on PATH (`python --version`). 3.14 works only if a CUDA torch
  wheel exists for it; when in doubt install 3.12 from python.org.
- NVIDIA driver recent enough for CUDA 12.6 wheels (any driver from 2024 on); 8 GB VRAM is
  plenty for the default network.
- Disk: archives 1.1 GB, corpus ≈ 25 GB (all 30 archives), dataset **≈ 80 GB at the
  default 24 000 games** (float16, ≈ 600 rows per game, 5.6 KB per row). The export stage
  refuses to start without the space; lower `-MaxGames` or point `-WorkDirectory` at a
  bigger drive.
- Memory: import and export stay under ~3 GB; training under ~4 GB RAM (the loader streams
  a bounded shuffle buffer) plus the GPU.

## Steps

```powershell
Expand-Archive training-win-x64.zip -DestinationPath C:\mh-training
cd C:\mh-training
.\Setup-Training.ps1          # venv + torch (CUDA 12.6 wheel) + numpy; prints the GPU it found
.\Run-Training.ps1            # all stages with the defaults below
```

`Run-Training.ps1` is resumable: every stage skips outputs that exist and training resumes
from `checkpoint.pt`. Stages can be run one at a time with `-Stage fetch,import` etc. Logs go
to `work\logs\<stage>-<timestamp>.log`.

| stage | what | output | expected time (16 cores / 8 GB GPU) |
|---|---|---|---|
| fetch | downloads `mjlog_pf4-20_n1..n30.zip` from tenhou.net with SHA-256 provenance | `work\raw\` + `archives.json` | minutes (1.1 GB) |
| import | parses every game; keeps East-South games with a 7-dan+ acting player; emits turn and claim-window decisions | `work\corpus\` | ~1 h for all archives (16 479 games / 4 archives took 9 min on 10 workers) |
| export | dense float16 rows for a uniform sample of `-MaxGames` games | `work\dataset-<games>-float16\` | ~30–45 min for 24 000 games (8 600 rows/s on 10 workers) |
| train | residual network on CUDA with mixed precision, early-stopped on validation loss, calibration fitted on validation | `work\model-<run>\learned_policy.json`, `metrics.json`, `parity.json` | GPU-bound: ~5–8 min per epoch at 14 M rows for the default net on an RTX 2070-class card (the first, loader-bound version of the script took 15 min for a net a third the size) |
| check | C# inference reproduces the PyTorch outputs on the parity vectors | log line "Verified 8 PyTorch/C# inference vectors" | seconds |
| eval | `learn-eval` on 1 500 held-out test games: agreement per category, reaction agreement + call rate, counterfactual deal-in of the chosen tile, tenpai/danger calibration, for the heuristics and the learned policies side by side | `work\eval\<run>-test.json` + log | ~30 min |

Defaults: `-MaxGames 24000 -Blocks 8 -Channels 160 -Hidden 512 -Epochs 20 -Batch 4096
-WindowRows 0`. The network is ≈ 5 M parameters (~100 MB as JSON; the plugin's load limit
is 256 MB) and costs ≈ 10 ms per position in the plugin's C# inference (a decision runs it
once, plus three score counterfactuals for the placement stakes). `-Blocks 10 -Channels 192`
(≈ 8 M parameters, ≈ 16 ms) is the next step up if the run looks capacity-limited (train
and validation loss still falling together at the end); `-LearningRate 0.002` is reasonable
at batch 4096.

How the data reaches the GPU: `train.py` streams the training split through device memory.
A reader thread fills a pinned window (`-WindowRows`, default ≈ a third of free GPU memory,
≈ 450 k rows / 2.5 GB on an 8 GB card) with randomly ordered contiguous chunks of the file
while the GPU trains on the previous window; the window is copied over in float16 and
shuffled, sliced and unpacked on the device, and the objective has no host/device
synchronisation points. Host work per epoch is one read of the file (80 GB: 40 s on NVMe,
~3 min on a SATA SSD, either well under the compute time) and ~3 GB of RAM. `train_seconds`
and `gpu_peak_mb` in the log say whether the card is busy: expect several GB of peak GPU
memory and `nvidia-smi` near 100 %. The first version of the script kept the loader on the
training thread with a sync per loss term, which starved the GPU (200 MB peak, 15 min per
epoch); that is what the log line `window_rows` distinguishes.

Useful variations:

```powershell
.\Run-Training.ps1 -Stage fetch,import,export                       # data only, once
.\Run-Training.ps1 -Stage train,check,eval -RunName res6            # default net
.\Run-Training.ps1 -Stage train,check,eval -RunName res10 -Blocks 10 -Channels 192 -Epochs 30
.\Run-Training.ps1 -Stage train,check,eval -RunName res6 -Resume    # continue an interrupted run
.\Run-Training.ps1 -MaxGames 12000                                  # half the disk (~40 GB)
.\Run-Training.ps1 -Device cpu -Batch 256                           # no usable GPU (10× slower)
.\Run-Training.ps1 -HandsInMatch 4 -RunName quick -MaxGames 100000  # Quick Match model (all tonpuusen games)
```

## What to send back

- `work\model-<run>\learned_policy.json` — the model. It loads in the plugin from the
  plugin config directory as `learned_policy.json` (Config → "learned policy"); the plugin
  checks the match length, so keep the hanchan and Quick Match models apart.
- `work\model-<run>\metrics.json` — training curves and test metrics (policy top-1,
  reaction top-1, tenpai/ron calibration, value MAE).
- `work\eval\<run>-test.json` and `work\logs\eval-*.log` — the harness comparison.
- `work\raw\archives.json` — which archives, sizes and hashes.

The corpus and dataset stay on the box; they are reproducible from the archives.

## Reading the results

`metrics.json → test.policy_top1` is discard/riichi imitation accuracy on held-out games
(references: 65–69 % for the earlier two-layer net on 0.5–3 M rows, 68.8 % for the CNN
Suphx cites, 76.7 % Suphx supervised). `test.reaction_top1` is the claim-window accuracy
(heuristic: 80 %; Suphx chow/pong 92–95 %). `test.placement_top1` should beat
`test.placement_top1_by_current_rank` (the "you finish where you are now" baseline). The eval
log's `reaction` block shows the learned-guarded call rate against the human rate; the
`deal-in rate of the chosen tile` block is the defense metric (lower than human is good, as
long as agreement holds up); the `final placement` block repeats the placement comparison
on held-out games, overall, in South 3+ and in the last hand.

Nothing here is a dan estimate. Promotion to the default policy needs matched-seed simulator
A/B (`docs/SIMULATOR.md`) or live matches.

## Files in the package

| file | role |
|---|---|
| `Precompute.exe` | replay import, dataset export, parity check, evaluation harness, simulator (self-contained .NET) |
| `train.py` | PyTorch trainer (CPU or CUDA), exports the schema-2 artifact |
| `Setup-Training.ps1`, `Run-Training.ps1`, `requirements.txt` | environment and pipeline |
| `generation.json`, `generation-4.json` | rule profiles (Doman: kuitan on; 8 or 4 hands) used by the importer |
| `REPLAY_IMPORT.md`, `EVALUATION.md` | corpus format and harness documentation |
| `PACKAGE.json` | repository commit the package was built from |
