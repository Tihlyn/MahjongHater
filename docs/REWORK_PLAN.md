# Rework plan — struct-backed state + policy layer (status)

Written 2026-09-18 after comparing against XeldarAlz/FFXIV-AutoMahjongSolver (AGPL — findings
and structure were borrowed, never code) and confirming with Cartographer that the full 14-tile
local hand is a plain `int32[14]` inside the `AddonEmj` struct. The rework replaced the 2.9k-line
hover / node-face / AtkValues-slice reader with a struct reader plus a small event tracker, and
added the decision layer the plugin never had. **Status as of 2026-09-19: phases 0–3 shipped in
v1.2.x; phase 4 is partly done.** This file records what was planned, what shipped and where it
deviated; the living technical references are [`EMJ_STRUCT.md`](EMJ_STRUCT.md),
[`EMJ_ADDON_REFERENCE.md`](EMJ_ADDON_REFERENCE.md) and [`POLICY_NOTES.md`](POLICY_NOTES.md).

## Goals

1. **One reader.** `GameStateReader` → `Core/State/` (`EmjLayout`, `EmjStructReader`,
   `EventTracker`, `SnapshotBuilder`) producing an immutable `StateSnapshot`. ✔
2. **One decision layer.** `HandAnalyzer` stays the engine (exact shanten, ukeire/ukeire2,
   yaku); `Core/Policy/` composes it into an `ActionChoice` with reasons. ✔
3. **Keep what was better here.** Exact per-suit shanten, 2-step ukeire, the off-thread
   `AnalysisService`. ✔ The two-way debug API and the original `EmjOperator` were **removed** for
   the first release (`3847a50`); the event-firing part came back on 2026-09-19 as
   `Core/Operate/` for auto play.

## Phases

### Phase 0 — Struct mapping ✔ (2026-09-18)

Two matches watched with `/mem/watch` → [`EMJ_STRUCT.md`](EMJ_STRUCT.md): hand array, dora
indicator + count, and per seat (0x2E0 stride) closed count, meld count, discard count, riichi
discard index, score, point difference, meld tile indices and from-directions. Not in the struct:
discard tiles, chi tiles, winds, dealer, wall, honba, riichi sticks, ura dora. Layout JSON at
`resources/layouts/emj.json`; eight hex fixtures under `resources/fixtures/`.

Deviation: the per-seat discard *arrays* were never found (`discardArrays` are `null`); type-8
events remain the discard-tile source, reconciled against the struct's counts.

### Phase 1 — Reader consolidation ✔ (`8f4ddac` … `f2a02d0`)

`Core/State/` as planned. `EventTracker` keeps: per-seat discards (type-8), call windows
(type-19/23/25 + label fallback + legality gate), melds (type-13 / hand delta, never atkType-74
payloads), round boundaries (type-21 Layout 1, struct counts, deal shape), winds, doras, W/L.
`GameStateReader`, `TileFaceMap`, hover tracking and pile scanning were deleted, not flagged.
`EmjScanner` keeps only slot/list/text lookups for acting and wind/banner reading.

### Phase 2 — Policy layer ✔ (codex, worktree `rework-policy`)

`OpponentModel`, `HeuristicDiscardPolicy`, `PushFoldPolicy`, `CallPolicy`, `RiichiPolicy`,
`DecisionPolicy`, `PolicyWeights`. Post-merge changes from live play: the additive tenpai estimate
became the logistic `TenpaiEstimator` with a ground-truth CSV and `tools/fit_tenpai.py`; push/fold
treats the estimate as soft (only a declared riichi folds a near-tenpai hand); `RiichiMinUkeire`
2. See [`POLICY_NOTES.md`](POLICY_NOTES.md).

### Phase 3 — Wiring + UI ✔ (`5fb413a`, glass UI `7b1cdcf`)

`AnalysisService` runs `IPolicy.Choose` off-thread over snapshots (debounced, watchdog,
latest-wins); `MainWindow` renders the `ActionChoice` (headline, tiles, candidates with
shanten/ukeire/risk/waits, reasoning steps) and glows the recommended tile on the game UI.
`EmjOperator.Execute` existed behind `/act` on the debug API, was removed with it, and returned as
`EmjActuator` + `AutoPlayer` (with requeue) on 2026-09-19 — still opt-in, from the main window.

### Phase 4 — Hardening (partial)

Done: analyzer golden hands, differential shanten, struct-fixture replay tests, tracker event
replays, GitHub Actions CI + release workflow, layout self-check surfaced in the overlay
("Layout check failed"). Open: policy golden positions from real hands, a replay harness for
whole recorded matches, and honba / riichi-stick / ura-dora sources.

## Branching (historical)

`rework` was the integration branch (July checkpoint + phases 0–3), fast-forwarded into `main`
on 2026-09-19 at `f2a02d0`; sub-work happened in worktrees `rework-reader` and `rework-policy`.
Releases are tagged from `main` (`v1.2.0`, `v1.2.2`).

## Layout JSON schema

`resources/layouts/emj.json` is the schema by example — every offset is a hex string or `null`,
unknown keys are ignored, missing ones stay `null`, and the reader tolerates any `null`. Reader
code must never hard-code a number that belongs there. Sections: `offsets` (hand, scores,
discard counts/arrays, point differences, `melds`, `riichiFlags`, dora, winds, honba, sticks,
wall), `atkValues` (state-code and wall-count indices), `stateCodes`, `nodes` (slot ids, list,
recap, seat-wind / score / honba / round-wind / result-banner text paths, chi-chooser buttons,
wall-counter digits).
