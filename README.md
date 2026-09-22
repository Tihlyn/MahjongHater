# MahjongHater

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for FFXIV **Doman Mahjong**. It reads the
table straight from the game's `Emj` addon, runs a decision policy over the position, and shows the
recommended action — discard, call, riichi, tsumo/ron or pass — with ranked alternatives, deal-in
risk and the reasoning behind it. The best discard is also outlined on the game's own tile.

Current version: **1.2.2** (see [CHANGELOG.md](CHANGELOG.md)).

The `experimental` branch also includes a standalone four-player simulator and
resumable MCTS database generator. See the [Windows compute-box guide](docs/SIMULATOR.md)
for packaging, generation, retrieval and current limitations.

## What it does

- **Reads the game, not the screen.** The local hand, per-seat discard/meld/riichi counters, melds
  and scores come from the `AddonEmj` struct (`docs/EMJ_STRUCT.md`); discard tiles, chi
  compositions, call prompts and round boundaries come from the addon's `AtkValues` refresh events;
  winds come from text nodes. Nothing depends on hovering tiles or decoding textures.
- **Exact hand analysis.** Per-suit shanten with a naive-oracle differential test, ukeire and
  two-step ukeire, yaku detection (Doman rules: kuitan configurable, atozuke on, no kuikae).
- **A policy, not just a tile.** `DecisionPolicy` orders every turn as tsumo/ron → call prompt →
  push/fold → discard → riichi, each step leaving a one-line reason. An opponent model estimates
  tenpai probability (logistic, calibrated from logged hand ends) and per-tile danger
  (genbutsu, suji, kabe, honors seen).
- **Overlay.** Session score / W-L, table state, the recommended action with tile chips, the top
  discard candidates (shanten, ukeire, risk bar, waits), the reasoning steps, and a glow on the
  recommended tile in the game UI. Analysis runs off-thread with a watchdog; the overlay never
  blocks the render loop.
- **Auto play + requeue (dev tooling).** An "AUTO PLAY" card in the main window can execute the
  recommendations against the table, click through recaps, and requeue the next Duty Finder match.
  It exists to run unattended matches and dump any stall it hits; see below.

## Install

In Dalamud Settings → **Experimental**, add
`https://raw.githubusercontent.com/Tihlyn/MahjongHater/main/repo.json` to the custom plugin
repositories, save, then install **MahjongHater** from the plugin installer.

### Learned policy

Both trained models ship inside the plugin (`resources/models/learned_policy-8.json.gz` for
Full Match, `-4` for Quick Match), so there is nothing to download or unzip. Turn on
**Learned policy** in `/mhater config` and reload the plugin; the one matching the configured
match length is used. The overlay's **Diagnostics** tab states whether a model is loaded and
which file it came from, and the Dalamud log confirms it with
`Learned policy loaded from ... as learned-guarded`.

To use your own model instead, put `learned_policy-8.json` (or `-4`, `.json.gz` also works)
in `%AppData%\XIVLauncher\pluginConfigs\MahjongHater\`: the config folder is searched
first. The same archives are attached to each
[release](https://github.com/Tihlyn/MahjongHater/releases) with a `metrics.json` summary.

What the models do and do not do is in `docs/research/CANDIDATE_ASSESSMENT.md`; the numbers
are in `docs/research/EVALUATION_RUNS.md`.

## Use

The main window shows your score, Doman Mahjong **rank** and **rating**. The game only
exposes rank and rating in the Gold Saucer Info window, so open **Gold Saucer → Doman
Mahjong** once and the plugin remembers what it read.


- `/mhater` toggles the overlay; `/mhater config` opens the settings. The overlay also opens from
  the plugin installer's main/config buttons.
- Sit down at any Doman Mahjong table (NPC or Duty Finder). The overlay shows "Ready when you are"
  until the `Emj` addon is open, then follows every draw, prompt and recap on its own.
- Header pill **Enabled/Disabled** pauses the whole framework tick (reader, analysis, auto play
  and requeue).

### Settings

| Setting | Effect |
|---|---|
| Enable plugin / Show overlay | Framework tick on/off; overlay window visibility (persisted). |
| Kuitan (open tanyao) | Fed into the yaku detector's ruleset. Turn off for the kuitan-disabled room. |
| Game length, Double-wind pair fu, Dora display | Stored and shown, **not yet consumed** by the analysis (open item). |

### Auto play and requeue

The **AUTO PLAY** card (main window, under the session metrics):

- **Play the recommended actions** — executes each fresh decision once (discard by slot, list rows
  by label, chi-shape buttons) after a random 2–4 second pause with an on-screen countdown.
  It retries once the state has not moved for 6 s, advances recaps every
  4 s and clicks *End match* / a confirmation at the end. Decisions on a call window that only the
  panel texts reported (no game event) are not answered.
- **Requeue when a match ends** — registers for the chosen solo duty as soon as the table closes
  and the Duty Finder queue is idle, then clicks *Commence* on the pop. Duty pills: Novice/Advanced
  × Quick (East only) / Full (East + South); Advanced needs 1st dan.
- **Stall watch** — if the snapshot has not changed for 15 s while the game waits on us (45 s
  otherwise), a dump goes to `pluginConfigs/MahjongHater/autoplay_stalls.log` (snapshot, decision
  and reasoning, prompt rows, per-slot clickability, the auto-play journal, the tracker's recent
  events), the card shows a red badge, and a recovery ladder (Pass → discard the draw → recap
  buttons) tries to get the match moving. Everything it does is also in the Dalamud log under
  `[AutoPlay]` / `[Queue]`.

Both toggles persist across reloads. This is a development aid and plays *ranked* matches on your
account when requeue is on — leave both off unless that is what you want.

## How it works

```
Emj addon ──struct read (EmjStructReader + resources/layouts/emj.json)──┐
          ──PostRefresh AtkValues events (EventTracker)────────────────┼─► SnapshotBuilder ─► StateSnapshot
          ──text nodes: winds, result banners (EmjStateReader)─────────┘        (immutable, Sequence-stamped)
                                                                                     │
                                       AnalysisService (off-thread, debounced, watchdog) ─► DecisionPolicy
                                                                                     │
                     MainWindow (overlay, tile glow) ◄── AnalysisPublication (ActionChoice + fingerprint)
                     AutoPlayer / EmjActuator ◄──────────┘         MatchQueuer (Duty Finder)
