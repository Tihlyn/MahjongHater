# Rework plan — struct-backed state + policy layer

Written 2026-09-18 after comparing against XeldarAlz/FFXIV-AutoMahjongSolver (AGPL — we borrow
*findings and structure*, never code). Live verification of the key finding was done the same day
with Cartographer (`C:\Users\capta\Desktop\dldm_reverse`): the full 14-tile local hand is readable
directly from the `AddonEmj` struct. That collapses the hover / node-scan / AtkValues-slice pipeline
for the hand into a 56-byte read.

## Goals

1. **One reader.** `GameStateReader` (2.9k lines, three tile-reading strategies) becomes a thin
   struct reader plus a small event tracker, producing an immutable `StateSnapshot`.
2. **One decision layer.** `HandAnalyzer` stays as the *engine* (exact shanten, ukeire/ukeire2,
   yaku/score/fu). A new *policy* layer (discard, opponent model, push/fold, call, riichi) composes it
   into an `ActionChoice` with human-readable reasoning — the part the other repo does well and we
   don't have at all.
3. **Keep what's better here.** Exact per-suit shanten, 2-step ukeire, off-thread `AnalysisService`,
   the two-way `/mhater` debug API, `EmjOperator` as the actuator.

## Confirmed facts (2026-09-18, EU client, Dalamud 15.0.3.5 / CS 7.56.2)

| Offset (from `AtkUnitBase*`) | Meaning | Status |
|---|---|---|
| `+0x0DB8` | Hand: 14 × int32 icon IDs, base 76041 (34/35/36 = red 5m/5p/5s). Slots 0–12 sorted 13-tile hand, **slot 13 = drawn / claimed tile** (0 when none). Zeroes on discard, refills on draw. | **Verified across a discard→draw cycle** |
| `+0x0500 / +0x07E0 / +0x0AC0 / +0x0DA0` | Scores self / shimocha / toimen / kamicha (int32) | Read 25000 ×4 at match start — plausible, not yet seen changing |
| `+0x0FD8` | Dora indicator (int32 icon ID) | Read 5p once — unverified against screen |
| `+0x04FE` (+0x2E0 per seat) | Per-seat discard-count byte | From XeldarAlz layout; unverified |
| AtkValues `[0]` | State code: 6 = our draw/turn, 15 = others' turns, 30 = discard prompt (theirs), 19 call window, 21 deal, 29 score, 32 win (ours) | Partially verified |
| AtkValues `[1]` | Wall count (baseline flips at transitions — derive from discard counts instead) | Their finding |

Seat blocks appear to have a **0x2E0 stride** (score offsets differ by exactly 0x2E0). That strongly
suggests each seat's record (discards, melds, riichi flag…) lives in a 736-byte block; XeldarAlz never
found the discard arrays (`SelfDiscardArray` etc. are `null` in their layout). Phase 0 hunts them.

## Phases

### Phase 0 — Struct mapping (live, Cartographer) — *parallel with 1 and 2*

Owner: RE subagent. Needs a running match; advances turns by tsumogiri only, declines every call.

- `/mem/watch?addon=Emj` → play turns → `/mem/changes`, `/mem/analyze`; correlate with `/events`.
- Targets, in priority order: per-seat **discard arrays** (icon-ID ints inside the 0x2E0 blocks),
  own **melds** record, **riichi** flags per seat, **round/seat wind**, **dealer**, **wall remaining**,
  **honba / riichi sticks**, **ura dora**, call-prompt tile + source seat.
- Verify the score / dora / discard-count offsets against the screen.
- `/mem/annotate` every confirmed field; `/mem/cs` → ClientStructs skeleton.
- Deliverables: `docs/EMJ_STRUCT.md` (offset table, evidence per row, hex fixtures),
  `resources/layouts/emj.json` (schema below), and a note in `docs/EMJ_ADDON_REFERENCE.md`
  pointing at it. Unknown fields stay `null` — the reader must tolerate that.

### Phase 1 — Reader consolidation — `Core/State/`

Owner: reader subagent (worktree). Builds against `Core/State/StateSnapshot.cs` (committed contract).

- `EmjLayout` — loads `resources/layouts/emj.json`; nullable offsets; `Validate(rawHand)` requires
  every non-zero slot to decode (self-check against patch shifts, ±8 base search like theirs).
- `EmjStructReader` — framework-thread only; reads hand / scores / dora / discard counts / any
  Phase-0 fields into a `StructFrame` (plain ints, no Dalamud types → unit-testable from hex dumps).
- `EventTracker` — the *minimum* of today's AtkValues logic still needed: per-seat discards (until
  Phase 0 finds the arrays), call-window detection + labels (`/listrows`), meld inference from
  hand deltas (13→11 closed + call prompt = pon/chi; explicit type-17/21 frames as confirmation),
  round transitions (type 21 with `roundEnded`), win/score frames.
