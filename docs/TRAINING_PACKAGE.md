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
- Network: 3×1 conv stem → N residual blocks → dense → heads (path C); float16 dataset so the
  full archive set fits on disk (path B).

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
| train | residual network on CUDA with mixed precision, early-stopped on validation loss, calibration fitted on validation | `work\model-<run>\learned_policy.json`, `metrics.json`, `parity.json` | ~15–30 min per epoch at 14 M rows (disk-bound), 20 epochs default |
| check | C# inference reproduces the PyTorch outputs on the parity vectors | log line "Verified 8 PyTorch/C# inference vectors" | seconds |
| eval | `learn-eval` on 1 500 held-out test games: agreement per category, reaction agreement + call rate, counterfactual deal-in of the chosen tile, tenpai/danger calibration, for the heuristics and the learned policies side by side | `work\eval\<run>-test.json` + log | ~30 min |

Defaults: `-MaxGames 24000 -Blocks 6 -Channels 128 -Hidden 512 -Epochs 20 -Batch 1024
-BufferRows 262144`. The network is ≈ 3 M parameters (62 MB as JSON; the plugin's load
limit is 256 MB), needs < 2 GB of VRAM at batch 1024 and costs ≈ 5 ms per position in the
plugin's C# inference. The box can take `-Blocks 10 -Channels 192` if the first run looks
data-limited rather than capacity-limited (train loss ≫ validation loss says the opposite).

Useful variations:

```powershell
.\Run-Training.ps1 -Stage fetch,import,export                       # data only, once
.\Run-Training.ps1 -Stage train,check,eval -RunName res6            # default net
.\Run-Training.ps1 -Stage train,check,eval -RunName res10 -Blocks 10 -Channels 192 -Epochs 30
.\Run-Training.ps1 -Stage train,check,eval -RunName res6 -Resume    # continue an interrupted run
.\Run-Training.ps1 -MaxGames 12000                                  # half the disk (~40 GB)
.\Run-Training.ps1 -Device cpu -Batch 256                           # no usable GPU (10× slower)
```

## What to send back

- `work\model-<run>\learned_policy.json` — the model. It loads in the plugin from the
  plugin config directory as `learned_policy.json` (Config → "learned policy").
- `work\model-<run>\metrics.json` — training curves and test metrics (policy top-1,
  reaction top-1, tenpai/ron calibration, value MAE).
- `work\eval\<run>-test.json` and `work\logs\eval-*.log` — the harness comparison.
- `work\raw\archives.json` — which archives, sizes and hashes.

The corpus and dataset stay on the box; they are reproducible from the archives.

## Reading the results

`metrics.json → test.policy_top1` is discard/riichi imitation accuracy on held-out games
(references: 65–69 % for the earlier two-layer net on 0.5–3 M rows, 68.8 % for the CNN
Suphx cites, 76.7 % Suphx supervised). `test.reaction_top1` is the claim-window accuracy
(heuristic: 80 %; Suphx chow/pong 92–95 %). The eval log's `reaction` block shows the
learned-guarded call rate against the human rate; the `deal-in rate of the chosen tile`
block is the defense metric (lower than human is good, as long as agreement holds up).

Nothing here is a dan estimate. Promotion to the default policy needs matched-seed simulator
A/B (`docs/SIMULATOR.md`) or live matches.

## Files in the package

| file | role |
|---|---|
| `Precompute.exe` | replay import, dataset export, parity check, evaluation harness, simulator (self-contained .NET) |
| `train.py` | PyTorch trainer (CPU or CUDA), exports the schema-2 artifact |
| `Setup-Training.ps1`, `Run-Training.ps1`, `requirements.txt` | environment and pipeline |
| `generation.json` | rule profile (Doman: kuitan on, 8 hands) used by the importer |
| `REPLAY_IMPORT.md`, `EVALUATION.md` | corpus format and harness documentation |
| `PACKAGE.json` | repository commit the package was built from |
