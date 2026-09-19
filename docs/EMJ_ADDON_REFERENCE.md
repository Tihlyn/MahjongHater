# Emj Addon Reference

Reverse-engineering notes for FFXIV's Doman Mahjong ("Emj") addon, compiled from
recording sessions and live debug-API sessions (2026-07-04 through 2026-07-06).
This is the ground truth the plugin's `Core/GameStateReader.cs`, `Core/EmjScanner.cs`,
and `Core/EmjOperator.cs` are built against. Update this file whenever a live session
overturns or refines something here — treat it as a living document, not a snapshot.

## Contents

1. [Node layout](#node-layout)
2. [Tile identity — icon IDs](#tile-identity--icon-ids)
3. [Tile reading methods](#tile-reading-methods)
4. [AtkValues layouts (count=50 vs count=109)](#atkvalues-layouts)
5. [Event model](#event-model)
6. [Call window lifecycle](#call-window-lifecycle)
7. [Discard pile reading](#discard-pile-reading)
8. [Cold-start reader lifecycle](#cold-start-reader-lifecycle)
9. [Debug / Operate HTTP API](#debug--operate-http-api)
10. [Known issues / open items](#known-issues--open-items)

---

## Node layout

Root addon name: `"Emj"` (via `IGameGui.GetAddonByName("Emj")`).

### Hand tile slots

- Type `1055` component nodes, one per closed-hand tile slot, in ascending screen-X
  order left→right.
- **NodeIds**: first slot is `134`; slots 1-12 are `1340001`-`1340012`; the far-right
  (14th / drawn-tile / ghost) slot is `135`.
- Inside each `1055` slot component:
  - **NodeId `9`** (type `1010`) — the addon-bound interaction chain:
    - `MouseOver` param = **slot index** (0-based), listener = the addon.
    - `ButtonClick` param = **slot index + 15**, listener = the addon. This is the
      actual discard action.
  - **NodeId `6`** (type `8`, nested under `9`) — a component-internal collision node
    carrying `MouseOver`/`MouseOut`/`MouseDown`/`MouseUp`/`MouseClick`/`InputReceived`
    with params `256-260`/`512`. These events exist but their listener is the
    **component itself**, not the addon — clicking here is silently ignored by the
    game. Always target NodeId `9`, never `6`.
  - A 42×55 backdrop image (constant across all tiles) plus a 40×52 face image whose
    `PartsList`/texture identifies the tile (see [Tile identity](#tile-identity--icon-ids)).

**Pooled twin slots**: extra `1055` nodes beyond the real 14 (`1340013`, `1340014`,
`1340015`, `1340016`, …) park at the exact same screen-X as a live slot — almost
always the far-right one. They wear a **stale face** and their inner NodeId-`9`
carries no addon-bound chain (dead component-internal events only). `EmjScanner.
ScanHandSlots` dedupes same-X collisions by picking the **lowest NodeId** (the real
slot IDs above are always lower than the pooled `1340013+` range). `EmjOperator.
HasAddonBoundActivation` is the run-time backstop: it requires a **visible,
addon-bound** `ButtonClick` chain before `/discard` will act, so even a scan miss
degrades to a clear refusal instead of a silent no-op click into a dead node.

**Node visibility ≠ node existence**: the outer slot node (e.g. `135`) can report
`IsVisible()==true` (so it still shows up in a face scan) while its inner NodeId-`9`
button is `IsVisible()==false` — clicking a chain event on an invisible node is
accepted by the game engine with no error, but silently does nothing (ATK gates
interactivity on visibility). Every operate path in `EmjOperator` now requires
`requireVisible: true` on its first (and preferred) search tier for exactly this
reason. A slot's button going invisible generally means "not interactable this
instant" — could be mid-animation, could mean it isn't actually your turn yet, or the
addon itself has frozen (see [Known issues](#known-issues--open-items)).

### Discard piles

Four container components, one per seat, found via `EmjScanner.ScanPileFaces`:

| NodeId | Seat |
|--------|------|
| 116 | Player (you) |
| 119 | West (relative) |
| 122 | North (relative) |
| 125 | East (relative) |

Pile slot faces are **not** direct children of the container — they're nested one or
more levels through sub-components / `Res` (type `1`) containers, hence the recursive
scan (`CollectFaceImages`, depth ≤ 4). Discard-pile scanning has **no visibility
filter** on inner images (unlike hand slots) since pile tiles remain visible once laid
down.

A just-discarded tile can render as **up to 6 duplicate images** in the pile scan
(claim-highlight / center-display copies) — `MergeDiscardPiles` clamps the merged
per-kind count at 4 (the maximum copies of any tile that can exist) to prevent this
polluting seen-tile counts.

### Call window panel

- **NodeId `104`** — the panel root (only used for reading button label text via
  `ScanCallButtonTexts`; never used for detection — see
  [Call window lifecycle](#call-window-lifecycle)).
- `104 → 3` (type `1030`, an `AtkComponentList`) — the actual list of decision rows
  (Chi / Pon / Ron / Pass / …).
- `104 → 3 → 2` (or `104 → 3 → 21001`, `21002`, … — NodeIds are **not stable** across
  panel rebuilds; only the `104 → 3` list container is stable) — individual row
  components (type `1029`), each with a nested type-`8` collision node (`id=7`).
- **Component-level mouse simulation on a row (MouseOver/Down/Up/Click, even
  `ButtonClick`) is silently ignored by the game.** The only way to actually select a
  row is the list's own **`ListItemClick`** event (registered on node `104/3` itself,
  listener = addon) fired with a populated `AtkEventData.ListItemData` (`SelectedIndex`
  + `AtkComponentListItemRenderer*`). See `EmjOperator.SelectListItem`.
- Row **index → label** is read from `AtkComponentList.ItemRendererList[i].Label` (the
  list's own item table) — this is authoritative. Do **not** rank rows by screen-Y:
  pooled/off-screen renderers report `IsVisible()==true` too and can outrank the real
  rows (observed: index 6 returned for a genuinely 2-row list when ranked by Y).

### End-of-round recap

The win/draw recap screen's "Next" button is a **plain addon-level button**
(NodeId `97`, type `1007`, `ButtonClick` param `7`, listener = addon) — ordinary
`ClickNode`/`ClickByLabel` handles it the same as any other addon-bound button.

### Other addon-level buttons (unconfirmed purpose)

NodeIds `6`-`11` at the bottom-right of the addon (`ButtonClick` params `0`-`6`) are
visible controls (settings/help/etc.) not yet mapped to specific functionality.

---

## Tile identity — icon IDs

Icon IDs `76041`-`76074` map to the 34 tile kinds:

| Range | Kind |
|-------|------|
| 76041-76049 | 1m-9m |
| 76050-76058 | 1p-9p |
| 76059-76067 | 1s-9s |
| 76068-76071 | East / South / West / North wind |
| 76072-76074 | White / Green / Red dragon (Haku / Hatsu / Chun) |

Red fives render as a distinct icon id (76075-77 for 5m/5p/5s) but are the same *kind*
as their normal counterpart (`TileHelpers.SameKind` / `Tile.IsRedFive`).

`TryTileFromIconId(int iconId, out Tile tile)` (GameStateReader.cs) is the canonical
decoder for a raw icon id found anywhere in `AtkValues`.

See **[`tile_table.md`](tile_table.md)** for the complete per-tile lookup — short
name, full in-game hover name, icon id, and texture path — for all 37 faces
(34 kinds + 3 red fives).

---

## Tile reading methods

> **Superseded for the local hand (2026-09-18).** The full 14-slot hand is a plain `int32[14]` of icon
> IDs at `AddonEmj + 0x0DB8` (slot 13 = drawn/claimed tile), verified live across deals, discards,
> draws, call prompts and win screens. Per-seat discard counts, meld count/tile indices, riichi
> index and scores are in the addon struct too. Hover tracking and node-face scanning are no longer
> needed to read the hand; see [`EMJ_STRUCT.md`](EMJ_STRUCT.md) for the offset table, what is *not*
> in the struct (discard tiles, chi tiles, winds, wall — still events/nodes), and the hex fixtures
> under `resources/fixtures/`. The methods below remain valid as cross-checks and for the operate side.

Five independent tile-identity sources exist, in descending order of trust. **None is
unconditionally correct** — every one of them has been caught wrong at least once in
live testing, which is why the plugin cross-checks rather than trusting any single
source blindly.

### 1. Type-8 discard truth (highest trust for discards)

`AtkValues[1]`=seat, `AtkValues[2]`=icon on a type-8 refresh event is the real
identity of **whatever tile that seat just discarded**, for every seat including
opponents. Confirmed correct across dozens of live discards this session. See
[Event model](#event-model).

### 2. Type-5 seat-0 own-draw field (highest trust for the just-drawn tile)

On a type-5 "turn advance" event where `AtkValues[2]` (seat) `== 0`, `AtkValues[3]`
carries the **real drawn tile icon** — not the constant placeholder it is for
opponent turn-advances. The client always knows its own tile, so it's disclosed
immediately. Live-confirmed: it matched both the rendered face **and** an
independent hover confirmation in a case where the type-6 draw event
(source 4 below) was simultaneously wrong.

### 3. Hover (`OnHoverEvent`, `PostReceiveEvent` atkType=6, "MouseOver")

`EventParam` = 0-based slot index; `AtkValues[1]` is a `ConstString` **English tile
display name** ("Dots (7)", "Characters (2)", "Red Dragon", …), parsed by
`TryParseTileName`. This is the game's own tooltip text for whatever the mouse is
currently over — as close to ground truth as anything gets, but it only fires when
something (a player, or the debug API's `/hover`) actually moves the mouse over a
slot. `hoverHandSlots` is a `slot → Tile` map, cleared on every discard/meld/deal
(so stale slot-index pairings from a shifted hand can never leak in).

### 4. Type-6 own-draw notification ("Draw (type-6)")

`AtkValues[2]` = drawn tile icon (76068-71 = a seat-wind-only notification with no
tile data, filtered out). **Two independent unreliability modes confirmed live**:
it does not fire for every local draw (a draw can happen with zero type-6 events),
and on at least one occasion it fired with a **stale/wrong icon** while the node
face and a hover confirmation agreed on a different tile. Because of this it is
downgraded to a **fallback** — only trusted when no type-5 seat-0 reading
(source 2) has already claimed this draw cycle (`lastDrawnTileAuthoritative`).

### 5. Node face decode (`EmjScanner.BuildFaceKey` / `BuildSlotFaceKey`)

Every hand/pile slot's 40×52 (or similarly-sized) face image has a `PartsList`;
`img->PartId` indexes into it, and each part's `UldAsset` points at a texture.
- **Icon-backed textures**: `GetIconId(AtkUldAsset*)` reads the icon id straight off
  the texture resource. **The `IconId` field itself can be stale** — it's only
  written when a texture is *loaded* as an icon, so a pooled node rebound to an
  already-loaded texture keeps the *previous* occupant's id. The fix: parse the id
  out of the resource's file path instead (`"ui/icon/076000/076050_hr1.tex"` →
  `76050`, via `IconIdFromTexPath`) and let that override the field — the path names
  what's actually rendered.
- **Non-icon textures** (some render modes): no icon id at all. Falls back to a
  composite key of `asset id + part id + UV rect + texture file tail`, which is
  stable per tile kind but must be **learned** (`TileFaceMap`) by pairing it with a
  trusted read (hover, or an ordered AtkValues read) the first time it's seen.

Because of the stale-`IconId` failure mode, **face decode is not automatically
trusted for the far-right (drawn) slot** — see priority ordering in
`GameStateReader.TryNodeHandRead`, which applies hover overrides first, then the
authoritative drawn-tile reading (source 2) for the last slot only, leaving all
other slots on face-decode/learned-map as normal.

### Resolution priority as implemented

```
per slot:
  1. hover pairing for this exact slot index, if present this cycle   → wins outright
  2. (far-right slot only) lastDrawnTile, IF lastDrawnTileAuthoritative → overrides face
  3. face decode (icon id, path-corrected) or learned TileFaceMap entry → otherwise
```

Discards/opponent identity separately prioritize type-8 truth over the type-5
pile-face-diff heuristic (see [Event model](#event-model)).

---

## AtkValues layouts

The addon exposes state through `AtkUnitBase.AtkValues[]`, whose meaning depends on
`AtkValuesCount` and `AtkValues[0].Int` (the "event type" for that refresh).

### count = 50 (normal play)

- **Layout 1** (`[14] == 0`, no open melds): hand tiles are **not** in `AtkValues` at
  all outside of a type-21 deal frame. `[16..21]` = up to 6 dora indicator icons,
  `[22]` = `-1` sentinel.
- **Layout 2** (`[14] > 0`, i.e. 2=Chi, 3=Pon, 5/6=Pon variants, 7=Tsumo — meaning at
  least one meld exists): `[17]` = `-1` sentinel, `[18]` = closed-tile count,
  `[19..18+count]` = closed tile icon ids in hand order. Refreshed every `Tick()`.
  **Gotcha (live 2026-07-06):** a type-30 (hover/tooltip) refresh's `AtkValues` reuses
  these same slot positions for unrelated data and can coincidentally satisfy this
  exact shape ([14]>0, [17]==-1) — observed with [14]=3, [17]=-1, [18]=8 on a pure
  hover event, where [19..21] happened to hold 3 real tile icons before hitting a
  ConstString that broke the read loop early, producing a plausible-looking but
  truncated 3-tile "hand." `ReadHandTilesFromAtkValues` now requires the read count to
  equal `[18]` exactly before trusting it — a genuine Layout-2 frame always satisfies
  this by construction, a spurious match generally won't. Do not weaken this check
  back to a bare `tiles.Count >= 1`.

### count = 109 (also seen during ordinary mid-round play, not just post-round)

- `[24..36]` = 13 sorted closed-hand tile icon ids — **but only readable on the exact
  type-21 deal frame** (`ReadHandTilesFromAtkValues` gates this on
  `AtkValues[0].Int == 21`); on other event types in this same count=109 mode the
  function returns nothing from `AtkValues`, and the node-scan path
  (`EmjScanner.ScanHandSlots` + face/hover resolution) is the only hand source.
- `[37]` = the 14th (drawn) tile during the draw phase of a deal frame.
- Historically documented as "post-round / win screen" — that's real (win screens use
  it) but **not exclusive**; ordinary mid-round ticks can also report count=109.
  `TryNodeHandRead` explicitly skips event types 29/32 (the genuine post-round
  screens) since those show the final/winning hand, not a playable one.

---

## Event model

Delivered via `IAddonLifecycle.AddonEvent.PostRefresh` on `"Emj"`
(`OnGameRefresh` in `GameStateReader.cs`), keyed on `AtkValues[0].Int`. **This model
was substantially reinterpreted during the 2026-07-05 live sessions** — treat the
table below as current, not the historical understanding.

| Type | Meaning | Key fields |
|------|---------|------------|
| **5** | **Turn advance** (a seat is about to draw) — NOT a discard, despite the historical name. `[3]` is a constant placeholder icon (`76041`) for opponents, but carries the **real drawn tile** when `[2]==0` (own draw) — see [Tile reading methods §2](#tile-reading-methods). | `[1]`=wall remaining, `[2]`=seat that draws next, `[3]`=icon (placeholder or real, see above) |
| **6** | Local draw notification. Does not fire for every draw; occasionally stale. | `[2]`=drawn tile icon (76068-71 = seat-wind-only, no tile) |
| **8** | **THE discard event** — real tile icon for **every** seat, unconditionally. | `[1]`=seat, `[2]`=real tile icon |
| **9** | Fires right after an 8 (post-discard bookkeeping); observed `[2]`=13 alongside `[1]`=seat. Not separately handled. | |
| **10** | Fires right after 9. Not separately handled. | |
| **13** | Meld-acceptance detail, alongside `atkType=74` PostReceiveEvent. | `[8]`=called tile icon |
| **15** | Fires ~20ms after a call window **opens**, and after every seat's turn cycle in general. **Does NOT mean "own discard turn"** (a historical misreading) — must never clear the call-window UI; doing so killed a freshly-activated real chi window in testing. No longer handled in the event switch. | |
| **17** | Seen alongside discard bookkeeping (`[1]`=seat, `[2]`=icon). Not separately handled. | |
| **19** | Call opportunity window (Pon/Chi/Kan/Ron) **or** an announcement of another seat's call (Pon!/Chi! banner) — same event, two flavors, disambiguated only by the [legality gate](#call-window-lifecycle). | `[4]`=called tile icon (persists after the fact — don't trust once stale), `[6]`=Chi slot, `[7]`=Pon slot, `[8]`=Ron slot (each "Pass" = unavailable); `[16..21]`=all 6 dora indicators (only in this event type; elsewhere `[19+]` are hand tiles) |
| **20** | Seen paired with discard-cycle bookkeeping. Not separately handled. | |
| **21** | New deal / hand-state refresh. Layout 1 (`[14]==0`, `[2]==0` local only): start-of-round when `roundEnded` was true → full state reset; otherwise a **mid-round refresh that must be ignored** (wiping here was a historical corruption bug — loses hand/melds/discards/wall). Layout 2 (`[14]>0`): post-meld hand update from `AtkValues[19..]`. | `[1]`=tile count in this frame (13 deal / 11 post-pon-Layout2 seen), `[2]`=seat, `[14]`=layout flag |
| **22** | Seen paired with discard-cycle bookkeeping. Not separately handled. | |
| **23** | Call-window option strings appear here too (mirrors 19's `[6-8]`). Not separately handled (options are read from the type-19 handler and the label scan, not this event). | `[6..8]`="Pass"/"Chi"/"Pon"/… |
| **24** | Seen alongside a local player's own resolved discard sequence. Not separately handled. | |
| **29** | Post-round score delta. | `[1]`=seat-0 delta ×100, `[5]`/`[6]`=yaku/fu-han strings, `[7]`=fu, `[8]`=han |
| **30** | Tooltip/hover refresh — fires alongside real hover events (`PostReceiveEvent` atkType=6), carries the same tile name text at `[1]`. Purely observational. | `[1]`=hovered tile name string |
| **32** | Win screen. | `[2]`=round wind + wind string ("East 1 West Wind"), `[4]`=winner name, `[5]`=win type ("Called Tsumo"), `[6]`=fu/han string |
| `atkType=74` (PostReceiveEvent, not a refresh type) | Meld acceptance. Fires constantly with no payload (noise) — only a firing **with payload tiles present** (`HandleMeldAccepted`) represents a real meld and resolves the call window. **It also fires REPEATEDLY with the SAME payload still present** (not just once per meld) — `HandleMeldAccepted` must dedupe by tile signature against the last accepted meld and hard-cap at 4 total melds, or `trackedCalledMelds` grows unboundedly (live 2026-07-06: drove `HandTracking.MaxClosedTiles` negative, crashing `Tick()`'s oversize-hand truncation every frame and freezing the recommendation on stale data). | |

**What clears the call-window UI flag** (`isCallWindowActive`): type-5 (a seat drew —
play continued past the window), type-8 (a discard resolved it), a **genuine**
type-21 (new deal or a real post-meld hand update), type-29/32 (round end), a real
`atkType=74` meld, or the button labels simply disappearing on the next `Tick()`.
Type-15 explicitly does **not** clear it (see table).

**type-21 must only clear it when `HandleNewDeal` actually did something.** type-21
also fires as an ordinary, no-op mid-round refresh (`HandleNewDeal` recognizes this
and returns without touching any state) — but a claim window can legitimately be
active at that exact moment (e.g. just activated by the slot-jump fingerprint one
event earlier). Live-confirmed 2026-07-06: unconditionally clearing on every type-21
regardless of `HandleNewDeal`'s outcome silently killed a freshly-activated real
window one event after activating it — and because the panel's labels were stuck
(no fresh edge to re-detect it) and the slot-jump fallback won't refire for an
already-consumed discard event, the window became **permanently invisible** for the
rest of that turn (the reported symptom: a visible Chi/Pass panel the plugin never
recognized). Fixed: `HandleNewDeal` returns `bool` (true only for a genuine
reset/update); the `case 21` switch arm only clears the call-window flag when it
returns `true`.

---

## Call window lifecycle

Riichi/Ron/Chi/Pon prompts fire **no distinguishing event of their own** for "this
window is for you" vs "someone else is calling" vs "this is a stale leftover panel" —
all three look identical at the raw-event level. Two independent mechanisms handle
this:

### 1. Edge-triggered label scanning (primary)

`NodeId=104`'s button texts are scanned every `Tick()` (`ScanPromptOptions`,
`ScanCallButtonTexts`). The **label set is level-triggered, not edge-triggered** — in
count=109 mode the game leaves the panel **visibly on-screen with stale labels
forever** after a window closes (confirmed live: alpha-127 panel, "Chi/Pass/Time
remaining:" persisting 137+ seconds into an unrelated later turn). So:

- A **changed** label signature (`string.Join(",", labels)`) marks a genuinely new
  prompt edge → `OnPromptDetected()` + `OnClaimPromptEdge()`.
- An **unchanged** signature while the game state clearly implies a fresh window
  (discard event within 5s, no local draw since, ghost slot pushes hand size to
  closed+1) is caught by a secondary fingerprint check (`HandTracking.
  IsClaimWindowSlotJump`) — this exists specifically because two consecutive windows
  can share an identical label set and produce no edge at all.
- Label **disappearance** or a resolution event (see table above) ends the window.

### 2. Legality gate (the actual discriminator — "is this window for me")

The single reliable signal that a claim window is real and local: **the game only
opens a window when a call is actually legal for your hand.** `HandTracking.
HasAnyLegalCall(closedHand, claimed, calledMeldCount)` checks pon/kan (≥2 copies),
chi (suit-neighbor run shapes, never crossing suits/honors), and ron (the claim
completes the hand via `Shanten.Calculate(...) == -1`). Wrapped as
`GameStateReader.HasAnyLegalLocalCall` (excludes a trailing ghost/drawn 14th slot so
the claimed tile is judged against the true 13-tile hand).

Any "window" that fails legality is either another seat's call (the same type-19
event fires for opponents' Pon!/Chi! banners) or a stale panel — suppressed, never
advised on. **Fails OPEN** (does not suppress) when the tracked hand's size doesn't
match either expected size for the current meld count — i.e. right after a plugin
reload before the first deal/hover repopulates it — since a false "not ours" would
permanently hide a real window from advice, which is strictly worse than a
false-positive the player can just ignore (the plugin never acts automatically).

### The offered/claimable tile's own identity is not simple either

The far-right "ghost" slot that renders during a claim window is the natural source
for the claimable tile, but its face can itself be stale (the same pooled-node issue
as the drawn slot) — a fresh, still-authoritative type-8 discard reading
(`lastDiscardKindAuthoritative`, valid ~2.5s) **overrides** a disagreeing ghost
decode. `/prompt`'s `farRightSlot` (raw ghost decode) and `claimableTile` (corrected
identity used for the actual legality check) are reported separately for exactly this
reason — they can legitimately disagree.

---

### Verified live 2026-09-19 (first policy-driven match, count=109 mode)

- **Open signal = AtkValues type 19/23** (`[6..8]` = row labels, e.g. `"Pass","Pon","Pass"`;
  `[5]` = claimed tile icon on 19). The type-8 discard that precedes it names the tile.
  **Close signal = the next type-5 (draw) / type-8 (discard) / type-13 or atkType-74 (meld) /
  29 / 32.** Nothing in the node tree changes when a prompt closes.
- The panel `104`, its list `104/3`, the two allocated rows and their texts (`"Pon"`, `"Pass"`)
  are **identical open vs closed** (both trees dumped and diffed). `AtkComponentList.ItemRendererList[i].Label`
  is **always empty**; the row text lives in the renderer's text child (node `4`).
  `EmjOperator.ListRows` falls back to it. Panel texts are therefore only a fallback *open*
  edge (needs a fresh opponent discard to name the tile) and must never *close* a window.
- **No auto-pass timer**: a Pon offer sat open for four minutes until answered. The
  `"Time remaining:"` sub-panel (`104/2`) is hidden in this mode.
- Declining/accepting works only through `ListItemClick` on `104/3` (row index; renderer text
  gives the index → `ClickByLabel` routes there). Accept fires a type-19 echo of the selection,
  then type-13 with the meld and atkType-74.
- **Post-meld slot mapping**: with 10 closed tiles the visible `1055` slots are `134`,
  `1340001..1340009`, then the parked `1340010..12` (still `IsVisible()`, no addon-bound
  button) **sorted by X before the draw slot `135`**. Map the draw to `135` and closed tile *i*
  to the *i*-th non-`135` slot — never "hand index == visual index".
- **Round wind**: hidden text `1/46/54/57` ("South 4 South Wind", leading word = round) is
  win-screen residue and beats the East default on a mid-session load; `layouts/emj.json`
  `nodes.roundWindText`. Riichi-stick / honba counters: `1/46/54/88` and `/91`.
- **End of match**: after the South 4 win screen the results panel has no `Next` (`97` hidden);
  only "End match" remains — state code 27 with an empty hand and final scores.


### Verified live 2026-09-19, second match (full hanchan driven by the policy, finished 1st)

- **Self-declare prompt** = type-23 `[6]="Riichi!" [7]="Riichi" [8]="Pass"` (or `"Tsumo!","Tsumo","Riichi"`)
  right after our type-5 draw; the list `104/3` shows the same labels. `Riichi!`/`Tsumo!` is the
  announcement banner echoing the option (dedupe on `TrimEnd('!')`).
- **Riichi selection**: `ListItemClick` row 0 → the game fires type-7, then **type-6 with
  `[1]=count, [2..]=closed-slot indices whose discard would break tenpai`** (the greyed-out
  tiles; the draw slot is never listed), and waits for the discard. Any tenpai-keeping slot
  click completes it; the struct riichi index (`+0x2C7`) flips on that discard. No further
  event marks the selection, so the operator must clear its own window after answering.
- **Answer echo**: every accepted row (Pon/Chi/Riichi/Tsumo/Ron) is echoed as a type-19 with
  the *same* `[6..8]` labels — not a new prompt.
- **Chi-shape chooser = state 25**: after "Chi" when more than one sequence fits:
  `[0]=25 [1]=6 [2]="Chi" [3]=count`, then 4 ints per option from `[4]` (three tile icons +
  a 76041 placeholder), e.g. `2s3s4s·, 3s4s5s·, 4s5s6s·`. Panel `1/46/52`: option buttons
  `52/5, 52/6, 52/7, 52/8` (`ButtonClick` params 9–12, left→right = AtkValues order),
  `52/11` (param 8) = Cancel. The list `104/3` is hidden meanwhile. A single fitting shape
  skips the chooser. No auto-pick timer observed (it waited ~2 min for a click).
- **atkType-74 payload is unsafe**: for a chi its `[8]` was the 76041 placeholder, parsed
  as 1m → a bogus `Daiminkan [1m 4s 5s 6s]` that outranked the correct type-13
  `Chi [4s 5s 6s]` (`[6]=255, [7]=3`) when the struct meld count trimmed the list. The
  tracker now books our melds only from type-13 / the hand delta; 74 only closes the window.
- The call panel keeps `"Tsumo"`/`"Riichi"` texts across the next deal: a label-edge
  self-declare must require the draw in hand.
- `Pass` on a 3-row list (`Kan`,`Pon`,`Pass`) resolves to row 2 by renderer text — worked.
- Plugin hot-reload mid-hand (Dalamud dev auto-reload) is safe: the tracker cold-starts from
  the struct; only event-only data (opponent discards before the reload) is missing.

## Discard pile reading

Two independent sources are merged (`MergeDiscardPiles`):

1. **Event-tracked pile** (`eventDiscardPile`): built incrementally from type-8 truth
   events (all seats) plus, historically, resolved type-5 pile-face diffs. Reliable
   ordering, but only as complete as the events actually observed this session (a
   fresh plugin instance starts empty).
2. **Node-scanned pile** (`scannedPileTiles`, via `DecodePileTiles`): a direct read of
   every pile slot's face this tick, decoded the same way as hand slots. Complete
   regardless of session history, but subject to the same face-decode caveats plus
   the claim-highlight duplicate-render issue.

Merge takes `Math.Min(Math.Max(scannedCount[kind], eventCount[kind]), 4)` per tile
kind — the max of the two sources (whichever saw more copies) clamped to 4 (the
hard maximum copies of any tile kind that can exist), which absorbs the duplicate-
render inflation without discarding real information from either source.

---

## Cold-start reader lifecycle

A `GameStateReader` is frequently constructed **mid-round** — a plugin reload or a
game crash-and-restart doesn't wait for a round boundary. This one situation caused
three compounding bugs (all fixed 2026-07-06) worth understanding together, since a
new one could easily be introduced the same way in future changes:

1. **A mid-round type-21 refresh can be misclassified as a genuine new deal.**
   `roundEnded` defaults to `true` on construction (there's no way yet to know
   otherwise) — so the very first type-21 a fresh reader observes takes the
   destructive "real deal, reset everything" branch even when it's actually just an
   ordinary mid-round refresh. Mitigated two ways: (a) establishing a legitimately-
   sized hand during normal tracking sets `roundEnded = false` (proof the round is
   genuinely in progress — see `Tick()`); (b) `HandleScoreDelta`/`HandleWinScreen`
   explicitly clear `directHandTiles` when they set `roundEnded = true`, and the
   cold-start bootstrap (next point) explicitly skips event types 29/32 — otherwise
   the just-ended round's still-legit-sized winning hand would immediately re-arm
   `roundEnded = false` again before the next real deal arrives.
2. **The prompt-edge rollback (`OnPromptDetected`) can restore a snapshot from the
   reader's own not-yet-converged bootstrap.** It uses `recentHands.Peek()` — the
   OLDEST of up to 6 buffered pre-prompt hands — on the assumption that the buffer
   holds trustworthy continuous tracking history. For a reader only a few ticks old,
   the oldest entry can be a partial/garbage read from the very first tick. Fixed:
   `Tick()` only enqueues into `recentHands` when the hand size is legitimate for the
   current meld count — a bad snapshot can never enter the rollback pool.
3. **The cold-start bootstrap itself (in `Tick()`) originally discarded its ENTIRE
   decoded read if even one slot failed to decode, AND only ran once.** The far-right
   slot during an active claim window is exactly the opponent's ghost-offered-tile
   slot, and it routinely has no learnable/decodable face of its own — so
   bootstrapping mid-window reliably failed outright, leaving the hand permanently
   empty for that whole window (symptom: the analyzer reports "Waiting for stable
   hand data"). Fixed: stop at the first undecodable slot (keep everything decoded
   before it) and only trust that prefix if its length lands on a legitimate
   closed-hand size — precisely the "13 real tiles + 1 undecodable ghost" shape.
   **Second fix (2026-07-06, same day):** the gate was originally `directHandTiles.
   Count == 0` — a ONE-SHOT check. A real hover event can add a single
   confirmed tile via `OnHoverEvent`'s sync logic before the very first bootstrap
   attempt manages a full decode (e.g. a transiently-undecodable face on that exact
   first tick); that bumps `Count` off zero, and the one-shot gate then never retried
   even though a full decode would succeed moments later — the hand got stuck
   growing one tile at a time via real mouse hovers only, for the whole frozen
   window. Fixed: the gate now retries every tick while the hand size isn't yet
   legitimate for the current meld count (not just once on empty).

**Residual, accepted gap**: a cold-started reader's very first claim window can still
have a genuinely unknowable offered/claimable tile if BOTH the ghost face is
undecodable AND no type-8 discard-truth event has been observed yet this session (no
override available). `/prompt`'s `offered`/`claimableTile`/`farRightCallable` report
`null` rather than guessing in this case — it self-resolves as soon as a real type-8
event or a later-readable ghost supplies the tile.

**A related but distinct bug hit the same symptom (empty/garbage tracked hand) from a
different cause**: `ReadHandTilesFromAtkValues` trusting a spurious type-30 match — see
the count=50 Layout-2 gotcha under [AtkValues layouts](#atkvalues-layouts). When
diagnosing a stuck/garbage hand, check BOTH this lifecycle section and that gotcha.

---

## Debug / Operate HTTP API

Temporary, localhost-only (`127.0.0.1`), started via `/mhater debug [port]` (default
port `9787`). All game-memory access is marshaled onto the framework thread by
`Plugin.RouteDebugRequest`; the HTTP server itself (`Core/DebugServer.cs`) runs on a
plain `TcpListener` so no Windows URL ACL is required. **Not** meant for automated
play — it exists so the plugin can be operated and inspected directly during
debugging, instead of a human reporting anomalies for offline diagnosis.

### Read endpoints

| Endpoint | Purpose |
|----------|---------|
| `/status` | Tracked hand/melds/wall/pile counts + one-line analysis summary |
| `/state` | `/status` + `/prompt` + `/reco` merged into one response |
| `/hand` | Tracked hand vs every scanned slot (icon/learned/hover/face detail) vs raw AtkValues read |
| `/piles` | Pile face scan, decoded pile, merged pile |
| `/frame[?addon=Emj]` | Full AtkValues table with tile-icon hints |
| `/prompt` | Call-window state, raw button texts, ghost-slot view, `farRightSlot`/`claimableTile`/`farRightCallable` |
| `/reco` | Full analysis result including ranked discard options |
| `/events[?tail=200]` | Rolling timeline (cap 600) of raw refresh events, hover events, tracker decisions, and operate actions, interleaved in arrival order |
| `/tree[?node=133][&addon=Emj]` | Full (or focused) node tree as text |
| `/nodes[?addon=Emj]` | Every event-bearing node: pointer, NodeId, owner path, type, visibility, position, registered event chain — the operate-target map |
| `/addons` | Every loaded addon and its visibility (for finding result/confirm screens by name) |

### Operate endpoints

| Endpoint | Purpose |
|----------|---------|
| `/discard?slot=N` or `?tile=8s` | Click a hand slot (turnkey; requires a visible, addon-bound `ButtonClick` chain or refuses with a specific reason) |
| `/hover?slot=N` | Fire `MouseOver` on a slot; returns the addon's own tile-name response (`AtkValues[1]`) — an on-demand ground-truth read |
| `/call?option=Chi` | Click a button by its visible label (exact match, then prefix); routes through `ListItemClick` automatically if the label lives inside a list |
| `/listclick?node=<list>&index=N` | Select a list row by index directly (bypasses label matching) |
| `/click?node=0x…|<nodeId>[&param=]` | Full click simulation (MouseOver → activation event, searched per the 4-tier visible/addon-bound priority) on any node |
| `/fire?node=…&type=<AtkEventType int>[&param=]` | One precise event, for calibration |
| `/callback?values=i,i,…` | Raw `AtkUnitBase.FireCallback` with int values |
| `/riichi?declared=true\|false` | Manual riichi-lock override (no auto-detection yet — see Known issues) |

### Calibration workflow for a new interaction

1. `/nodes` — find the target node and its registered event types.
2. `/fire` one event type at a time.
3. Watch `/events` for the game's response (the plugin's own fired events are
   captured in the same timeline via the `PostReceiveEvent` hook, so the loop closes
   without needing a second tool).

---

## Known issues / open items

- See [Cold-start reader lifecycle](#cold-start-reader-lifecycle) for the
  reload/crash-mid-round failure chain (three compounding bugs, all fixed
  2026-07-06) and its one residual accepted gap.
- **A stale phantom tracked meld can silently break ALL claim-window detection**
  (live 2026-07-06): `trackedCalledMelds` picked up a false-positive entry (exact
  `HandleMeldAccepted`/atkType=74 trigger not yet root-caused) that didn't match
  reality — the real closed hand cleanly scanned as 14 tiles (only possible with
  ZERO melds) while a meld was still tracked. Every `HandTracking.MaxClosedTiles(
  trackedCalledMelds.Count)`-based expected-slot-count check throughout the
  ghost/claim-window logic then compared against the wrong (too-small) size, so a
  real, visible Chi/Pass window never got recognized at all — no error, just
  silence. Fixed with a self-correction at the top of `Tick()`: 13-14 visible hand
  slots is mathematically impossible with any tracked meld, so seeing that many
  while `trackedCalledMelds` is non-empty clears it as stale. If a window still goes
  undetected after this fix, check `/status`'s `melds` field for anything that
  doesn't match what's visually on screen.
- **A claim window's "offered tile" can still come back wrong if the plugin reloads
  while the window is open** (live 2026-07-06, same incident as above): each reload
  wipes `lastDiscardKindAuthoritative`/`lastDiscardEventKind` — the confirmed
  type-8 truth for the discard that opened the window. If the window is STILL open
  after a reload (nothing else has happened in-game to re-confirm it), the fresh
  reader has no event history left and falls back to the ghost-slot face decode,
  which can itself be stale (confirmed: showed `5z` when the real discard, per the
  original pre-reload type-8 truth AND the player's own eyes, was `5s`). `/hover` on
  the ghost slot is not a reliable rescue either — it returned no tooltip text in
  this exact case. No fix exists for this specific combination (the confirming
  event already happened before the current reader existed — nothing left to
  recover from); if `offeredTile`/`claimableTile` disagree with what's visibly on
  screen, trust the screen — the plugin's own automation doesn't act on the
  player's behalf, so a wrong read here doesn't cost anything except bad advice.
  Related, now-fixed bug from the same investigation: `OnClaimPromptEdge`'s
  "trust type-8 over the ghost face" logic had a redundant elapsed-time re-check
  (`< 2500ms`) on top of the `lastDiscardKindAuthoritative` flag, which actively
  discarded an otherwise-still-100%-valid authoritative reading purely because
  detection itself had been delayed by an unrelated bug (the phantom meld above) —
  removed; the flag's own lifecycle is the correct signal, not a wall-clock re-check
  at the consumption site.
- **No automatic riichi-declared detection.** Once a player declares riichi they are
  locked into discarding whatever they draw (tsumogiri) every turn — the analyzer
  supports this correctly (`AnalysisContext.IsRiichi` forces the recommendation onto
  `hand.ClosedTiles[^1]`, the drawn tile, instead of the computed "best"), but
  **nothing sets it automatically yet**. Live investigation (2026-07-06) ruled out
  every hypothesis tried: node Color/Multiply/Add/Alpha are byte-for-byte identical
  between a supposedly-locked tile and the drawn one (the whole addon window was
  just uniformly dimmed at the root, unrelated to per-tile state); a 90°-rotated pond
  tile exists but on an OPPONENT's pile, not the declarer's — in this addon rotation
  more likely marks "this discard was called," not riichi (don't reuse the universal
  physical-tile convention here); no distinct "REACH" banner or stick token is
  visible anywhere on screen. Set manually via `/riichi?declared=true|false` (resets
  on the next genuine new deal) until a real signal is found. Worth trying next:
  comparing a riichi-declaring discard's normally-unused type-8 fields ([3]/[4]/[5])
  against a normal discard's.
  **Important caveat from the 2026-07-06 incident**: before assuming a "stuck
  recommendation" is a riichi-lock problem, verify the tracked hand itself isn't
  simply corrupted (check [Cold-start reader lifecycle](#cold-start-reader-lifecycle)
  and the type-30 gotcha under [AtkValues layouts](#atkvalues-layouts) first) — that
  was the actual root cause the one time this was reported, and the analyzer picked
  the "obviously correct" tile on its own once given the real hand, no lock needed.
- **Addon freeze is a real, observed failure mode** (2026-07-05): the Emj addon can
  stop updating entirely while remaining open/visible and fully readable (valid
  `AtkValues`, sensible-looking node tree) — clicks (even on visible, addon-bound,
  correctly-identified nodes) simply do nothing, and no new refresh events arrive.
  This is external to the plugin (a game/addon-side stall); the debug API's read
  endpoints will keep returning the same stale snapshot indefinitely. There is no
  known client-side detection for this yet beyond noticing no `/events` activity
  over an unusually long span while wall/hand data isn't changing.
- Ron-only repeat windows with stuck labels remain undetectable by the slot-jump
  fingerprint (accepted, low impact — legality gate still protects against acting on
  them incorrectly if they're ever misdetected as active).
- Whether type-5's seat-0 `[3]` own-draw reading is **always** reliable has one
  confirming data point so far; treat it as strong evidence, not yet exhaustively
  proven across kan/replacement draws or the very first draw of a hand.
- Chi run-choice sub-windows (when a claimed tile has more than one legal chi shape),
  riichi declaration, kan, and ron/tsumo confirmation for the local player have not
  yet been exercised through the operate API — expected to follow the same
  `ListItemClick` pattern used for Chi/Pon/Pass, but unconfirmed.
- The bottom-right addon-level buttons (NodeIds 6-11) have not been mapped to
  specific functionality.
