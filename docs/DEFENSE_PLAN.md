# Defense plan — opponent model + push/fold rework (proposal, 2026-09-20)

Status: **proposal, not started.** The recommendation engine wins hands but deals in too much: it
ranks discards by our own hand first and treats deal-in risk as a tie-break, with a danger model
whose numbers are far from measured deal-in rates. This document records what was researched
(Mahjong Discord, Riichi City Discord, the books those communities cite, and one 1.2M-game
statistical dataset), what is wrong with the current layer, and a phased design to fix it. The
living references stay [`POLICY_NOTES.md`](POLICY_NOTES.md) and [`REWORK_PLAN.md`](REWORK_PLAN.md).

## 1. Diagnosis of the current policy

All findings are against `Core/Policy/` as of `997bd0e` (v1.3.0).

### 1.1 Risk is a rounding error in the discard ranking (`HeuristicDiscardPolicy`)

`Score = analyzerScore − DealInRiskWeight × ExpectedDealInCost`, candidates ordered by
`ShantenAfter` **first**, then `Score`.

- For shanten ≥ 1 the analyzer score is `ukeire × 10 000 + ukeire2 + 50 × value`; the expected
  cost is in points and is typically 0–2 000. One extra accepting tile (10 000) outweighs any
  deal-in cost, so risk never changes the choice unless ukeire ties.
- For tenpai the analyzer score is `ukeire × (1 + 0.35 × value)` ≈ 5–30, so the same cost term
  now *dominates* — even at turn 5 with nobody near tenpai (5 % × 24 % × 2 000 ≈ 24) it flips
  wait choices. The units are inconsistent in opposite directions on either side of tenpai.
- Because shanten is the primary key, a safe tile that costs one shanten can never outrank an
  attacking tile; only the binary `PushFoldPolicy` flip can do that, and it re-sorts by
  `DealInRisk` alone (no hand value, no "keep safe tiles for later").

### 1.2 The danger model is miscalibrated (`OpponentModel.EstimateDanger`)

Measured deal-in rates against a riichi, Tenhou Houou room (top 1 %), 1.2 M games
("Path of Houou" analysis, see §6) versus the shipped constants:

| Tile class (vs riichi) | Shipped danger | Measured | Error |
|---|---|---|---|
| Honor, 0 copies visible | 16 % | 3.2 % (dragon) / 4.0 % (seat wind) / 2.6 % (guest wind) | ~5× too high |
| Honor, 1 visible | 12 % | 1.3 % | ~9× |
| Honor, 2 visible | 8 % | 0.14 % | ~60× |
| Honor, 3 visible | 4 % | 0.0 % (tanki/kokushi only) | ∞ |
| Non-suji 1/9 | 12 % | 6.6 % | 1.8× |
| Non-suji 2/8 | 18 % | 8.2 % | 2.2× |
| Non-suji 3/7 | 24 % | 9.6 % | 2.5× |
| Non-suji 4/5/6 | 24 % | 14.4 % | 1.7× |
| Suji 1/9 | 6 % (0.5×) | 1.5 % (0.23×) | wrong ratio |
| Suji 2/8 | 9 % (0.5×) | 3.0 % (0.37×) | |
| Suji 3/7 | 12 % (0.5×) | 4.5 % (0.47×) | |
| Half-suji 4/5/6 (one side) | 24 % (no discount) | 8.3 % (0.58×) | missing |
| Nakasuji 4/5/6 (both sides) | 12 % (0.5×) | 2.2 % (0.15×) | |
| Suji of the **riichi tile**, 2/8 | 9 % | 5.2 % (1.7× a normal suji) | not distinguished |
| Suji of the riichi tile, 3/7 | 12 % | 7.7 % | not distinguished |

Consequences: the shipped ordering is *inverted* for the most common fold decision — it throws
non-suji terminals (12 %) before honors (16 %), while real play is the opposite (6.6 % vs 3 %).
It does not distinguish 3/7 from 4/5/6, gives half-suji middles no credit, and treats the riichi
declaration tile's suji as safe.

Also missing entirely:

- **Live-suji dependence.** Non-suji 4 goes from 8 % with 18 live suji to 20 % with 8 and 35 %
  with 2 (same dataset). Every guide says "late game, non-suji tiles get much more dangerous";
  the model is flat in time.
