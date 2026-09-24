# Policy notes

`Core/Policy/` is the decision layer: pure C#, no Dalamud types, every sub-policy
constructor-injected into `DecisionPolicy` and unit-tested alone (`MahjongHater.Tests/Policy/`).
It composes the existing engine — `Shanten` (exact, per suit), `HandAnalyzer` (ukeire, two-step
ukeire, kept-hand value, waits), `YakuDetector` — into one `ActionChoice` per snapshot with a
`Reason` per stage. All tunables are the `PolicyWeights` record (defaults = shipped behaviour).

## Decision order (`DecisionPolicy.Choose`)

1. **Win** — if Tsumo/Ron is legal, count yaku han across every winning decomposition
   (`YakuDetector`, dora excluded from the gate); declare when `han ≥ MinHanDoman` (1).
2. **Opponent model update** — one transaction for the whole decision (the model is locked so two
   concurrent evaluations never interleave update/query).
3. **Call prompt** — `CallPolicy.Evaluate`; accept → that call; decline without a legal discard
   (a claim window) → `Pass`.
4. **No legal discard** → `Pass` with a 13-tile hand summary (shanten/ukeire/waits) for the overlay.
5. **Hand arithmetic** — closed tiles + 3·melds must be 14 (a kan counts as one set of 3, like
   `HandAnalyzer`); otherwise `None` ("hand out of sync") until the reader settles.
6. **Discard ranking** — `HeuristicDiscardPolicy.Rank`; no candidates → `None`.
7. **Push/fold** — `PushFoldPolicy`; folding re-sorts candidates by deal-in risk, then shanten,
   then score.
8. **Riichi** — only when pushing, `Legal.Riichi`, and the best candidate leaves tenpai; the
   choice then carries `Kind = Riichi` with the same discard tile.

`ActionKind.None` decisions are the ones an auto player cannot act on; they are the first
suspects when the game waits and nothing happens (see the stall dump in
[`EMJ_ADDON_REFERENCE.md`](EMJ_ADDON_REFERENCE.md)).

## Opponent model (`OpponentModel`, `TenpaiEstimator`)

- **Tenpai probability** per seat 1–3: riichi → 1; otherwise logistic
  `σ(intercept + perDiscard·discards + perMeld·openMelds + earlyOutside·early + lateMiddle·late)`
  clamped to `TenpaiMaxWithoutRiichi` (0.9). Defaults (−4.1, 0.25, 0.95, 0.3, 0.5) reproduce the
  tenpai-rate-by-turn curves quoted in riichi literature (no calls ≈ 5 % at turn 6, 17 % at 10,
  35 % at 14, 60 % at 18; one call ≈ 35 % at 10; two calls ≈ 70 % at 12). `early` = share of the
  first six discards that are terminals/honors; `late` = middle tiles (3–7) discarded from turn 7
  on, /12, capped at 1 — fixed denominators keep both monotone as discards arrive. The previous
  additive formula climbed to ~90 % late in every hand; that is why the estimate is **soft**
  everywhere it is used.
- **Ground truth** — every hand end appends one `TenpaiSample` per opponent (features at the
  freeze + tenpai yes/no: draw screens label every seat, a win proves the winner) to
  `pluginConfigs/MahjongHater/tenpai_calibration.csv`; `python tools/fit_tenpai.py` re-fits the
  same logistic form (plain gradient descent, L2), prints log-loss/Brier vs the shipped weights and
  a reliability table, and emits the `PolicyWeights` initializers to paste.
- **Danger** of a tile against a seat: genbutsu (in that seat's discards, or discarded by anyone
  after its riichi — tracked only before calls skip turns, via round indices) → `GenbutsuDanger`
  (0); honors → `HonorDanger` (0.16) × (4 − visible copies)/4; terminals 0.12, ranks 2/8 0.18,
  other numbers 0.24; ×`SujiDiscount` (0.5) when the suji is in that seat's discards (middle tiles
  need both sides); ×`KabeDiscount` (0.4) when a neighbouring rank shows four copies.
- **Value** of a seat's hand: `2000 + 800·melds + 2000·riichi + 1000·visibleDora` (indicator dora
  and red fives inside that seat's melds). **Expected deal-in cost** of a tile =
  Σ over seats of `tenpai × danger × value`.

## Discard ranking (`HeuristicDiscardPolicy`)

