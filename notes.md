# Working notes

Scratch list of what is being worked on and what is still open. The durable references are in
`docs/` (see `README.md`); this file is for the current thread of work only.

## Current thread — riichi stall vs real players (2026-09-19)

- Suspected: an uncaught stall on a specific riichi when playing against real players.
- Tooling in place: the AUTO PLAY card (play + requeue Novice Quick/Full) and the stall dump at
  `pluginConfigs/MahjongHater/autoplay_stalls.log`. Read the newest block first: the `decision:`
  line, the `slots` discardability list and the last tracker notes say whether the reader, the
  policy or the click is at fault.
- First things to check in a dump: a `None` decision in riichi (`riichi locked: drawn tile` with
  no draw in hand, `hand out of sync`), a wanted slot without an addon-bound activation (the game
  greys tenpai-breaking tiles after the riichi call — the actuator falls back down the candidate
  list and logs `RECOVERY`), or a call window the tracker thinks is still open after the riichi
  echo.
- The queue/commence/End-match path had not run live when this was written — confirm from the
  `[Queue]` log lines on the first run.

## Open items

- Honba, riichi sticks, ura dora: unsourced (text-node candidates in `docs/EMJ_STRUCT.md`).
- Kan never executed live; chooser Cancel untested; real-player turn timers uncharacterised.
- Settings game length / double-wind fu / dora display are stored but not consumed;
  `ScoringEngine` + `FuCalculator` are not on the live path (value = yaku han + dora).
- Tenpai estimate: re-fit with `tools/fit_tenpai.py` once a few dozen matches of
  `tenpai_calibration.csv` rows exist.
- Label-edge phantom window (~40 ms) on some opponent discards: harmless, noisy.
- Phase 4 leftovers: policy golden positions from real hands, whole-match replay harness.

## Resolved (kept for context)

- Hover-based tile reading, wrong-tile highlights, prompt handling after chi/pon/pass and the
  "scan the whole Emj tree" idea — all superseded by the struct reader (2026-09-18) and
  Cartographer (`dldm_reverse`, port 9790) for RE.
