# Changelog

Notable changes are recorded here using the Keep a Changelog format and semantic versioning.

## Unreleased

## [2.0.2.1] - 2026-09-22

### Fixed

- Anti-idle: the synthetic keystroke did not stop duty ejection (the client does not count
  synthetic input for its timers). The guard now holds `UIModule.InputTimerModule`'s AFK,
  content and input timers at zero while auto play runs a match, the method NightmareXIV's
  AntiAfkKick uses; outside an unattended match the normal AFK behaviour is untouched.
- Call windows are read from the option codes in the AtkValues integer lane (1=Tsumo, 2=Ron,
  3=Riichi, 4=Kan, 5=Pon, 6=Chi) instead of the button text, cross-checked against the row
  labels and the row count. This is locale-independent, keeps the "Discard" banner out of the
  option list, and rejects the unrelated integer payloads a type-19 also carries.
- A call window opened only from the prompt panel's text now expires after 400 ms unless an
  event confirms it. The panel keeps its labels after a prompt closes, which invented windows
  offering a finished hand's Ron or a riichi on a 1-shanten hand.
- Clicks never leave the addon hovering a tile: the operator fires the activation chain alone,
  and the hover fallback (MouseOver + MouseOut before the click) is only used if a click does
  not register. An unpaired MouseOver left the Emj agent refreshing a discarded tile's tooltip
  and crashed the game three times in one session (docs/research/LIVE_ISSUES_2026_09_22.md).
- The optional precomputed/simulation tables no longer log a warning when they were never
  generated; their state shows in the Diagnostics tab instead.

### Changed

- The main window shows the Doman Mahjong rank and rating in place of the session W/L and win
  rate. The game exposes them only in the Gold Saucer Info window, so the plugin reads them
  whenever that window is open and remembers the last value.
- When the game offers riichi and the policy answers with a discard, the closed hand, drawn
  tile and meld count are logged — the one decision-quality defect the 2026-09-22 session
  showed (2 of 9 genuine offers) needs that to be diagnosed.

## [2.0.1] - 2026-09-22

### Added

- Both trained models ship inside the plugin (gzipped, 37 MB each); the learned policy works
  after ticking one setting, with no download. A model in the plugin config folder still
  overrides the shipped one, and `.json.gz` is read directly.
- Diagnostics tab shows whether a learned model is loaded, which file it came from, and why
  it is not in use when it is not.

## [2.0.0] - 2026-09-22

### Added

- Learned policy (opt-in, `learned_policy.json` in the plugin config folder): a residual
  network trained on Tenhou Phoenix logs supplies the discard ordering, the opponent tenpai
  estimate, the call decisions and a final-placement head, all inside the measured danger
  budget ("learned-guarded"). Held-out imitation: 75.0 % of discard/riichi decisions and
  92.7 % of claim-window reactions; chosen-tile deal-in against a riichi 1.70 % where the
  Phoenix players themselves were at 2.05 % (`docs/research/EVALUATION_RUNS.md`).
- Defense v2 throughout: danger tables measured from Houou deal-in rates, push/fold as a
  danger budget with hand and threat values in points, betaori ordering, and placement
  stakes that scale the budget by what a win or a deal-in does to our final placement.
- Tenhou replay pipeline and evaluation harness (`tools/Precompute`): corpus import with
  claim-window reactions, dense dataset export, `learn-eval` scoring every policy against
  human decisions with calibration and counterfactual deal-in, and `learn-fit-tenpai`.
- Information-set MCTS simulator, precomputed policy tables and a self-contained
  Windows/CUDA training package (`docs/TRAINING_PACKAGE.md`) that fetches the archives,
  imports, exports, trains, verifies C#/PyTorch inference parity and evaluates.
- Anti-idle safeguard for unattended runs: while auto play is in a match and the machine
  has been idle for the interval, one F19 keystroke every 150 s keeps the duty from
  ejecting the player.

### Changed

- Overlay split into Play and Diagnostics tabs; table tracking, tracker notes and the
  auto-play counters moved to Diagnostics.
- `hand_results.csv` records the policy that was deciding, so live A/B arms can be compared
  (`tools/ab_summary.py`, `tools/candidate_bars.py`).

### Fixed

- Round-end hand results were built from a snapshot that had already been cleared, which
  threw on every recap screen and left `hand_results.csv` empty.

## [1.3.0] - 2026-09-19

### Added

- Auto play and requeue in the main window (dev tooling): the overlay's decisions are
  executed against the table, round recaps are advanced, and matches are requeued through
  the Duty Finder (Novice/Advanced × Quick/Full). Stalls are dumped to
  `autoplay_stalls.log` with the snapshot, decision, prompt rows, slot clickability and
  recent tracker events, then a recovery ladder keeps the match moving.

### Changed

- Documentation rewritten for the struct-backed reader and policy layer: README, addon
  reference (hover-era material condensed into a history section), struct map, policy notes,
  rework plan status, release guide, tile table, working notes. Manifest text no longer
  advertises a win-probability display.

## [1.2.2] - 2026-09-19

## [1.2.0] - 2026-09-19

### Added

- Struct-backed Doman Mahjong snapshots with an editable layout and embedded fallback.
- Discard recommendations with exact shanten, two-step ukeire, and han/fu scoring.
- Opponent risk estimates and push/fold, call, and riichi policies with decision reasoning.
- Background analysis and an in-game advice overlay (`/mhater`, `/mhater config`).
- Logistic opponent tenpai estimate with per-hand ground-truth logging (`tenpai_calibration.csv`)
  and `tools/fit_tenpai.py` to re-fit it.

### Removed

- The localhost debug/operate API, `/mhater dump` and `/mhater debug`; Cartographer covers that
  surface during reverse-engineering.