- **One-chance** (3 of the neighbour visible) and the honor "third copy visible → tanki only" tier.
- **Dora** as a danger factor: dora tile ≈ 1–2 ranks worse (×1.2–1.7), neighbours ×1.1 (Fukuchi).
- **Early-outside**: tiles outside a seat's early discards ≈ 0.6× (Fukuchi; stronger vs open hands).
- **Post-riichi genbutsu after any call** (`PassedAfterRiichi` gives up once anyone has called).

### 1.3 Push/fold ignores the inputs every source uses (`PushFoldPolicy`)

The decision is `fold = f(ShantenAfter, riichi flag, value < 2, ukeire < 3, tenpai estimate ≥ 0.6)`.
It does not see: **turn** (early/mid/late), whether the threat is the **dealer**, our **wait
quality** (good ≥ 5–6 live tiles vs bad), our **hand value in points**, **which tile** we would
have to cut (danger rank), how many **safe tiles** we hold (can we actually fold?), or the
**score situation**. All of the literature's push/fold criteria are functions of exactly those.

### 1.4 Opponent value is coarse

`2000 + 800·melds + 2000·riichi + 1000·visibleDora`. Statistically a winning riichi averages
**7 000 (non-dealer) / 9 800 (dealer)** with 3 aka (Fukuchi, from Scientific Mahjong). A riichi
valued at 4 000 halves the cost of every push. Open hands should be valued from visible yaku
(yakuhai/dora melds, honitsu skeleton, toitoi) rather than a flat per-meld bonus.

### 1.5 What is fine

- Exact shanten / ukeire / 2-step ukeire engine and the off-thread `AnalysisService`.
- The logistic **tenpai estimate** is in the right ballpark against the quoted data (Fukuchi: 3
  calls ≤ turn 6 ≈ 50 %, mid ≈ 80 %, late ≈ 90 %; 2 calls turn 10 ≈ 50 %, late ≈ 70 %; 1 call
  turn 13 ≈ 50 %). Keep it; keep the calibration CSV.
- `RiichiPolicy`'s "bad wait vs riichi → don't chase" line (ukeire < 4) matches the chase rules.
- `ScoringEngine`/`FuCalculator` exist and are validated — just not on the live path.

## 2. What the sources agree on (the model we should implement)

Distilled from Riichi Book 1 ch. 8, Fukuchi's *Riichi Mahjong Strategy* ch. 2, the Statistical
Mahjong round-balance tables, and the Riichi City #wwyd regulars (citations in §6).

1. **Threat gating.** Defend only against (a) a declared riichi, (b) an open hand that is likely
   tenpai *and* worth ≥ mangan-ish (yakuhai/dora melds, flush), (c) late-game open hands with 2–3
   calls. Dama is ignored except as a late-game caution for narrow hands.
2. **Tile danger is a rank.** S genbutsu 0 % · A+ tanki honor 0.9 % · B suji 1/9 2.9 % ·
   C non-tanki honor 3.4 % · D suji 2/8 4.8 %, suji 3/7 5.5 %, nakasuji 4–6 · E non-suji 1/9
   6.3 %, half-suji 4–6 7.0 %, non-suji 2/8 7.0 %, non-suji 3/7 7.1 % · F non-suji 4–6 12.3 %.
   Modifiers: kabe no-chance ≥ suji; one-chance between non-suji and suji; early-outside ×0.6;
   dora ×1.2–1.7, dora neighbours ×1.1; riichi-tile suji ≈ one rank worse; danger of E/F tiles
   roughly doubles from mid to late game as suji die (the live-suji table quantifies this).
3. **Push/fold is "our hand vs the tile we must cut".** Riichi Book 1's shortcut: push on two of
   {tenpai, ≥ 7 700, good wait}; fold on two of {1-shanten+, cheap, bad wait}. Riichi Book 1's
   discard gate: tenpai may cut D; 1-shanten pushing cuts ≤ C (D only with a guaranteed mangan);
   2-shanten cuts ≤ B; otherwise betaori. Fukuchi's tables refine this by turn (4th/7th/12th),
   dealer/non-dealer on both sides, wait shape and hand value — e.g. non-dealer vs non-dealer,
   good wait, mid game: cut a 10 % tile with ≥ 2 000 open (riichi-only borderline), cut a 5 %
   tile with anything; late game: 10 % needs ≥ 2 000 riichi / 2 600 open, 5 % anything but 1-han
   open. Bad wait roughly doubles the required value; vs the dealer roughly doubles again.
