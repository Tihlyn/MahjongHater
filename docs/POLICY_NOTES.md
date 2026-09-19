# Phase 2 policy notes

Implemented `OpponentModel`, `HeuristicDiscardPolicy`, `PushFoldPolicy`, `CallPolicy`,
`RiichiPolicy`, and constructor-injected `DecisionPolicy`. Existing contract members
are unchanged; additional heuristic weights are additive. All code is pure C#.
Shanten, ukeire, discard value, and winning yaku come from the existing engine.

## Formulas and decisions

- Non-riichi tenpai probability is clamped to [0, 0.99]:
  `0.05 + 0.025 * discards + 0.12 * openMelds + 0.08 * earlyOutside + 0.12 * lateMiddle`.
  `earlyOutside` counts honors/terminals in the first six discards divided by six;
  `lateMiddle` counts ranks 3–7 thereafter divided by twelve, capped at one.
  Fixed denominators preserve monotonicity as discards arrive. Riichi gives 1.
- Base danger: honors 0.16, terminals 0.12, ranks 2/8 0.18, other numbers 0.24.
  Honor danger scales by `(4 - visibleCopies) / 4`. Suji multiplies by 0.5;
  middle suji needs both sides. Four visible copies of a neighboring rank
  multiply danger by 0.4. Genbutsu uses `GenbutsuDanger` (default zero).
- Estimated opponent value is `2000 + 800 * melds + 2000 * riichi + 1000 * visibleDora`.
  Visible dora counts indicator dora and red fives in that opponent's melds.
  Expected cost is the sum of `tenpaiProbability * danger * value` over seats 1–3.
- Candidate risk is `1 - product(1 - tenpaiProbability * danger)`.
  Candidate score is `analyzerScore - DealInRiskWeight * expectedCost`.
  Shanten remains the first ranking key because analyzer scores use different
  scales for tenpai and non-tenpai. Existing analyzer tuning remains authoritative.
- Fold at the configured threat threshold if shanten is too high or estimated
  value is below `FoldMinValue` (default 2). Folding sorts all legal candidates
  by risk and suppresses riichi. An existing riichi permits only the exact draw,
  including its red/plain identity.
- Pon/chi must improve shanten after the mandatory discard and retain yakuhai,
  kuitan, or a compatible honitsu route. Chi is kamicha-only and protects the
  sole pair; pon wins equal-shanten ties. Kan is treated as the specified exception
  to strict shanten improvement: it must preserve shanten and ukeire. Open kan
  requires an already-open hand; riichi ankan also preserves the wait kinds.
- Winning declarations use `YakuDetector` across engine decompositions, exclude
  dora from the minimum-yaku gate, and use the snapshot's winds and riichi flag.
  Closed tsumo itself provides a yaku; the yakuless tsumo regression is open.

## Contract limits and integration notes

- The snapshot has no global discard chronology. Before open calls, dealer order
  and discard indices identify post-riichi discards. After calls can skip turns,
  only the current offered discard and the target's own discards are treated as
  known genbutsu. Historical cross-seat post-riichi safety after calls needs
  reader-provided chronology; inferring it from equal discard indices is unsafe.
- The explicitly requested discard guard counts physical meld tiles and requires
  exactly 14. Thus a normal post-kan draw with 15 physical tiles is rejected.
  The existing winning decomposer has the same physical-count limitation.
  Integration needs a contract/engine change to support kan-adjusted totals;
  those files were outside this task's scope.
- `Meld` factories normalize red fives and `MakeChi` defaults to closed. The call
  policy creates an appropriately open meld and restores physical tile copies
  on that new instance. The analyzer input puts the explicit drawn tile last,
  as required by the engine's riichi lock.
- Opponent estimates are deterministic heuristics, not calibrated probabilities.
  Concurrent choices serialize the injected model's update/query transaction;
  cancellation is checked between stages and while waiting for that transaction.

## Validation and delivery

`dotnet build MahjongHater.csproj -c Debug -nologo -v q` and
`dotnet test MahjongHater.Tests -nologo -v q` both passed. All 201 tests are green:
the original 119 plus 82 focused policy cases covering
danger, calls, defensive selection, win gates, red copies, cancellation,
concurrency, and complete decision flows. Only NU1900 warnings remain because the
NuGet vulnerability feed is inaccessible in this session.

Commits are blocked by the session filesystem policy: this worktree's Git
metadata is in `../MahjongHater/.git/worktrees/MahjongHater-policy`, outside the
writable root. `git add`/`git commit` fail creating `index.lock`. Changes remain
in the worktree; no Git permissions or external files were altered.
