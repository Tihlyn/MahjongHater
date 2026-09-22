# Changelog

Notable changes are recorded here using the Keep a Changelog format and semantic versioning.

## Unreleased

## [2.2.0.0] - 2026-09-22

### Changed

- Addon interaction is guarded end to end (docs/research/ADDON_INTERACTION_2026_09_22.md).
  An operate call now either dispatches at a target it has just verified against the live
  addon, or refuses and names the guard that refused: the whole ancestor chain must be
  visible, a call row must be the single matching, renderer-backed, enabled row of the live
  item table, and the list's rows must still carry the options the open window advertises.
  The row-0 fallback, the synthetic click and the pooled-text list click are gone.
- "Dispatched" and "accepted" are now separate outcomes. The journal, the overlay and the
  stall dump say whether the game actually moved after a click, instead of reporting every
  delivered event as a success.
- The click style resets to activation-only at each match boundary, and the hover fallback
  is opt-in (Diagnostics → Addon interaction). It now needs an activation that really
  reached the game and was then ignored; a refused dispatch or an unanswered list row no
  longer changes how clicks are sent. A hover cycle also requires a matched
  MouseOver/MouseOut pair on one holder — no invented MouseOut against the addon.
- Call rows can optionally be committed with AtkComponentList.SelectItem instead of the
  registered ListItemClick, for comparison. Off by default; the registered event is the
  route the 2026-07/09 sessions verified.
- tools/Precompute `train` reports and skips the snapshots it cannot use instead of aborting
  the whole corpus on the first one (8 of 79 rows recorded on 2026-09-22 hold a fifth
  visible copy of a tile). An offline table is also loaded from the plugin folder and from
  resources/policy, and a .json.gz table is read directly, so a generated table ships with
  the plugin like the learned models.

### Added

- The scoring rules are now checked against the game every hand. The win screen states fu,
  han, the limit it applied and what the winner collects; `ScoringEngine`'s payout table is
  compared against it for all four seats and logs `[Rules] SCORING MISMATCH` when they differ.
  A cross-check of the whole ruleset against the Lodestone pages found no discrepancy, and
  the table reproduced all 23 payouts observed on 2026-09-22 exactly
  (docs/research/RULES_CROSSCHECK_2026_09_22.md). The one boundary no observed hand covers is
  round-up mangan at 4 han 30 fu / 3 han 60 fu; the check will report it if it ever appears.

### Fixed

- A win the game offers is now declared instead of re-judged. On 2026-09-22 the plugin
  declared riichi on a 6m/9m wait, ankan'd, and then answered the game's own Ron window on
  6m with Pass because its own hand evaluation scored that hand as no win; the hand ran out
  as an exhaustive draw. The game only opens a Tsumo/Ron window for a complete, yaku-bearing
  hand, so that evaluation is now an explanation rather than a gate - and when the two
  disagree, the closed hand, melds, riichi flag and offered tile are logged at Warning so
  the hand read can be repaired (docs/research/WIN_OFFERS_2026_09_22.md).
- The offer is only authoritative when a prompt event opened the window. A window guessed
  from the panel's text keeps the old yaku gate, so a phantom "Tsumo" label still cannot
  become a declaration.
- A call window the game corroborates is no longer discarded because our own hand read cannot
  explain it. The tracker dropped any claim window whose tile our closed hand could not call -
  the guard that separates our prompt from another seat's Pon!/Chi! banner, which shares the
  type-19 event - and that guard consumed the very hand read that drifts. A type-23 whose row
  count equals its option codes plus Pass describes a row list the game is showing us (145/145
  frames on 2026-09-22); those now open regardless, and record the disagreement. The bare
  type-19 and the label edge are still gated, since for them the hand read is the only evidence.
- Every confirmed claim window is now a check on the hand read: which of Chi/Pon/Kan a tile
  allows is pure tile arithmetic, so the game's offer and our own inference must agree, and a
  difference is reported as a read-health note naming the direction of the drift.
- The snapshot's read-health notes now reach the Dalamud log whenever they change, and a
  hand whose closed tiles and melds do not total 13 or 14 is one of them. Both used to go
  only to the overlay and stall dumps, so a session log could not show that a decision had
  been taken on a drifted hand.
- A call answer now names the window it was aimed at. A native handler can open the next
  prompt inside ReceiveEvent — a Chi row raising its shape chooser does — and the old code
  cleared whatever window was open after the click, losing the new prompt; a failed Ron or
  Tsumo likewise declared a win that had not happened.
- Stall recovery no longer discards when no discard is legal (during the 18:27 stall every
  hand slot had a registered activation while the snapshot said legal=None), and no longer
  answers Pass on a hidden list carrying a finished hand's labels.
- Event type, chain parameter and node ids are copied before ReceiveEvent, not read
  afterwards: a handler may rebuild the tree while the call is still running.
- A list event is dispatched with a zeroed, list-shaped payload and a renderer taken from
  the item table. AtkEventData is a union whose mouse coordinates overlap the renderer
  pointer at offset 0, so a missing renderer left coordinates in a pointer field.
- Anti-AFK now reads the game's idle counters and sends a brief Control press to its
  window when idle, with a delayed release and before/after diagnostics. It no longer
  overwrites the timers. The v2.0.2.1 attribution to AntiAfkKick below was incorrect:
  that plugin reads the timers and sends input, rather than zeroing them.

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