4. **1-shanten is not half a tenpai.** With ~10 % per-turn tenpai chance (one-sided 1-shanten)
   even 18 000 does not justify a 7 % tile; ~20 % (double ryanmen) justifies 7 % with mangan;
   > 20 % (headless / sticky shapes) plays like a bad-wait tenpai. 2-shanten: fold, unless wide,
   expensive and callable. Against a riichi, **call anything that reaches tenpai** with a yaku.
5. **Folding is a technique, not "min risk".** Fold EV is ≈ −1 100 to −1 700 for a non-dealer
   (opponent tsumo ~40 % × ~1 800 + noten ~30 % × ~1 200), which is the bar a push must beat.
   When folding: cut pairs/triplets of safe tiles to bank turns; keep tiles that stay safe (genbutsu
   > suji); vs two threats pick tiles *confirmed* safe vs the more dangerous seat, then safest vs the
   other; prefer discarding tiles opponents can call; with no safe tiles, cut terminals / tiles that
   deny tanyao and stay away from dora.
6. **Riichi vs riichi.** Chase with a good wait even riichi-only; bad wait needs ≥ 5 200 (non-dealer
   vs non-dealer) or mangan vs the dealer; otherwise dama and fold on the first dangerous draw.
   Choose the good wait over value when chasing, even at half the score.
7. **Just before a draw**, only the tile's danger matters: push at x < 26 % vs non-dealer mangan,
   < 18.5 % vs dealer mangan when noten payment is certain; less if turns remain.
8. **Placement.** Leading → fold more (especially South); trailing in South → push more; in all-last
   evaluate placement, not points. (Deferred to a later phase; scores are in the snapshot.)

## 3. Design

Everything stays pure C#, constructor-injected, weight-driven, and testable alone. New pieces are
added behind `PolicyWeights` and a config switch (`DefenseModel = Legacy | V2`) so both can run
live for comparison.

### 3.1 `TileDangerModel` (replaces `OpponentModel.EstimateDanger`)

`Danger(tile, seat) → (double p, DangerRank rank, string why)` — probability of dealing in
**given that seat is tenpai**, plus the rank and a one-line explanation for the overlay.

Inputs per seat (all already in `StateSnapshot`/`SeatState` or derivable): discards with index,
`RiichiDiscardIndex` (→ riichi declaration tile), melds, visible tile counts, dora indicators,
seat/round winds (yakuhai honors), our hand (kabe from our own concealed sets counts twice).

Lookup order:

1. Genbutsu (seat's own discards; anything discarded by anyone after that seat's riichi — see 3.6;
   the tile our kamicha just discarded is safe vs everyone this turn) → 0.
2. Honors: by copies visible {0,1,2,3} × {yakuhai for that seat, guest wind, dora}; 3 visible → 0
   unless kokushi is live.