```

- `Core/State/` — `EmjLayout` (offsets/state codes/node paths from JSON, embedded fallback, ±8
  icon-base self-heal), `EmjStructReader` → `StructFrame` → `DecodedStruct`, `EventTracker`
  (discards, call windows, melds, round boundaries, winds, doras, session W/L), `SnapshotBuilder`.
- `Core/` — `Shanten`/`ShantenSuit`, `HandAnalyzer` (ukeire, ukeire2, value, waits),
  `YakuDetector`, `HandTracking` (hand-size and call-legality rules), `AnalysisService`.
  `ScoringEngine`/`FuCalculator` implement han/fu/points but the live advice currently values hands
  by yaku han + dora only.
- `Core/Policy/` — `OpponentModel` + `TenpaiEstimator`, `HeuristicDiscardPolicy`, `PushFoldPolicy`,
  `CallPolicy`, `RiichiPolicy`, `DecisionPolicy`, `PolicyWeights` (all tunables),
  `TenpaiCalibration` (ground-truth CSV). Pure C#, no Dalamud types.
- `Core/Operate/` — `EmjOperator` (AtkEvent firing), `EmjActuator` (decision → clicks),
  `AutoPlayer`, `MatchQueuer`.
- `Windows/` — `MainWindow`, `ConfigWindow`, `Theme`/`Widgets` (glass UI), `TileArt` (game tile
  faces via the texture provider).

Details: [docs/EMJ_ADDON_REFERENCE.md](docs/EMJ_ADDON_REFERENCE.md) (addon nodes, events, call
window lifecycle, operating the addon), [docs/EMJ_STRUCT.md](docs/EMJ_STRUCT.md) (struct offsets and
evidence), [docs/POLICY_NOTES.md](docs/POLICY_NOTES.md) (policy formulas and limits),
[docs/tile_table.md](docs/tile_table.md) (tile ids), [docs/REWORK_PLAN.md](docs/REWORK_PLAN.md)
(the 2026-09 rework and its status).

## Development

Experimental offline policy work: [precomputed belief-state engine](docs/PRECOMPUTED_POLICY.md)
documents the standalone trainer, snapshot capture, opt-in lookup and current simulator limits.

- Requirements: .NET 10 SDK, a Dalamud dev install (`%APPDATA%\XIVLauncher\addon\Hooks\dev`, or
  set `DALAMUD_HOME`). `dotnet build MahjongHater.csproj -c Release` writes
  `bin/Release/net10.0-windows/MahjongHater.dll`; load it as a dev plugin and Dalamud hot-reloads it
  on every rebuild (the tracker cold-starts from the struct mid-hand).
- Tests: `dotnet test MahjongHater.Tests -c Release` — 266 tests: shanten reference/differential,
  analyzer golden hands, analysis service, policy (calls, discards, push/fold, riichi, opponent
  model, tenpai estimate/calibration), state (layout, struct frames and live hex fixtures under
  `resources/fixtures/`, event tracker replays, snapshot builder, meld inference).
- Layout: every game number lives in `resources/layouts/emj.json` (ships next to the DLL, embedded
  copy as fallback). A client patch that shifts icon ids self-heals within ±8; anything else is a
  layout edit, not a code change.
- Reverse engineering: Cartographer (`dldm_reverse`, `/carto debug`, port 9790) gives node trees,
  live `AtkValues`, memory watches and operator clicks on any addon; the reference doc says which
  endpoints answer which question.
- Tenpai calibration: every hand end appends one row per opponent to
  `pluginConfigs/MahjongHater/tenpai_calibration.csv`; `python tools/fit_tenpai.py` re-fits the
  logistic weights and prints the `PolicyWeights` initializers.
- CI (`.github/workflows/ci.yml`) builds and tests on Windows/.NET 10 against the Dalamud
  distribution; a `v*` tag runs the release workflow.

## Releasing

From a clean, up-to-date `main`: `./tools/release.ps1 -Bump patch`. The script tests, bumps
`Directory.Build.props` and the changelog, commits, tags and pushes; CI publishes the release and
updates `repo.json`. See [docs/RELEASING.md](docs/RELEASING.md).

## Repository map

```
Plugin.cs, Configuration.cs      entry point, persisted settings
Core/, Core/State, Core/Policy, Core/Operate   engine, reader, policy, actuator
Windows/                          overlay + settings UI
resources/layouts/emj.json        struct/AtkValues/node layout (also embedded)
resources/fixtures/*.hex|.json    live struct captures used by tests
resources/tile_face_map.json, resources/emj_analysis_*.txt   historical captures from the
                                  hover/node-face era (no longer read by code)
docs/                             reference docs (see above)
tools/                            release.ps1, make_repo_json.py, fit_tenpai.py
MahjongHater.Tests/               xunit suite
```
