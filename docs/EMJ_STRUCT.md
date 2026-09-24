# AddonEmj struct map

Live mapping of `Client::UI::AddonEmj` (the Doman Mahjong addon object) done 2026-09-18 with
Cartographer (`/mem/watch` on the addon, `/mem/changes` per turn, correlated with `/events`).
EU client, Dalamud 15.0.3.5, FFXIVClientStructs 7.56.2.9089. Two matches' worth of rounds were
played by tsumogiri (every call declined), so every write below was observed against known game
actions. Offsets are from the `AtkUnitBase*` returned by `IGameGui.GetAddonByName("Emj")`.

Status legend: **verified** = written/read against a known screen state at least twice;
*plausible* = consistent with one observation or with a sibling field; unknown = not identified.

**Implementation.** Every offset here is data, not code: `resources/layouts/emj.json` (shipped next
to the DLL, embedded copy as fallback) → `EmjLayout` → `EmjStructReader` copies `RequiredBytes`
(currently 4061) from the addon each tick into a `StructFrame`, and `StructFrame.Decode` turns it
into a `DecodedStruct` (tiles, per-seat counters, meld records). `MahjongHater.Tests/State/
StructFrameTests` + `LiveFixtureTests` replay the hex fixtures below through the same path.
Everything this table lists as *not in the struct* comes from `AtkValues` events and text nodes —
see [`EMJ_ADDON_REFERENCE.md`](EMJ_ADDON_REFERENCE.md), "Reading model".

## Headline

- The addon keeps **only display state**: local hand, per-seat counters, scores, meld tile indices,
  dora indicator. It does **not** hold discard tile identities, chi compositions, winds, dealer,
  wall, honba or riichi sticks — those are pushed to nodes and to `AtkValues` refresh events.
  `Client::Game::UI::Emj` (static, 0x38 bytes) is all zeros during play and `AgentEmj` (0x60 bytes)
  points only at UI-module objects; neither holds tiles.
- Layout: `AtkUnitBase` (0x000–0x237), then **four seat panels of 0x2E0 bytes** at 0x238 / 0x518 /
  0x7F8 / 0xAD8 (relative seats 0=us, 1=shimocha, 2=toimen, 3=kamicha), then the hand array at 0xDB8.

## Global fields

| Offset | Type | Meaning | Status | Evidence |
|---|---|---|---|---|
| `0x0DB8` | `int32[14]` | Local hand as icon IDs (base 76041; idx 34/35/36 = red 5m/5p/5s). Slots 0–12 = sorted closed hand, **slot 13 = drawn or claimed tile** (0 when none). | **verified** | Slot 13 zeroes on our discard (`0xDEC 172901→000000`), refills on the next draw; whole array rewritten on deal (`0xDB8 len=51` at state 2); zeroed at state 27; matches `AtkValues[16..21]` hand slice and `AtkValues[2]` draw. |
| `0x0FD8` | `int32` | Dora indicator icon ID | **verified** | Set at every deal (`000000→172901` = 6p, `→2A2901` = R, `→1F2901` = 5s), cleared at state 27. |
| `0x0FDC` | `byte` | Number of dora indicators revealed (1 after the deal animation) | *plausible* | `00→01` ~360 frames after each deal; cleared with the indicator. Kan-revealed indicators not observed. |
| `0x0FF0` | `ptr` | Transient: set while a call prompt is open (points into the tile-descriptor pool the `0xDF0` pointers use) | *plausible* | Set at every Pon/Chi prompt frame, alternates between two values. |
| `0x0FF8` | `byte` | Transient counter tied to call prompts (`0x66`/`0x65`) | unknown | Changes together with `0x0FF0`. |
| `0x0F40` | `int32[7]` | Icon IDs 121486..121492 | unknown | Static during play; possibly the stick/marker icon set. |
| `0x12BC`, `0x12C0` | `int32` | Small ints that change only at round end (`2→0`, `5→3`) | unknown | Not a round counter (read 2 during round 5). |
| `0x0E18` | `int32` | `-1` | unknown | Never changed. |

Everything else between 0x0DF0 and 0x1100 is node/resource pointers; 0x1100–0x6000 is embedded
node/tween data that churns every frame (ignore).