3. Number tiles: category ∈ {non-suji, suji, riichi-tile suji, half-suji, nakasuji} × rank
   {1/9, 2/8, 3/7, 4/5/6} × **live-suji bucket** for that seat (18 suji minus those denied by
   the seat's discards, bucketed 18–13 / 12–9 / 8–5 / 4–0). Base rates from the Houou table.
4. Multipliers: kabe (no-chance → floor to the suji rate for that rank; double one-chance ×0.5;
   one-chance ×0.75 early, ×1.0 late), early-outside ×0.6 (discard within the seat's first six and
   tile is 1–2 ranks outside it), dora ×1.3 (×1.7 terminals), dora neighbour ×1.1, riichi-tile
   suji handled by category.
5. Against **open hands** with a toitoi/honor-heavy read, suji/kabe do not apply — use raw-tile
   danger (live tiles up, dead tiles down).

Tables live in `resources/policy/deal_in_rates.json` with attribution; loaded once, no numbers in
code (same rule as the layout JSON). Unit tests pin every row and the rank ordering.

### 3.2 `ThreatModel` (evolves `OpponentModel`)

Per seat: `Tenpai` (keep logistic; riichi → 1), `IsRiichi`, `IsDealer`, `Value` in points
(riichi: 7 000 / 9 800 dealer, + 1 000 per visible dora/aka; open: yakuhai melds, dora in melds,
flush skeleton, toitoi read; floor 1 300 / 2 000 dealer), `LiveSuji`, `RiichiTile`,
`Danger(tile)` from 3.1, and `ExpectedLoss(tile) = Σ Tenpai × Danger × Value` in real points.
Also `SafeTiles(hand)`: count of our tiles at rank ≤ C vs the primary threat (fold feasibility).

### 3.3 Turn and hand value (`PolicyContext`)

Derived once per snapshot: `Turn = our discard count + 1` (fallback `(70 − wall)/4`),
`Phase = Early ≤ 6 | Mid 7–11 | Late ≥ 12`, `WeAreDealer`, and our **value in points** via
`ScoringEngine`/`FuCalculator` per wait (min / expected for tenpai; dora + probable yaku + riichi
for 1-shanten). `DiscardCandidate` gains `Danger`, `DangerRank`, `ValuePoints`, `WaitGood`,
`TenpaiChance` (1-shanten: ukeire × 5/6 %).

### 3.4 Push/fold as a **danger budget** (`PushFoldPolicy` v2)

Instead of a binary stance, compute `MaxDanger` — the highest deal-in probability we accept
cutting this turn — from (our shanten, wait quality / tenpai chance, value points, phase,
threat is dealer, we are dealer, safe-tile count, number of threats). Initial table transcribed
from Fukuchi §2.3 (5 % / 10 % thresholds by value) and Riichi Book 1's rank gate, e.g.:

| Our hand | Non-dealer vs non-dealer riichi | vs dealer riichi |
|---|---|---|
| Tenpai, good wait, early | any tile | any tile |
| Tenpai, good wait, mid | 10 % ≥ 2 000; 5 % any | 10 % ≥ 2 000 riichi; 5 % ≥ 2 000 open |
| Tenpai, good wait, late | 10 % ≥ 2 000 riichi / 2 600 open; 5 % except 1-han open | 10 % ≥ 2 600 riichi / 3 900 open; 5 % ≥ 2 000 |
| Tenpai, bad wait, mid | 10 % ≥ 3 900; 5 % ≥ 2 600 riichi | 10 % ≥ 5 200; 5 % ≥ 5 200 (3 900 open borderline) |
| Tenpai, bad wait, late | 10 % ≥ 5 200; 5 % ≥ 3 900 open | 10 % ≥ mangan; 5 % ≥ 5 200 |
| 1-shanten, ~20 % tenpai chance | 7 % with mangan | 7 % with haneman |
| 1-shanten, ~10 % | fold (safe tiles only) | fold |
| 2-shanten+ | rank ≤ B only (betaori) | betaori |

Two threats: budget from the more dangerous seat, then the tile must also pass the other. Zero
safe tiles → push regardless (with the least dangerous tile). Fold reference EV ≈ −1 100.

The discard ranking then becomes: **among candidates with `Danger ≤ MaxDanger`, best attacking
candidate**; if none qualify, `BetaoriPolicy` (3.5). This makes shanten-first ranking correct
again (it only ranks tiles we have already decided we are willing to cut) and gives the overlay a
crisp explanation: "Budget 5 % (tenpai, bad wait, 2 600, late, vs dealer riichi) — 3p is 7.7 %,
West is 1.3 %."

### 3.5 `BetaoriPolicy` (fold-mode discard choice)

Rank by: danger vs the primary threat (dealer / riichi / highest `Tenpai × Value`), then vs the
others; prefer pairs/triplets of safe tiles (turns banked); prefer genbutsu over suji when equal;
keep common-safe tiles when two threats exist; among equals, the tile least useful to our hand
(so we can resume attacking). Report `SafeTurns` for the overlay.

### 3.6 Reader/tracker support

- **Global discard sequence**: `EventTracker` stamps every discard with a monotonic counter so
  "discarded after seat X's riichi" works after calls (fixes the `PassedAfterRiichi` limit).
- **Riichi declaration tile**: already `RiichiDiscardIndex`.
- **Tedashi vs tsumogiri** (open-hand tenpai reading): check whether the type-8 discard event
  carries a from-hand flag; if not, defer.
- **Round-end reveal**: at exhaustive draw all tenpai hands are shown and at a win the winner's
  hand is; capture them for labelling (3.8).

### 3.7 Calls and riichi vs a declared riichi

- `CallPolicy`: if any seat is in riichi and a call reaches tenpai with a yaku, accept (value is
  secondary). If we are folding, decline calls that would force a dangerous discard.
- `RiichiPolicy`: chase with a good wait regardless of value; bad wait needs ≥ 5 200 (≥ mangan vs
  dealer); when the wait is on a safe tile and the hand is ≥ 6 400, prefer dama (later).

### 3.8 Calibration and validation

1. **Unit tests**: danger table rows, rank ordering, modifiers, budget table cells, betaori
   ordering, call/riichi rules.
2. **Golden positions** (`MahjongHater.Tests/Policy/Golden/*.json` → `PolicyFixtures.Snap`): the
   worked examples from Riichi Book 1 ch. 7–8 and Fukuchi ch. 2, plus ~20 Riichi City #wwyd
   threads with a clear consensus (cited by permalink). Each asserts stance, budget, discard.
3. **Deal-in ground truth**: extend the calibration CSV — per own discard log (tile, class,
   rank, p, live suji, threat seat flags); at hand end label whether the tile *was* a winning tile
   for any seat revealed tenpai (winner's hand, tenpai hands at draw). `tools/fit_danger.py`
   reports calibration per class (like `fit_tenpai.py`) and proposes multipliers for the FF14
   population (NPCs and casual players riichi on worse waits than Houou, so suji should be
   discounted less there; the data decides).
4. **A/B via auto play**: the existing `AutoPlayer` + requeue loop runs N matches per policy;
   per-match summary (win %, deal-in %, avg placement, avg points) to a CSV. Targets: deal-in rate
   down from the current live number (measure first) toward ~12 %, win rate not below current.
5. **Optional oracle**: export our matches as mjai JSON (others' hands masked) and run
   `mjai-reviewer -e mortal` for disagreement mining. Mortal outputs no danger levels, so it is a
   judge, not a source of tables.

## 4. Phasing

| Phase | Scope | Deliverable |
|---|---|---|
| 0 | Instrumentation | `PolicyContext` (turn/phase/dealer/points), per-discard danger log rows, round-end reveal capture, discard sequence counter, vendored tables + attribution, golden-fixture format |
| 1 | **Danger model** | `TileDangerModel` + tests; `OpponentModel.Danger` delegates; fold ordering fixed; overlay shows rank + % per candidate. Biggest single win, lowest risk. |
| 2 | Threat value in points | riichi 7 000 / 9 800 baseline, open-hand yaku reading, `ScoringEngine` on the live path for our value, `ExpectedLoss` in real points, `DealInRiskWeight` units fixed |
| 3 | Push/fold v2 | danger budget table, wait quality / tenpai chance, safe-tile count, `BetaoriPolicy`, call-vs-riichi and chase rules |
| 4 | Calibration loop | `fit_danger.py`, A/B auto-play runs, weight tuning, golden positions from live hands |
| 5 | Later | placement / all-last EV, tedashi tracking, dama logic, mjai export for Mortal review |

Phases 1–3 are each shippable behind `DefenseModel = V2`; phase 1 alone fixes the inverted fold
ordering and honours-vs-terminals mistake.

## 5. Open questions (to settle during phase 0)

- Does the type-8 discard event distinguish tedashi from tsumogiri? (Needed only for phase 5.)
- Does the draw recap expose every tenpai hand (labels for `fit_danger.py`)? Believed yes.
- Ruleset: Doman Mahjong = kuitan on, 3 aka, ippatsu/ura on, noten payments — confirm the
  round-balance assumptions (Fukuchi assumed the same) and whether Quick Match (East only) should
  push harder in East 4 as "all last".
- Which population to calibrate against first: Novice queue NPCs (current auto-play loop) or
  real players. Proposal: log both, tag rows, fit separately.

## 6. Sources

Community (Discord, read-only research account; permalinks):

- Mahjong Discord #guides — moderator's canonical reading list (Riichi Book 1, mahjong.guide
  fundamentals, push/fold chart): <https://discord.com/channels/150412836500275200/584898975446859796/1190782153005477898>,
  <https://discord.com/channels/150412836500275200/584898975446859796/592962441323741184>
- Mahjong Discord #bot-spam — Natsuki's push/fold chart sheet (hand shape × tile class × turn,
  source epsilon69399 simulations): <https://discord.com/channels/150412836500275200/629737480803057685/1190781638037225493>
- Mahjong Discord #dev — Mortal is the review oracle; its NN emits no danger levels (KillerDucky
  adds suji heuristics in the viewer): <https://discord.com/channels/150412836500275200/757876760569446470/1215690955794153563>;
  tenhou-python-bot's deterministic defence (acceptance / safety / push-fold) as an open reference:
  <https://discord.com/channels/150412836500275200/757876760569446470/1532793339970322672>