- `SnapshotBuilder` — `StructFrame + tracker → StateSnapshot`; `Sequence` bumps only on change.
- `GameState` is deleted; `AnalysisService`, `MainWindow`, `Plugin` consume `StateSnapshot`.
- `EmjScanner` hand-slot face reading, `TileFaceMap`, `HandTracking` hover path: **removed** from
  the read path. `EmjScanner` keeps only what `EmjOperator` needs to *act* (slot node lookup,
  call-list rows). `FaceKeys`/`TileFaceMap` may stay as an optional cross-check behind a config flag.
- Tests: hex-dump fixtures (`resources/fixtures/*.hex` captured via Cartographer `/mem`) →
  `StructFrame` → `StateSnapshot`; event replays from `resources/emj_analysis_*.txt`.

### Phase 2 — Policy layer — `Core/Policy/`

Owner: codex (worktree). Builds against `Core/Policy/Contracts.cs` (committed contract). Pure C#,
no Dalamud, every sub-policy constructor-injected and unit-tested.

- `HeuristicDiscardPolicy` — wraps `HandAnalyzer`/`Shanten` (ours: ukeire×value, ukeire2 for
  finalists) and adds per-candidate `DealInRisk` from the opponent model. Never re-implements shanten.
- `OpponentModel` — per seat: tenpai probability (riichi → 1.0; else from discard count, melds,
  early-honor vs late-simple discards), danger per tile (genbutsu 0, suji discount, kabe, honors
  seen count, terminal vs middle), expected deal-in cost.
- `PushFoldPolicy` — push when shanten ≤ 1 and value justifies, fold to safest candidate when an
  opponent is tenpai and we're ≥ 2-shanten or the hand is cheap.
- `CallPolicy` — accept pon/chi only if it (a) lowers shanten and (b) keeps a yaku reachable
  (yakuhai, tanyao, honitsu…) — the "open yakuless" trap our analyzer already flags. Kan only closed
  or when already open with dora upside. Doman `MinHan` gate on tsumo/ron.
- `RiichiPolicy` — tenpai + closed + ukeire ≥ threshold + wall ≥ 4 (already in `HandAnalyzer`),
  plus "don't riichi into a riichi with a bad wait".
- `DecisionPolicy : IPolicy` — orchestrates in the order: tsumo/ron → call prompt → push/fold →
  discard → riichi; every step appends a `Reason`.
- Tunables in one `PolicyWeights` record with defaults; JSON-loadable later.

### Phase 3 — Wiring + UI

Owner: main session after merging 1 and 2. `AnalysisService` runs `IPolicy.Choose` off-thread over
snapshots; `MainWindow` renders `ActionChoice` (best action, top-3 candidates with shanten/ukeire/
risk, reasoning steps, per-seat danger strip). `EmjOperator` gains `Execute(ActionChoice)` (no
auto-loop yet — that's a later, opt-in feature).

### Phase 4 — Hardening

Golden hands for the policy, differential shanten (keep), snapshot replay tests, GitHub Actions
build+test, layout self-check surfaced in the UI ("layout OK / shifted / unknown").

## Branching

`rework` is the integration branch (checkpoint of the uncommitted July work). Sub-work happens in
worktrees branched from it: `rework-reader`, `rework-policy`. Phase 0 writes docs/resources only and
commits to `rework` directly. Merge order: reader → policy → wiring.

## Layout JSON schema (`resources/layouts/emj.json`)

```json
{
  "name": "Emj", "addonName": "Emj", "tileIconBase": 76041,
  "offsets": {
    "handArray": "0x0DB8", "handSlots": 14,
    "scores": ["0x0500", "0x07E0", "0x0AC0", "0x0DA0"],
    "discardCounts": ["0x04FE", "0x07DE", "0x0ABE", "0x0D9E"],
    "discardArrays": [null, null, null, null], "discardArrayMaxLen": 24,
    "doraIndicator": "0x0FD8", "uraDoraIndicator": null,
    "melds": null, "riichiFlags": null, "roundWind": null, "seatWind": null, "dealerSeat": null,
    "honba": null, "riichiSticks": null, "wallRemaining": null
  },
  "atkValues": { "stateCode": 0, "wallCount": 1 },
  "stateCodes": { "ourTurn": 6, "othersTurn": 15, "callPrompt": 19, "deal": 21, "score": 29, "win": 32 },
  "nodes": { "handSlotFirst": 134, "handSlotDraw": 135, "handSlotButton": 9, "callList": "104/3", "recapNext": 97 }
}
```

Every offset is a string hex or `null`. Reader code must never hard-code a number that belongs here.