## Seat panel (0x2E0 bytes, base `B = 0x238 + 0x2E0·seat`)

| Offset in panel | Type | Meaning | Status | Evidence |
|---|---|---|---|---|
| `+0x000` | `ptr` | Node (score panel) | — | |
| `+0x008`, `+0x00C` | `int32`, `int32` | Score display copies (current/target of the number tween) | *plausible* | Both equal `+0x2C8` after animations settle. |
| `+0x010` | `float[4]` | Number-tween params (1.0) | — | |
| `+0x028` | `ptr` | Node | — | |
| `+0x030`, `+0x034` | `int32`, `int32` | Point-difference display copies | *plausible* | Equal `+0x2CC`. |
| `+0x038` | `float[4]` | Tween params | — | |
| `+0x050` … `+0x23F` | `ptr[62]` | Node pointers: hand tile slots (14, listed twice for opponents, two distinct sets for us: face-up and face-down), discard-pile slots, meld slots. Grouping by role is **not** established. | — | Pool addresses only. |
| `+0x240` | `int32[4]` | **Meld i tile index** (34-index, same space as icon − 76041). `-1` = empty. **255 for chi or concealed kan** (composition comes from events; see `research/CLOSED_KAN_2026_09_23.md`). Reset to `-1` at round end. | **verified** | Seat 2 pon 7s → `0xA38 = 24`; pon Wh → `31`; pon G → `32`; seat 3 pon 5p → `0xD18 = 13`; chi 7-8-9p → `0xA3C = 255`. All matched the type-13 meld event `[6]`. |
| `+0x250` | `byte[4]` | **Meld i claimed-from direction**, relative to the caller in turn order: `1` = shimocha (next seat), `2` = toimen, `3` = kamicha (previous seat). **Not reset at round end** — only meaningful for `i < meldCount`. | **verified** | Seat 2 pon of seat 1's 7s → `3`; seat 3 pon of our 5p → `1`; seat 2 pon of our G → `2`. Equals type-13 event `[5]`. |
| `+0x254` | 4 bytes | Uninitialised garbage | — | |
| `+0x258` … `+0x2BF` | `ptr[13]` | Node pointers | — | |
| `+0x2C0` | `int32` | Garbage-looking constant (differs per seat) | unknown | |
| `+0x2C4` | `byte` | **Closed tile count** excluding the drawn/claimed tile (13 at deal; 11 right after a pon/chi, 10 after the following discard; 7 with two melds). Set to 0 for non-winners at the win screen; animates 0→4→8→12→13 during the deal for opponents. | **verified** | `0xABC 0D→0B→0A`, `08→07`; `0x7DC 00→04→08→0C→0D`. |
| `+0x2C5` | `byte` | **Declared meld count**, including concealed kans | **verified** | `0xABD 00→01→02`, reset to 0 at round end. |
| `+0x2C6` | `byte` | **Discard count** (increments per discard; **does not** decrement when the discard is claimed) | **verified** | Every discard `+1`; `0x7DE` stayed at 12 when seat 2 pon'd seat 1's 12th discard. |
| `+0x2C7` | `byte` | **Riichi discard index** (0-based index into the seat's discards), `0xFF` when not in riichi | **verified** | `0x7DF FF→07` and `0xABF FF→0B` at the riichi declarations (state 12); reset to `FF` on deal. |
| `+0x2C8` | `int32` | **Score** | **verified** | 25000 ×4 at start; 19000/18100/41900/21000 after round 2 (sum 100000); later 17000/18100/43500/21400 matched the score text nodes. |
| `+0x2CC` | `int32` | Point difference shown in "Ctrl: Show Point Difference" mode: `ourScore − seatScore` (for seat 0 it holds the raw score) | **verified** | `-1100`, `-26500`, `-4400` against 17000/18100/43500/21400. |
| `+0x2D0` | `int32` | 0 | unknown | |
| `+0x2D7` | `byte` | `0xFF` | unknown | Never changed. |
| `+0x2D8` | `byte` | Toggles 0/1 on that seat's discards; stays 1 for us while we tsumogiri | *plausible: last discard was tsumogiri* | `0x7F0 0→1→0→1`, `0xAD0`, `0xDB0` flip on opponent discards only. |

Absolute offsets for the four seats (seat 0..3):

| Field | seat 0 | seat 1 | seat 2 | seat 3 |
|---|---|---|---|---|
| meld tile indices | `0x478` | `0x758` | `0xA38` | `0xD18` |
| meld from-direction | `0x488` | `0x768` | `0xA48` | `0xD28` |
| closed tile count | `0x4FC` | `0x7DC` | `0xABC` | `0xD9C` |
| meld count | `0x4FD` | `0x7DD` | `0xABD` | `0xD9D` |
| discard count | `0x4FE` | `0x7DE` | `0xABE` | `0xD9E` |
| riichi index | `0x4FF` | `0x7DF` | `0xABF` | `0xD9F` |
| score | `0x500` | `0x7E0` | `0xAC0` | `0xDA0` |
| point diff | `0x504` | `0x7E4` | `0xAC4` | `0xDA4` |
| tsumogiri flag | `0x510` | `0x7F0` | `0xAD0` | `0xDB0` |

## Not in the struct — where to read it instead

| Datum | Source | Notes |
|---|---|---|
| Opponent discard tiles | `AtkValues` refresh type **8**: `[1]` = seat, `[2]` = tile icon (`EventTracker`) | Fires once per discard for every seat incl. us. Opponent type-5/20 `[3]` is the 76041 placeholder. The struct only counts them (`+0x2C6`). |
| Own discard | type 8 with `[1]=0`, or the ButtonClick callback `[15, icon]` | |
| Draw / wall | type **5**: `[1]` = live wall remaining (70 → 0), `[2]` = seat that drew, `[3]` = drawn icon for us (placeholder for others) | `SnapshotBuilder` uses the type-5 wall, else `70 − Σ discard counts`. The centre counter node `1/46/105/{2,3}/2` (two `Counter` digits) shows the same number. |
| Meld composition | type **13**: `[1]` caller, `[3]` 4=pon / 5=chi / 6=kan, `[4]` display selector (not a meld ordinal), `[5]` from-direction (0 for concealed kan), `[6]` tile index or ambiguous 255, `[7]` tile count, `[8..11]` tile icons (chi: claimed tile first) | Only source for chi tiles and for red fives inside melds; a missed one is inferred from our closed-hand delta (`MeldInference`). |
| Added kan | type **14**: `[1]` caller, `[2]` tile index, `[3]` fourth tile icon | Upgrades the existing pon in place; count does not increase. Observed at 16:05:31 on 2026-09-23 and supported by the saved native handler. |
| Riichi declaration | struct riichi index (`+0x2C7`) flips on the riichi discard; state code **12** on the declaring seat's turn | The snapshot's `Seats[i].Riichi` is the struct index ≠ 255. |
| Seat winds | Text nodes `1/36/37/38/7/9` (us), `1/36/39/40/8/10`, `1/36/41/42/8/10`, `1/36/43/44/8/10` — "East"/"South"/"West"/"North" (`EmjStateReader.ScanWinds`, every 30 ticks) | Dealer = the seat whose text is "East". Round wind: the hidden text `1/46/54/57` (`nodes.roundWindText`, win-screen residue — leading word) or `AtkValues[2]` on type 32 ("East 4 North Wind"). |
| Honba | Hidden text `1/46/48/2` "Honba N" | Only visible during the announcement; residue afterwards. **Not read** — the snapshot reports 0. |
| Riichi sticks / honba counters | Text nodes `1/46/54/86/88` and `/91` ("0") | On the recap panel; unverified during play. **Not read.** |
| Ura dora | — | Not found; `uraDoraIndicator` is `null` in the layout. |
| Scores (redundant) | Text nodes `1/36/{37/38/10/12, 39/40/11/13, 41/42/11/13, 43/44/11/13}/2` | Same values as the struct. |
| Hand-end tenpai / winner | Result banner texts `1/36/{37/38,39/40,41/42,43/44}/2/2/3` ("Tenpai!"/"Noten…" on a draw); type-32 `[1]` = winner seat | Feeds the tenpai calibration CSV. |
| Call prompt rows | List `1/46/104/3` (`ListItemClick → addon`), rows by item-table index: e.g. row 0 "Pon"/"Chi", row 1 "Pass" (labels from the renderer text — the item table's `Label` is empty) | Open = type-19/23/25, close = type-5/8/13/74/29/32. The panel and labels persist after the window closes and never serve as a close signal. |

## State codes seen (`AtkValues[0]`)

`2` deal (hand written, then counts animate), `5` draw, `6` our turn (draw in slot 13), `8` discard,
`9`/`10`/`12`/`22` transient refreshes (`12` = riichi declared, `22` = call window opened for someone),
`13` meld, `15` others' turns / idle, `20` discard confirm, `21` post-meld hand refresh, `23` call
options, `27` scores/transition after a win, `32` win screen (hand still populated; counts hold; scores
animate after "Next").

## ClientStructs skeleton (observed, not statically verified)

```csharp
[StructLayout(LayoutKind.Explicit, Size = 0x2E0)]
public unsafe struct EmjSeatPanel {
    [FieldOffset(0x008)] public int ScoreDisplayCurrent;
    [FieldOffset(0x00C)] public int ScoreDisplayTarget;
    [FieldOffset(0x030)] public int DiffDisplayCurrent;
    [FieldOffset(0x034)] public int DiffDisplayTarget;
    [FieldOffset(0x050)] public fixed ulong Nodes[62];          // AtkResNode*/AtkComponentNode*
    [FieldOffset(0x240)] public fixed int MeldTileIndex[4];     // 34-index, -1 empty, 255 chi/concealed kan
    [FieldOffset(0x250)] public fixed byte MeldFromDirection[4]; // 1 shimocha, 2 toimen, 3 kamicha
    [FieldOffset(0x258)] public fixed ulong Nodes2[13];
    [FieldOffset(0x2C4)] public byte ClosedTileCount;
    [FieldOffset(0x2C5)] public byte MeldCount;
    [FieldOffset(0x2C6)] public byte DiscardCount;
    [FieldOffset(0x2C7)] public byte RiichiDiscardIndex;       // 0xFF none
    [FieldOffset(0x2C8)] public int Score;
    [FieldOffset(0x2CC)] public int PointDifference;
    [FieldOffset(0x2D8)] public byte LastDiscardWasTsumogiri;   // plausible
}

[StructLayout(LayoutKind.Explicit, Size = 0x1100)]                // real allocation is larger
public unsafe struct AddonEmj {
    [FieldOffset(0x000)] public AtkUnitBase AtkUnitBase;
    [FieldOffset(0x238)] public EmjSeatPanel Seat0;               // us
    [FieldOffset(0x518)] public EmjSeatPanel Seat1;               // shimocha
    [FieldOffset(0x7F8)] public EmjSeatPanel Seat2;               // toimen
    [FieldOffset(0xAD8)] public EmjSeatPanel Seat3;               // kamicha
    [FieldOffset(0xDB8)] public fixed int HandIconIds[14];        // slot 13 = draw/claim
    [FieldOffset(0xFD8)] public int DoraIndicatorIconId;
    [FieldOffset(0xFDC)] public byte DoraIndicatorCount;
}
```

## Fixtures

`resources/fixtures/emj_struct_*.hex` are raw dumps of `+0x0000..+0x1100` (4352 bytes, lowercase
hex, one line). Each has a sidecar `.json` with the state at capture time: `state0`, `hand`
(slots 0–13, `-` = empty), `scores`, `dora`, `discardCounts`, per-seat `melds`, and the discard
sequences where they could be reconstructed from type-8 events.

## Open questions

- Grouping of the 62 + 13 node pointers per seat (which are pile slots vs meld slots) — resolve by
  reading `NodeId` through each pointer.
- `+0x2D8` "tsumogiri" reading, `0x0FDC` behaviour after a kan, `0x0FF8`, `0x12BC/0x12C0`.
- Concealed and added kans were captured on 2026-09-23 (see
  `research/CLOSED_KAN_2026_09_23.md`). The former shares marker 255 with chi; the latter
  upgrades a pon through refresh 14. Further client versions still need layout validation.
- Whether `+0x2C6` is capped / how furiten and the "Calls Off" toggle surface.
- Honba, riichi sticks and ura dora have no struct field; the text-node candidates above are unread.