- Riichi City Discord #wwyd — round-balance reasoning with the Statistical Mahjong tables
  ("900 push vs −1 700 fold", tile danger diagram, assumptions: 1-shanten→tenpai, dealer riichi,
  ryanmen): <https://discord.com/channels/941185061057888347/1099537738291744868/1535598089086439484>,
  <https://discord.com/channels/941185061057888347/1099537738291744868/1535595368975306835>,
  <https://discord.com/channels/941185061057888347/1099537738291744868/1535598686812377179>;
  betaori EV derivation (≈ −1 100 non-dealer): <https://discord.com/channels/941185061057888347/1099537738291744868/1543544357968154686>;
  "folding ≈ −1.5k, a push that loses less is still right": <https://discord.com/channels/941185061057888347/1099537738291744868/1543496212018102343>;
  dama-to-keep-fold-option with bad waits: <https://discord.com/channels/941185061057888347/1099537738291744868/1543677318021775413>;
  placement-aware fold (4th-place South player): <https://discord.com/channels/941185061057888347/1099537738291744868/1547196960275046480>;
  value comparison vs a visible half-flush: <https://discord.com/channels/941185061057888347/1099537738291744868/1541915692670918657>

Books and data:

- Daina Chiba, *Riichi Book 1*, ch. 7 (riichi judgement, good/bad waits, 5 200 rule) and ch. 8
  (push/fold 2-of-3, suji/kabe tables, safety ranking, rank gate by shanten, open-hand tenpai
  heuristics) — <https://dainachiba.github.io/RiichiBooks/>, LaTeX source
  <https://github.com/dainachiba/RiichiBooks> (`RiichiBook1-ch8-defense.tex`).