- Candidates come from `HandAnalyzer.Analyze` (never a second shanten implementation): for each
  discard the shanten after, ukeire, two-step ukeire, kept value (dora + red fives + best wait
  yaku), waits, and the open-yakuless flag.
- Per candidate: `DealInRisk = 1 − Π(1 − tenpai_s × danger_s)` over seats; ranking
  `Score = analyzerScore − DealInRiskWeight × expectedCost`; ordered by shanten first (analyzer
  scores use different scales per shanten), then score, then tile.
- **Riichi lock**: with `OurRiichi` only the drawn tile is a candidate (its exact red/plain copy);
  a red draw with a plain copy in hand is normalised so the analyzer does not "keep the red".
  No draw in hand → no candidates → `None`.

## Push/fold (`PushFoldPolicy`)

A declared opponent riichi is a hard threat; the tenpai estimate is soft. Fold when:
tenpai-after (`ShantenAfter = 0`) and riichi and value < `FoldMinValue` (2) and ukeire < 3;
1-shanten and riichi and cheap; otherwise `ShantenAfter ≥ FoldMinShanten` (2) and (riichi or
max tenpai estimate ≥ `FoldTenpaiThreshold` 0.6). Folding picks the lowest deal-in risk and
suppresses riichi. (Live 2026-09-19: the estimate alone used to fold a tenpai into 1-shanten
with nobody in riichi — hence the riichi gate on near-tenpai hands.)

## Calls (`CallPolicy`)

- `CallDescriptor` validates hand arithmetic, physical copies, existing melds, source seat,
  riichi restrictions and exact chooser shapes before policy evaluation. Each candidate
  records consumed tiles, remaining hand and resulting melds. Combined offers keep separate
  candidates for Pon, Kan and every available Chi shape. Invalid construction is reported
  separately from strategic rejection.
- Options built from the prompt: pon (two copies, plain before red), daiminkan (three
  copies), chi (kamicha only, honors excluded; with the chooser open only the game's offered
  shapes), ankan (four in hand; in riichi only the drawn kind), shouminkan (open hand, not in
  riichi).
- Pon/chi are accepted only if some post-call discard **lowers shanten** and keeps an **open yaku
  route**: a yakuhai triplet/claim (dragons, seat or round wind), all-simples with kuitan, or a
  ≥ 9-tile honitsu skeleton whose melds fit the suit. A chi that breaks the only pair is skipped.
- Kan must preserve shanten and ukeire (the claimed tile is removed from the seen set once); in
  riichi the wait kinds must be unchanged.
- Opening a closed hand with daiminkan is still declined by strategy, after recognizing its
  valid shape. Learned tempo calls must also preserve Kan safety.
- Ties: lower shanten, then pon > chi > kan. Each candidate logs its evaluation; Kan logs
  before/after shanten and live improving tiles, or the specific construction/strategy blocker.
- `LearnedCallPolicy` compares validated claim actions with both its prompt-based mask and
  `LearningFeatures.ClaimOptions` shapes before inference. A mismatch falls back to validated
  heuristic candidates and logs all three action sets. Its scoring mask contains only
  validated candidates plus Pass. Own-turn Kans explicitly report that they use the heuristic.
  Feature encoding and model weights are unchanged. These diagnostics reach `choice.Steps`
  and therefore the autoplay log for each attempted action.

## Riichi (`RiichiPolicy`)

Declare when: not already in riichi, closed hand, tenpai after the discard, wall ≥ `RiichiMinWall`
(4), ukeire ≥ `RiichiMinUkeire` (2 — tanki/shanpon/kanchan waits live on 2–3 tiles), and against
an opponent riichi ukeire ≥ 4. A dead wait (0 live tiles) never declares.

## Limits

- No global discard chronology in the snapshot: post-riichi genbutsu across seats is inferred
  from round indices only while no seat has called; after calls only the current offered discard
  and the target's own discards count as safe.
- Honba, riichi sticks and ura dora are 0 in the snapshot (unsourced); hand value ignores them.
- `ScoringEngine`/`FuCalculator` (han/fu/points, self-validated against reference scores) are not
  on the live path; value is yaku han + dora.
- Opponent estimates are heuristics plus one logistic fit; a few dozen matches of calibration rows
  are needed before `fit_tenpai.py` says anything the defaults do not.
- Cancellation: every stage checks the token; `AnalysisService` cancels after 2 s and publishes
  `TimedOut` so the overlay never waits on a stuck evaluation.
