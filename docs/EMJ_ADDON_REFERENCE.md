# Emj Addon Reference

Reverse-engineering notes for FFXIV's Doman Mahjong addon (`"Emj"`), compiled from recording and
live sessions between 2026-06 and 2026-09-19 (EU client, Dalamud 15.0.3.5, FFXIVClientStructs
7.56.2). This is the ground truth `Core/State/` (reader), `Core/Operate/` (actuator) and
`resources/layouts/emj.json` are built against. Update it whenever a live session overturns or
refines something — it is a living document, not a snapshot. Struct offsets live in
[`EMJ_STRUCT.md`](EMJ_STRUCT.md); this file covers nodes, `AtkValues` events, lifecycles and how
to operate the addon.

## Contents

1. [Reading model — what comes from where](#reading-model)
2. [Node layout](#node-layout)
3. [Tile identity — icon IDs](#tile-identity--icon-ids)
4. [AtkValues and the event model](#atkvalues-and-the-event-model)
5. [Call window lifecycle](#call-window-lifecycle)
6. [Round lifecycle and cold start](#round-lifecycle-and-cold-start)
7. [Operating the addon (auto play)](#operating-the-addon-auto-play)
8. [Superseded methods (history)](#superseded-methods-history)
9. [Known issues / open items](#known-issues--open-items)

Tooling: Cartographer (`C:\Users\capta\Desktop\dldm_reverse`, `/carto debug`, port 9790) answers
almost every question here live — `/tree?name=Emj&node=<id|path>&text=1` for nodes,
`/values?name=Emj` for the live `AtkValues`, `/events` for the refresh timeline,
`/mem/watch?addon=Emj` + `/mem/changes` for struct fields, `/click`, `/listclick`, `/callback` to
operate. Its path syntax is `1/46/104/3` (NodeIds root → … → node), the same the layout JSON uses.

---

## Reading model

| Datum | Source | Code |
|---|---|---|
| Local hand (13 closed + draw/claim slot) | Struct `+0x0DB8`, `int32[14]` icon ids | `EmjStructReader` → `DecodedStruct.ClosedTiles` / `DrawnTile` |
| Per-seat closed count, meld count, discard count, riichi discard index, score, point difference | Struct seat panels (`0x2E0` stride) | `DecodedStruct.Seats[i]` |
| Own/opponent meld tiles (pon/kan tile, chi = 255 marker) | Struct meld records + type-13 events (chi tiles, red fives) + hand-delta inference | `EventTracker` (`TryAddMeld`, `MeldInference`) |
| Discard tiles per seat | Type-8 events (`[1]` seat, `[2]` icon) | `EventTracker.SeatDiscardsOf` |
| Dora indicator(s) | Struct `+0x0FD8` (+ count `+0x0FDC`); type-19 `[16..21]` for kan doras | `SnapshotBuilder` merges both |
| Wall remaining | Type-5 `[1]` (else `70 − Σ discard counts`) | `EventTracker.EventWallRemaining` |
| Call window (open/close, options, offered tile, source seat, chi shapes) | Type-19/23/25 events; close on 5/8/13/74/29/32 | `EventTracker` |
| Seat winds / dealer | Text nodes `nodes.seatWindTexts` ("East" = dealer), every 30 ticks | `EmjStateReader.ScanWinds` |
| Round wind | Hidden text `1/46/54/57` (win-screen residue, leading word) or type-32 `[2]` | `EventTracker.HintRoundWind` |
| Winner / tenpai ground truth at hand end | Type-32 `[1]` winner seat; result banner texts `nodes.resultBanners` ("Tenpai!"/"Noten…") | `EmjStateReader.RecordTenpaiGroundTruth` |
| Session W/L | Type-29 `[1]` seat-0 delta | `EventTracker.WinsThisSession` |
| Honba, riichi sticks, ura dora | **unsourced** (candidates: texts `1/46/48/2`, `1/46/54/88`, `/91`) | snapshot reports 0 |

The reader ticks once per framework update: struct read → `EventTracker.OnTick` (struct-driven
consistency: meld-count trim, round boundary, hand-delta melds, label fallback) → winds → snapshot.
`StateSnapshot.Sequence` bumps only when the content changed; `AnalysisService` fingerprints the
snapshot and re-runs the policy only for a new, 3-tick-stable fingerprint.

---

## Node layout

Root addon name `"Emj"` (`IGameGui.GetAddonByName("Emj")`). Paths below are NodeIds from the root.

### Hand tile slots

- Type `1055` component nodes, one per closed-hand slot, in ascending screen-X order.
- **NodeIds**: first slot `134`; slots 1–12 `1340001`–`1340012`; the far-right draw/claim slot
  is **`135`** (`nodes.handSlotDraw`).
- Inside each slot, **NodeId `9`** (type `1010`) carries the addon-bound chain: `MouseOver` param =
  slot index, **`ButtonClick` param = slot index + 15** — that click is the discard. NodeId `6`
  (collision node under 9) only has component-internal events; clicking it does nothing.
- **Pooled twins** `1340013+` park at the same X as a live slot, wear a stale face and have no
  addon-bound chain. `EmjScanner.ScanHandSlots` dedupes same-X collisions to the lowest NodeId and
  drops nodes parked at X≈0 (except real slot 0).
- **Post-meld mapping (live 2026-09-19)**: with 10 closed tiles the visible slots are `134`,
  `1340001..1340009`, then the *parked* `1340010..12` (still `IsVisible()`, no addon-bound button),
  all sorted by X **before** the draw slot `135`. So: the draw always maps to `135`, closed tile *i*
  to the *i*-th non-`135` slot — never "hand index == visual index".
  `EmjStateReader.FindSlotNodeForTile` implements this (exact tile first, then same kind, the
  draw wins ties).
- **Visibility ≠ interactivity**: the outer slot can be visible while its inner NodeId-9 button is
  not; a click on an invisible chain is accepted without error and ignored. Every operate path
  therefore requires a *visible, addon-bound* `ButtonClick` (`EmjOperator.HasAddonBoundActivation`).
  During a claim freeze the slots expose only component-internal events.
- After a riichi selection the game lists the tenpai-breaking closed slots in a type-6 event
  (`[1]` = count, `[2..]` = slot indices); those slots lose their addon-bound activation until the
  discard. While in riichi only the draw slot is clickable.

### Call window panel

- **`1/46/104`** — panel root; **`104/3`** (type `1030`, `AtkComponentList`) is the decision list
  (`nodes.callList`). Row components (type `1029`) under it have **unstable NodeIds** (`2`,
  `21001`, …) — only the list container is stable.
- Rows are selected **only** through the list's own `ListItemClick` (registered on `104/3`,
  listener = addon) with populated `AtkEventData.ListItemData` (`SelectedIndex` +
  renderer pointer). Mouse simulation on a row, even `ButtonClick`, is ignored
  (`EmjOperator.SelectListItem`).
- `AtkComponentList.ItemRendererList[i].Label` is **always empty** here; the row text is the
  renderer's text child (node `4`). `EmjOperator.ListRows` reads the item table for the index and
  the renderer text for the label. Never rank rows by screen Y — pooled renderers report visible.
- The panel, list, rows and texts are **identical open vs closed** (trees diffed live). Texts
  persist after every prompt ("Pon"/"Pass", "Tsumo"/"Riichi" across the next deal), so they can
  only ever be a fallback *open* edge, never a close signal.
- `104/2` ("Time remaining:") is hidden against NPCs; no auto-pass timer was observed there (a
  Pon offer sat open four minutes). Behaviour against real players is not yet characterised.

### Chi-shape chooser (state 25)

After "Chi" when more than one sequence fits: panel **`1/46/52`**, option buttons `52/5`, `52/6`,
`52/7`, `52/8` (`ButtonClick` params 9–12, left→right = `AtkValues` order), `52/11` (param 8) =
Cancel (`nodes.chiShapeButtons` / `chiShapeCancel`). The list `104/3` is hidden meanwhile. A single
fitting shape skips the chooser. No auto-pick timer (it waited ~2 min).

### Recap and results

- Round recap "Next": plain addon-level button **NodeId `97`** (`ButtonClick` param 7, listener =
  addon) — `nodes.recapNext`.
- End of match: after the last win screen the results panel has no `Next` (`97` hidden); only
  **"End match"** remains (state code 27, empty hand, final scores). Whether it raises a
  `SelectYesno` is handled but not yet observed.

### Text nodes the reader uses

| Purpose | Path(s) | Layout key |
|---|---|---|
| Seat winds ("East"/"South"/…; dealer = "East") | `1/36/37/38/7/9` (us), `1/36/39/40/8/10`, `1/36/41/42/8/10`, `1/36/43/44/8/10` | `seatWindTexts` |
| Scores (redundant with the struct) | `1/36/{37/38/10/12, 39/40/11/13, 41/42/11/13, 43/44/11/13}/2` | `scoreTexts` |
| Round wind (hidden, win-screen residue: "South 4 South Wind") | `1/46/54/57` | `roundWindText` |
| Honba announcement ("Honba N", residue afterwards) | `1/46/48/2` | `honbaText` (unused) |
| Result banners ("Tenpai!"/"Noten…" per seat on a draw) | `1/36/{37/38, 39/40, 41/42, 43/44}/2/2/3` | `resultBanners` |
| Wall counter digits (two `Counter` nodes) | `1/46/105/2/2`, `1/46/105/3/2` | `wallCounterDigits` (unused) |

### Discard piles (reference only)

Container components per seat: `116` (us), `119`, `122`, `125` (relative West/North/East).
Pile faces are nested one or more levels below the container. The plugin no longer scans them
(type-8 events + struct counts are exact); the ids are kept for future RE.

### Other addon-level buttons

NodeIds `6`–`11` at the bottom-right (`ButtonClick` params 0–6) are unmapped controls.

---

## Tile identity — icon IDs

Icon ids `76041`–`76074` are the 34 kinds (`tileIconBase` + 34-index):

| Range | Kind |
|---|---|
| 76041–76049 | 1m–9m |
| 76050–76058 | 1p–9p |
| 76059–76067 | 1s–9s |
| 76068–76071 | East / South / West / North |
| 76072–76074 | White / Green / Red dragon |

Red fives are `76075`–`76077` (5m/5p/5s; 34-index 34/35/36) — same *kind* as the plain five
(`TileHelpers.SameKind`, `Tile.IsRedFive`). Decoders: `EmjLayout.TryDecodeTile` (layout base) and
`TileHelpers.TryTileFromIconId` (raw `AtkValues`). Meld records in the struct use the bare 34-index.
The overlay's tile chips load the same icon ids through Dalamud's texture provider (`TileArt`).
A patch that shifts the base is absorbed by `EmjLayout.ResolveBase` (±8, needs ≥5 populated slots).
Full per-tile table: [`tile_table.md`](tile_table.md).

`76041` also serves the game as a **placeholder** (opponents' hidden draws in type-5/20 `[3]`, the
fourth int of every chi-chooser option, the atkType-74 payload) — never read a lone 76041 as 1m
outside the hand array.

---

## AtkValues and the event model

`AtkUnitBase.AtkValues[0].Int` is the **state code** of the last refresh. Codes seen
(`stateCodes` in the layout): `2` deal, `5` draw / turn advance, `6` our turn (also the riichi
tenpai-breaker list), `8` discard, `9`/`10`/`22` transient refreshes, `12` riichi declared, `13`
meld, `15` others' turns / idle, `19` call window, `20` discard confirm, `21` post-meld / deal
refresh, `23` call options, `25` chi-shape chooser, `27` post-win transition / final results, `29`
score delta, `32` win screen. `[1]` on most stable-state refreshes is the wall count baseline (not
used — it flips at transitions).

Events reach the reader through `IAddonLifecycle` `PostRefresh` (frames with ≥ 22 values are
copied into an `AtkFrame`) and `PostReceiveEvent` (only atkType 74 is used). What the tracker does
with each:

| Type | Meaning | Fields used | Tracker action |
|---|---|---|---|
| **5** | Turn advance — a seat is about to draw (not a discard). | `[1]` wall remaining (1–70), `[2]` seat, `[3]` icon (real for us, placeholder for others) | wall; **clears the call window**; drops a `WinDeclared` hold |
| **6** | Own-draw notification / after a riichi answer the tenpai-breaking slot list. Unreliable as a tile source. | `[1]` count, `[2..]` slot indices (riichi) | ignored |
| **7** | Fires after a riichi row is selected. | — | ignored |
| **8** | **The discard event**, every seat, real tile. | `[1]` seat, `[2]` icon | books the discard; remembers the last opponent discard (+time, seat); `roundEnded=false`; clears the window; drops `WinDeclared` |
| **13** | Meld composition, all seats. | `[1]` caller, `[3]` 4 pon / 5 chi, `[5]` from-direction, `[6]` tile index or 255, `[7]` tile count, `[8..10]` icons (chi: claimed first) | books the meld (4 tiles: ankan if from=0 else daiminkan; chi if `[3]=5` or first two differ; else pon), dedupes by signature, caps at 4; clears the window |
| **19** | Call window **or** another seat's "Pon!"/"Chi!" banner (same event). | `[4]` called icon (stale afterwards), `[6..8]` row labels ("Pass" = unavailable), `[16..21]` doras (Layout 1) / `[16]` only when `[14]>0` | `OpenCallWindow` (see lifecycle); caches doras |
| **21** | New deal (Layout 1: `[2]=0`, `[14]=0`) or a post-meld/mid-round refresh. | `[2]`, `[14]` | `ResetRound` **only** when `roundEnded` was true; otherwise ignored |
| **23** | Call options (mirrors 19's `[6..8]`); the self-declare prompt (`"Riichi!","Riichi","Pass"`, `"Tsumo!","Tsumo","Riichi"`) arrives as 23 right after our type-5. | as 19 | as 19 |
| **25** | Chi-shape chooser. | `[2]="Chi"`, `[3]` count, then 4 ints per option from `[4]` (three icons + placeholder) | opens a claim window with `CallShapes`; claimed tile = current/answered window's tile, else fresh opponent discard, else the tile common to all shapes |
| **29** | Post-round score delta. | `[1]` seat-0 delta ×100 | `roundEnded=true`; W/L counter; clears window and `WinDeclared` |
| **32** | Win screen. | `[1]` winner seat, `[2]` "East 3 South Wind" | `roundEnded=true`; `LastWinnerSeat`; round wind; clears window and `WinDeclared` |
| **30** | Tooltip/hover refresh (`[1]` = hovered tile name). | — | ignored |
| `atkType=74` (ReceiveEvent) | Our meld accepted. Fires repeatedly; payload `[8..11]` is **unsafe** (a chi's `[8]` was the 76041 placeholder → read as 1m → bogus kan, live 2026-09-19). | ≥ 3 decodable icons in `[8..11]` | **only closes the window**; melds come from type-13 / hand delta |

Also on every stable-state event (not 5/6/8): `[2]` may carry our seat-wind icon (76068–71).

Historical `AtkValues` hand slices (count=50 Layout 2 `[19..]`, count=109 `[24..36]` on the deal
frame) are no longer read — the struct hand replaced them (see history).

---

## Call window lifecycle

**Open** — a type-19/23 with any of Chi/Pon/Kan/Ron/Riichi/Tsumo in `[6..8]` ("!" banner
duplicates deduped by `TrimEnd('!')`), or a type-25 chooser. Fallback: the panel texts changing to
such a label set while no window is active (`OnTick` label edge) — only accepted when a fresh
(< 8 s) opponent discard names the tile, or, for a Riichi/Tsumo/Kan-only set, when we hold the
draw (`14 − 3·melds` closed tiles), because those texts persist across deals. Windows opened this
way are flagged `CallWindowFromLabels`; the auto player never answers them.

**Claim vs self-declare** — Chi/Pon/Ron ⇒ claim on an opponent's discard; Riichi/Tsumo ⇒ own
draw; a Kan-only set is a claim only when we hold three of the offered tile. The offered tile is
the fresh opponent discard, else type-19 `[4]`, else whatever the struct parked in slot 13 when the
hand has claim shape (`MaxClosedTiles(melds) − 1`).

**Legality gate** — a claim window whose tile admits no pon/kan/chi (kamicha only)/ron
(`HandTracking.InferClaims`) is another seat's banner or a stale panel and is dropped. Fails open
when the hand is not in claim shape (mid-transition).

**...unless the game corroborates it.** A type-23 whose `[1]` row count equals its option codes
plus Pass describes a **row list the game is showing us**; an announcement of another seat's call
has none (145/145 type-23 frames on 2026-09-22). That outranks our hand read: such a window opens
even when our closed tiles cannot explain it, and the disagreement is recorded instead. Only the
uncorroborated sources — a bare type-19 and the label edge — are still gated by the hand read,
because for them it is the only evidence there is. Dropping a corroborated window over a drifted
read is how a Ron on our own declared wait was passed
(docs/research/WIN_OFFERS_2026_09_22.md).

**Claims as an oracle** — which of Chi/Pon/Kan a tile allows is pure arithmetic over the closed
hand: no yaku, no furiten, no rule option. `SnapshotBuilder` therefore compares the game's offer
against `HandTracking.InferClaims` on every confirmed claim window and reports any difference as a
read-health note (the game offering what we cannot derive = tiles missing from our read; the
reverse = tiles we hold that are not there). Ron is checked in one direction only, since the game
also requires a yaku and a furiten-free wait.

**Snapshot** — `Phase = CallPrompt` / `SelfDeclare`, `Legal` = the offered options + Pass (+ Discard
for a self-declare with the draw in hand), `CallTile`/`CallFromSeat`, `CallShapes` for the chooser.
Slot 13 is excluded from the hand during a claim (the claimed tile is parked there, already part of
the meld once accepted).

**Close** — only events: type-5 (play moved on), type-8 (someone discarded), type-13 / atkType-74
(meld booked), 29/32 (round end), a new deal. Never the panel texts (they never change) and never
a timer. Exception: a label-edge window closes when the labels vanish.

**Answer** — every accepted row (Pon/Chi/Riichi/Tsumo/Ron/Pass) is echoed by the game as a
type-19 with the *same* labels. `EventTracker.MarkCallAnswered` (called by the actuator) clears
the window, remembers the label signature so that echo is ignored, keeps the claimed tile/seat for a
following chooser, and for Tsumo/Ron sets `WinDeclared`, which parks the snapshot at `RoundEnd`
with nothing legal until type-29/32 — or until a type-5/8 proves the click never landed.

**Riichi** — row 0 → type-7, then type-6 with the tenpai-breaking slot list; the game waits for
the discard; the struct riichi index (`+0x2C7`) flips on it. Nothing else marks the selection.

**Chi** — "Chi" row → either type-13 directly (one shape) or state 25 (chooser) → button → type-13.

---

## Round lifecycle and cold start

- **Round reset** (`EventTracker.ResetRound`: discards, melds, riichi, wall, window): a type-21
  Layout-1 deal while `roundEnded` is true; the struct's discard counts all returning to 0 after
  being > 0; or a "deal shape" (13 closed tiles with ≥ 8 tiles replaced). `roundEnded` is set by
  type-29/32 and at construction, cleared by the first type-8.
- **Struct is the authority for counts**: tracked melds a seat no longer has are trimmed each tick
  (a chi we never saw the type-13 for can only be *missing*, never extra); 13–14 closed tiles proves
  zero melds. A missing own meld is reconstructed from the closed-hand delta
  (`MeldInference.Infer`: drop ≥ 2 tiles + the claimed tile).
- **Hot reload / crash mid-hand is safe**: the tracker cold-starts from the struct; only
  event-only data before the reload (opponent discard *tiles*, chi compositions) is missing, and
  the snapshot's `Notes` say so (`seat N discards a/b tracked`, `chi composition unknown`).
- **Hand end**: the phase turns `RoundEnd` on codes 27/29/32 (or a `WinDeclared` hold). The last
  in-play snapshot is frozen and, once the winner (type-32) or all three opponent banners
  (Tenpai/Noten, up to ~5 s) are known, one `TenpaiSample` per opponent goes to the calibration
  CSV.
- **Unhealthy struct read** (a slot fails to decode and no base within ±8 fits): the last healthy
  hand is kept with a note; the overlay shows "Layout check failed".

---

## Operating the addon (auto play)

Dev tooling added 2026-09-19 to run unattended matches and dump stalls (`Core/Operate/`, the
"AUTO PLAY" card in the main window; no separate command). Remove the card and the `Plugin`
wiring to retire it.

**Firing strategy (`EmjOperator`)** — reuse the events *registered* on the target node (correct
listener, param, target) and deliver exactly one semantic activation per click:
`ButtonClick` > `ListItemClick` > `MouseClick` > `MouseDown`+`MouseUp`; search the subtree per
event type, preferring an addon-bound **visible** chain (the only one the game reacts to). Chain
params are authoritative (a slot's `ButtonClick` param is slot+15, not the slot index). A list row
is committed through the list's addon-bound `ListItemClick` with a **zeroed, list-shaped**
`ListItemData` (index and renderer from the item table) — `AtkEventData` is a union whose
`MouseData.PosX/PosY` overlap `ListItemData.ListItemRenderer` at offset 0, so a mouse payload
would leave screen coordinates in a pointer field. `FireCallback(int…)` covers handlers wired at
the callback layer.

Since 2026-09-22 every entry point is guarded and returns a `Dispatch` that says *what was sent*
or *which guard refused* — there is no synthetic click, no row-0 fallback and no dispatch at a
control whose ancestor chain is hidden. Diagnostic scalars are copied before `ReceiveEvent`,
because a handler may rebuild the tree. No `MouseOver` precedes a click by default; the hover
style is opt-in and needs a matched `MouseOver`/`MouseOut` pair on one holder. See
[the rework record](research/ADDON_INTERACTION_2026_09_22.md).

**Actuation (`EmjActuator.Execute`)**:

| Decision | Click |
|---|---|
| Discard / riichi discard | slot from `FindSlotNodeForTile`, only if it has a visible addon-bound `ButtonClick` and a visible ancestor chain; otherwise the next candidate (`RECOVERY:` in the journal) |
| Pon / Chi / Kan / Ron / Tsumo / Riichi | `AnswerCall` → `CallRowResolver` over the live item table of `104/3` → `SelectRow` |
| Chi with chooser | the `52/5..8` button whose three tiles equal the policy's meld |
| Pass | the "Pass" row of `104/3`, or `52/11` in the chooser |
| Recap | `SelectYesno` Yes if one is up, else node 97, else the "End match" **button** (a list row is refused here) |

A call answer requires an **event-confirmed** window (a label-only window is panel residue),
a call list whose whole ancestor chain is visible, rows that still carry the open window's
options, and exactly one matching row with a renderer. It then calls
`MarkCallAnswered(isWin, generation)` with the window generation captured *before* dispatch, so
a prompt the handler opens synchronously (the type-25 chi chooser) is not cleared by the answer
to the window it replaced. Verified live 2026-09-19 (two matches, one
full hanchan finished 1st): pon accept + post-call discard, riichi → discard → Ron, tsumo (chiitoi),
open-tanyao Ron, chooser pick, Pass on a 3-row list (`Kan`,`Pon`,`Pass` → row 2). Kan was never
executed; the chooser's Cancel is untested.

**Driver (`AutoPlayer.Tick`)** — executes a decision once per analysis fingerprint that matches
the live state and is actionable (Discard/Riichi need `Legal.Discard`; Pass needs a prompt or the
chooser; `None` never). A dispatch is remembered until the snapshot sequence moves: that, and only
that, counts as the game accepting it, and Diagnostics reports the two separately. Retries after
6 s (max 3). Clicks through recaps every 4 s. **Stall** = no
`Sequence` change for 15 s on `OurTurn`/`CallPrompt`/`SelfDeclare`, 45 s otherwise → appends a dump
to `pluginConfigs/MahjongHater/autoplay_stalls.log` (snapshot, decision + reasoning steps, prompt
rows, per-slot discardability, journal, the tracker's last 120 notes), then a recovery ladder:
Pass through the same guarded call path (+5 s), discard the draw or any clickable slot **only
while a discard is legal** (+15 s), recap buttons (+30 s), repeated every 30 s. All of it is journaled and mirrored to the Dalamud log as `[AutoPlay]`.

**Requeue (`MatchQueuer.Tick`)** — Doman Mahjong is a Duty Finder duty (Gold Saucer tab).
Solo ContentFinderCondition rows: **643** Novice Full Ranked, **766** Novice Quick Ranked, **644**
Advanced Full, **767** Advanced Quick (Advanced needs 1st dan); 645/650/768/769 are the four-player
custom rooms. With the Emj addon closed for 8 s and `ContentsFinder.Instance()->QueueInfo.QueueState
== None`, it calls `ContentsFinderQueueInfo.QueueDuties(&id, 1)`; if the state has not moved in
8 s it opens the Duty Finder on the duty (`AgentContentsFinder.OpenRegularDuty`) and clicks the
typed `AddonContentsFinder.JoinButton`. On `Ready` it clicks `AddonContentsFinderConfirm.
CommenceButton` (callback `8` fallback). State transitions log as `[Queue] state A → B`
(`None → Pending → Queued → Ready → Accepted → InContent`). The first live run of this path is
still pending; check those lines.

---

## Superseded methods (history)

Everything below was how the plugin read the table before the struct map (2026-09-18) and is
kept only so old logs, captures and ideas can be understood. Code: `GameStateReader.cs`,
`TileFaceMap.cs` last at `8f4ddac^`; the debug HTTP API, `EmjOperator` (original), `NodeDump`,
`EmjStateReader.Debug` and `tools/play_loop.py` last at `3847a50^`.

- **Hover** (`PostReceiveEvent` atkType 6 / type-30, `[1]` = English tile name "Dots (7)"): exact
  but only fires when the mouse moves over a slot; drove the early overlay, hence the "hover every
  tile" complaint.
- **Node face decode**: each slot's 40×52 face image `PartsList` → `UldAsset` → texture; the
  `IconId` field is stale on pooled nodes (written only on texture load), so the id had to be
  parsed from the resource path (`ui/icon/076000/076050_hr1.tex`); non-icon render modes needed a
  learned composite key (`TileFaceMap`, `resources/tile_face_map.json`).
- **AtkValues hand slices**: count=50 Layout 2 (`[14]>0`): `[18]` = closed count, `[19..]` = tiles
  (a type-30 refresh can fake the shape — the read had to match `[18]` exactly); count=109: `[24..36]`
  sorted hand and `[37]` the draw, only on the type-21 deal frame.
- **Own-draw fields**: type-5 `[3]` when `[2]==0` (reliable) and type-6 `[2]` (misses draws, once
  stale).
- **Pile scanning** (`ScanPileFaces`, `MergeDiscardPiles`): duplicate renders of a just-discarded
  tile (up to 6 images) had to be clamped at 4 per kind.
- **Old reader cold-start chain** (2026-07-06): the first type-21 misread as a deal, prompt-edge
  rollback from an unconverged bootstrap, a one-shot bootstrap that gave up on one undecodable
  ghost slot, and a phantom atkType-74 meld that broke every hand-size check. All moot now that
  counts come from the struct.
- **Type-15**: fires ~20 ms after a window opens and after every turn cycle; it was once misread as
  "own discard turn" and must never clear a window.

---

## Known issues / open items

- **Honba, riichi sticks, ura dora** are not sourced (snapshot reports 0; scoring value ignores
  them). Candidates: texts `1/46/48/2`, `1/46/54/88`, `/91`; the struct has no field for them.
- **Riichi stall (under investigation, 2026-09-19)**: a suspected uncaught stall on a specific
  riichi against real players. The auto-play stall dump exists to catch it; policy branches that
  yield a `None` decision in riichi (`riichi locked` with no draw in hand, `hand out of sync`) are
  the first suspects.
- **Kan** has never been executed live (one minkan offer was correctly declined); the struct's
  meld record for kans and `+0x0FDC` after a kan dora are unverified. Chooser **Cancel** untested.
- **Label-edge phantom**: the fallback still flickers a ~40 ms window on some opponent discards
  (harmless: the auto player ignores label-only windows; the overlay may blink).
- **Real-player timers**: no auto-pass/turn timer has been characterised outside NPC matches.
- **Addon freeze** (observed 2026-07-05): the addon can stop updating while staying readable;
  clicks do nothing and no events arrive. Only detectable as a long stall (the auto player's
  45 s watch will dump it).
- **Akadora inside melds** are not representable in the struct meld record (type-13 icons carry
  them; the hand-delta fallback loses them).
- Bottom-right buttons `6`–`11` unmapped; seat-panel node pointer roles (`+0x050..`, `+0x258..`)
  unresolved; `+0x2D8` "tsumogiri" flag, `0x0FF8`, `0x12BC/0x12C0` unknown.
- Settings **game length, double-wind pair fu, dora display** are stored but not consumed by the
  analysis; `ScoringEngine`/`FuCalculator` are not on the live path (value = yaku han + dora).