- Fukuchi Makoto, *Riichi Mahjong Strategy* (fan translation), ch. 2 "Push-fold judgment": tile
  danger diagram (S–F with rates), modifiers (kabe, early-outside ×0.6, dora ×1.2–1.7), push
  thresholds by turn / dealer / wait / value, 1-shanten tenpai-chance tiers, open-hand tenpai
  rates by calls × turn, two-riichi rules, pre-draw formula — <https://files.catbox.moe/chwp6q.pdf>
  (listed at <https://mjg-repo.neocities.org/guides>). Based on *Scientific Mahjong* (Totsugeki
  Tōhoku).
- みーにん, *Statistical Strategies for Riichi Mahjong* (「統計学」のマージャン戦術, translation):
  round-balance tables (safe vs 10 % tile × turn 8/11/14 × hand value) — as circulated in the
  Riichi City thread above.
- "Path of Houou", *Tile deal-in rates by live suji* (1.2 M Houou games): number-tile sheet
  <https://docs.google.com/spreadsheets/d/1zvYPme17Nwdquq3b2GHqCWvmeQh7L0CowKGzNYGx2ZY>, honor
  sheet <https://docs.google.com/spreadsheets/d/1ExcR9O_GDvsXU4aEL7lnnln22UpWVjOPMdC_W9rzdLQ>,
  post <https://pathofhouou.blogspot.com/2020/05/analysis-tile-deal-in-rates-by-live-suji.html>.
- osamuko, *Identifying dangerous suji* (riichi-tile suji frequencies) —
  <https://osamuko.com/identifying-dangerous-suji/>.
- mahjong.guide, *Fundamentals 6: How to defend*, *7: When to defend* (fold/push thresholds,
  wait ≥ 5 tiles = good) — <https://mahjong.guide/2018/02/04/mahjong-fundamentals-6-how-to-defend/>,
  <https://mahjong.guide/2018/05/11/mahjong-fundamentals-7-when-to-defend/>.
- Reference implementations (ideas only; check licences before borrowing code):
  tenhou-python-bot `project/game/ai/defence/` (MIT), Mortal / mjai-reviewer (AGPL) —
  <https://github.com/MahjongRepository/tenhou-python-bot>, <https://github.com/Equim-chan/mjai-reviewer>.
