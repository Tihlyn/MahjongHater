# Emj struct survey — can we read offered actions authoritatively?

Survey date 2026-09-22. Question: is there a struct/agent field that tells us which of
**Ron, Tsumo, Riichi, Pon, Chi, Kan, Pass** the Emj UI is offering right now, so the plugin can
stop depending on button text?

**Headline answer.** No FFXIVClientStructs type describes the Mahjong table at all, and nothing in
the plugin's 4352-byte addon dump encodes the offered option set. **But the offered actions are
already in `AtkValues` as integers**, not only as strings: on every type-19/23 refresh,
`[2]` and `[3]` are numeric option codes (`1`=Tsumo, `2`=Ron, `3`=Riichi, `4`=Kan, `5`=Pon,
`6`=Chi, `0`=absent) and, on type-23, `[1]` is the list row count. Verified on 257 events across
the whole 2026-09-22 session, in every array size, with **zero** disagreements against the strings.
The plugin does not read any of them.

A second result contradicts `LIVE_ISSUES_2026_09_22.md` §3 and is set out in
[§6](#6-correction-to-live_issues-3--the-dropped-ron-was-a-phantom): the events never dropped a
Ron. The phantom came from the label-edge fallback, and the "union the options" fix would make the
plugin click calls the game is not offering.

Sources: `FFXIVClientStructs.dll` 7.56.2.9089 (`%AppData%\XIVLauncher\addon\Hooks\dev\`) read with
`MetadataLoadContext`; `resources/fixtures/emj_struct_*.hex` (8 dumps of `+0x0000..+0x1100`);
`%AppData%\XIVLauncher\dalamud.log` + `dalamud.old.log` for 2026-09-22 14:40–17:33;
`docs/EMJ_STRUCT.md`, `docs/EMJ_ADDON_REFERENCE.md` and the code at `320628c`.

Every claim below is tagged **[observed]** (read out of the DLL, a fixture or the log) or
**[inferred]** (a reading consistent with the evidence but not directly demonstrated).

---

## 1. Emj-related types in the installed FFXIVClientStructs

`FFXIVClientStructs, Version=7.56.2.9089`, 31 436 types. Matching `*Emj*` or `*Mahjong*` gives
**four** real types (the rest of the 90 matches are `+Delegates+*` and `+*VirtualTable` helpers).
A scan of the assembly's whole string heap for any identifier containing `Emj` returns only:
`AddonGSInfoEmj`, `AddonGSInfoEmjVirtualTable`, `AgentEmjVoiceCharacterVirtualTable`, `EmjCostume`,
`EmjCostumeId`, `EmjIntro`, `EmjModuleVirtualTable`, `EmjSetting`, `EmjSolo`, `GSInfoEmj`,
`GetAgentEmjVoiceCharacter`, `GetEmjModule`, `HighlightedEmjVoiceNpc`, `SelectedEmjVoiceNpc`.
No identifier anywhere in the assembly contains `Mahjong`, `Riichi`, `Tsumo`, `Chii` or `Haifu`.
**[observed]**

| Type | Size | What it is | Use for this question |
|---|---|---|---|
| `FFXIV.Client.Game.UI.Emj` | `0x38` | **Declared with zero fields.** An empty placeholder. | None. Matches `EMJ_STRUCT.md`'s live note that it is all zeros during play. |
| `FFXIV.Client.UI.Misc.EmjModule` | `0xD0` | The saved-settings module (`UserFileManager` subclass). | None — config only. Fields below. |
| `FFXIV.Client.UI.AddonGSInfoEmj` | `0x2B8` | The **Gold Saucer rating window**, not the table. Only 7 own fields, all `AtkTextNode*`/`AtkComponentButton*` (`MatchesPlayed` `0x238`, `CurrentRating` `0x240`, `HighestRating` `0x248`, `Rank` `0x250`, `NextRankPoints` `0x258`, `Points` `0x260`, `ResetRankButton` `0x268`); everything below `0x238` is inherited `AtkUnitBase`. | None. |
| `FFXIV.Client.UI.Agent.AgentEmjVoiceCharacter` | `0x90` | The voice/costume picker agent. | None. Fields below. |

**There is no `AddonEmj` and no `AgentEmj` type in this DLL.** **[observed]** Both names are the
plugin's and Cartographer's own labels for memory the repo mapped by hand; the game's own
`AgentEmj` exists (it is on the crash stacks in `LIVE_ISSUES_2026_09_22.md` §2) but FFXIVClientStructs
does not describe it. So there is no vendored, upstream-maintained layout to lean on.

`EmjModule` (`0xD0`) — settings only, nothing about a live hand: **[observed]**

```
0x000 UserFileEvent   0x008 CharacterContentId   0x010 UserFileManager   0x018 TempDataPtr
0x020 TempDataBytesWritten  0x044 HasChanges  0x045 IsSavePending  0x046 IsVirtual
0x048 byte TileSet            0x049 bool HideHints        0x04A bool HideDangerousTileMarker
0x04B bool HideChatLog        0x04C byte Unk4C            0x04D byte Unk4D
0x04E bool HideTileNames      0x04F bool ShowHighResolutionLayout
0x050 bool ShowTraditionalDoraIndicator  0x051 byte OwnPlayerNameSetting
0x052 byte OthersPlayerNameSetting       0x053 bool DisableCharacterVoices
0x054 byte EmjVoiceNpc         0x055 byte EmjCostumeId    0x058 int Unk58   0x05C int Unk5C
0x068 int[16] _unk68   0x0A8 short[3] _unkA8
0x0C8 byte[4] _seenVoiceBitflags   0x0CC byte[4] _seenCostumeBitflags
```

`AgentEmjVoiceCharacter` (`0x90`) — costume picker state, nothing about a hand: **[observed]**

```
0x000 AgentInterface  0x028 Unk28  0x029 Unk29  0x02A Unk2A  0x02B Unk2B
0x02C uint AttireAddon  0x030 Unk30  0x034 Unk34  0x038 SoundData* VoLine
0x040 CurrentCharacterPage  0x044 CharacterPages  0x048 HighlightedEmjVoiceNpc
0x04C SelectedEmjVoiceNpc   0x050 SelectedAttire  0x054 Unk54
0x058 StdVector<uint> SelectedAttires   0x070 StdVector<EmjCostume> SelectedCostumeRows
```

`AgentInterface` (`0x28`) — the base every agent starts with, so the *only* thing we could
statically rely on if we resolved the real `AgentEmj` by id: **[observed]**

```
0x000 AgentInterfaceVirtualTable* VirtualTable   (also AtkEventInterface at 0x000)
0x010 UIModuleInterface* UIModuleInterface
0x018 AtkModuleInterface.AtkEventInterface* OpenerEventInterface
0x020 uint AddonId
```

`AgentId` relevant members, verbatim: `Emj = 328`, `Unk329 = 329`, `Unk330 = 330`,
`EmjIntro = 331`, `EmjVoiceCharacter = 332`, `EmjSetting = 339`. **[observed]**
So the table agent is reachable as `AgentModule.GetAgentByInternalId(AgentId.Emj)` — it returns an
`AgentInterface*` with **no typed body past `0x20`**. Anything beyond that would be a hand-mapped
offset with no upstream backing, which is exactly what this survey is trying to avoid.
`EMJ_STRUCT.md` records the live agent as `0x60` bytes pointing only at UI-module objects.

---

## 2. What the plugin already reads vs what is documented but unused

### Struct offsets the reader actually consumes

Everything in `resources/layouts/emj.json` with a non-null value; `EmjStructReader` copies
`RequiredBytes` from the addon each tick and `StructFrame.Decode` turns it into a `DecodedStruct`.

| Datum | Offset(s) | Consumed by |
|---|---|---|
| Local hand, `int32[14]` icon ids, base 76041, slot 13 = draw/claim | `0x0DB8` | `DecodedStruct.ClosedTiles` / `DrawnTile` |
| Dora indicator icon id | `0x0FD8` | `SnapshotBuilder` (merged with type-19 kan doras) |
| Dora indicator count | `0x0FDC` | `DecodedStruct` |
| Meld tile indices `int32[4]` (`-1` empty, `255` chi) | `0x0478 / 0x0758 / 0x0A38 / 0x0D18` | `EventTracker` meld trim, `MeldInference` |
| Meld from-direction `byte[4]` | `0x0488 / 0x0768 / 0x0A48 / 0x0D28` | same |
| Closed tile count | `0x04FC / 0x07DC / 0x0ABC / 0x0D9C` | round/meld consistency |
| Meld count | `0x04FD / 0x07DD / 0x0ABD / 0x0D9D` | meld trim ("the struct is the authority for counts") |
| Discard count | `0x04FE / 0x07DE / 0x0ABE / 0x0D9E` | wall fallback `70 − Σ`, round reset |
| Riichi discard index (`0xFF` = none) | `0x04FF / 0x07DF / 0x0ABF / 0x0D9F` | `Seats[i].Riichi` |
| Score | `0x0500 / 0x07E0 / 0x0AC0 / 0x0DA0` | snapshot |
| Point difference | `0x0504 / 0x07E4 / 0x0AC4 / 0x0DA4` | snapshot |
| `AtkValues[0]` state code, `AtkValues[1]` wall baseline | via `AtkUnitBase` | `StructFrame` |

### Documented in `EMJ_STRUCT.md` but NOT read by any code

| Offset | Documented meaning | Status |
|---|---|---|
| `+0x2D8` per seat (`0x0510 / 0x07F0 / 0x0AD0 / 0x0DB0`) | last discard was tsumogiri (*plausible*) | not in `emj.json`, never read |
| `0x0FF0` | pointer set while a call prompt is open (*plausible*) | never read — see §3 for what it actually does |
| `0x0FF8` | transient `0x66`/`0x65` counter beside it | never read |
| `0x0F40` | `int32[7]`, icons 121486–121492, static | never read, unknown role |
| `0x12BC`, `0x12C0` | small ints that change only at round end | never read, unknown |
| `0x0E18` | constant `-1` | never read |
| seat `+0x008/+0x00C`, `+0x030/+0x034` | score / point-diff tween display copies | redundant with `+0x2C8/+0x2CC` |
| seat `+0x050..+0x23F` (62 ptrs), `+0x258..+0x2BF` (13 ptrs) | node pointers, roles unresolved | never read |
| seat `+0x2C0`, `+0x2D0`, `+0x2D7` | unknown | never read |

`emj.json` keys deliberately left `null` — **no struct field is known for any of them**:
`discardArrays`, `uraDoraIndicator`, `roundWind`, `seatWind`, `dealerSeat`, `honba`,
`riichiSticks`, `wallRemaining`.

### What comes from text / events instead

`EventTracker` case 19/23 is the entire source of call legality:

```csharp
case 19: // call window (or another seat's Pon!/Chi! banner — same event)
case 23: // call options (mirrors 19's [6..8])
{
    var opts = new List<string>(3);
    for (var i = 6; i <= 8; i++)
    {
        var s = f.Str(i);
        if (!string.IsNullOrEmpty(s) && !s.Equals("Pass", StringComparison.OrdinalIgnoreCase))
            opts.Add(s);
    }
```

and `SnapshotBuilder` turns those strings straight into flags:

```csharp
"Pon" => LegalAction.Pon,  "Chi" => LegalAction.Chi,
"Kan" => selfDeclare ? LegalAction.AnKan : LegalAction.MinKan,
"Ron" => LegalAction.Ron,  "Riichi" => LegalAction.Riichi,  "Tsumo" => LegalAction.Tsumo,
```

So **every action except `Discard` and `Pass` exists only as an English string**, from
`AtkValues[6..8]` or from the panel's renderer text (the label-edge fallback). The `[1]`, `[2]`,
`[3]` ints of the same frame — which carry the same information numerically — are read for other
event types but **never for 19/23**. **[observed]**

Two further limits worth recording: `AtkFrame.MaxCopied = 96`, so nothing past index 95 is ever
copied; and `EventTracker.NoteRefresh` logs only `[1..8]`, which is why no one has looked further.
This session's `AtkValuesCount` histogram is **109 ×10749, 388 ×1992, 50 ×1061, 73 ×10** — note
**388**, a size the brief did not mention. **[observed]**

---

## 3. Per action: where availability can be read authoritatively

### 3a. The struct: nothing, and this was tested

A byte-for-byte diff of the two call-prompt fixtures (`emj_struct_round1_call_prompt_pon`,
`emj_struct_round1_call_prompt_2`, both Pon-offered) against the six non-prompt fixtures over the
whole 4352-byte dump finds **exactly one** byte that is constant across both prompts and absent
from every other fixture: `0x0D9E` = 9. That is seat 3's *discard count* — a coincidence of the two
captures being seconds apart in the same round, not an option field. An int32-aligned scan of
`0x0DB8..0x1100` finds **zero** candidates. **[observed]**

`0x0FF0` / `0x0FF8`, which `EMJ_STRUCT.md` marks *plausible* as call-prompt fields, do not
discriminate: **[observed]**

| fixture | `0x0FF0` | `0x0FF8` |
|---|---|---|
| deal_fresh | `0x0222DCECD044` | 102 |
| deal_fresh_settled | `0x0222DCECD044` | 102 |
| **call_prompt_pon** | `0x0222DCECD044` | 102 |
| **call_prompt_2** | `0x0222DCECD044` | 102 |
| midround | `0` | 0 |
| turn1 | `0` | 0 |
| win_screen | `0x0222DCECD194` | 101 |
| round6_melds | `0x0222DCECD194` | 101 |

Non-zero at a deal, a prompt, a win screen and a meld capture alike. Whatever it tracks, it is not
"a call is offered", and it certainly does not distinguish Ron from Chi. **[observed]** It is more
likely a pointer into the tile-descriptor pool for whatever overlay is up. **[inferred]**

Caveat: the fixtures only cover `+0x0000..+0x1100`. The real allocation is larger
(`EMJ_STRUCT.md`: "0x1100–0x6000 is embedded node/tween data that churns every frame"), so a field
above `0x1100` is not excluded — only untested. **[stated uncertainty]**

### 3b. `AtkValues[1..3]` — the numeric option encoding (the recommendation)

On every type-19 and type-23 refresh:

| Index | Meaning | Confidence |
|---|---|---|
| `[1]` | **type-23 only**: number of rows in the call list, Pass included. `0` on type-19. | **[observed]**, 137/137 events |
| `[2]` | option code of **row 0** | **[observed]**, 257/257 events |
| `[3]` | option code of **row 1**, `0` when there is none | **[observed]**, 257/257 events |

Codes, every one seen at least once with its matching English label: **[observed]**

| code | action |
|---|---|
| `0` | no option in this slot (the row is Pass, or does not exist) |
| `1` | Tsumo |
| `2` | Ron |
| `3` | Riichi |
| `4` | Kan |
| `5` | Pon |
| `6` | Chi |

Verification, over `dalamud.log` + `dalamud.old.log` (the full 2026-09-22 session):

- 257 type-19/23 events parsed. Decoding `[2]`,`[3]` through the table and comparing with
  `[7]`,`[8]` after stripping `!` and dropping `Pass`: **257 agree, 0 disagree.**
- On type-23, `[1]` equals (non-zero codes) + 1 in **every** case: `(1)=2` with 1 option ×127,
  `(1)=3` with 2 options ×10.
- No value outside `0..6` ever appeared in `[2]` or `[3]`.
- Holds identically at `n=50`, `n=109` and `n=388`.

Representative lines, verbatim from `dalamud.old.log` (the logger appends `→tile` to any int that
decodes as an icon id):

```
evt type=23 n=50  [1]=3 [2]=2 [3]=6 [4]=0 [5]=1 [6]="Ron!"    [7]="Ron"    [8]="Chi"
evt type=23 n=50  [1]=3 [2]=4 [3]=5 [4]=0 [5]=0 [6]="Pass"    [7]="Kan"    [8]="Pon"
evt type=23 n=109 [1]=2 [2]=1 [3]=0 [4]=1 [5]=1 [6]="Tsumo!"  [7]="Tsumo"  [8]="Pass"
evt type=23 n=109 [1]=2 [2]=3 [3]=0 [4]=0 [5]=0 [6]="Discard" [7]="Riichi" [8]="Pass"
evt type=23 n=109 [1]=2 [2]=6 [3]=0 [4]=0 [5]=0 [6]="Pass"    [7]="Chi"    [8]="Pass"
evt type=388      [1]=2 [2]=4 [3]=0 [4]=3z  [5]=3z [6]="Kan!" [7]="Kan"    [8]="Pass"
```

Note how `[6]` is the **banner** slot, not a row: it is `"Ron!"`, `"Pon!"`, `"Discard"` or even
`"Pass"` depending on the frame, while `[7]` and `[8]` are rows 0 and 1. That inconsistency is one
more reason the string path is fragile and the codes are not. **[observed]**

Why the codes are better than the strings, even though both live in the same array:

- **Locale-independent.** The current switch would silently return `LegalAction.None` for every
  action on a non-English client. `[2]`/`[3]` would not.
- **No sentinel parsing.** No `TrimEnd('!')`, no `"Pass"`-means-absent convention, no banner slot
  to confuse with a row.
- **Self-checking.** `[1]` on type-23 gives an independent row count, so a frame where the codes and
  the count disagree can be rejected instead of trusted.

Limits, stated plainly:

- **Same array, same staleness.** These are `AtkValues`, so they persist after the window closes
  exactly as the strings do, and other event types reuse the slots (on a type-5 draw, `[2]` is the
  seat and `[3]` the drawn icon). The codes are meaningful **only on a frame whose `[0]` is 19 or
  23**, so read them in `EventTracker`'s `case 19/23`, off the event frame — not by polling the
  live array. **[observed]** The existing close rules (type-5/8/13/74/29/32) are unaffected.
- **A third option has no known home.** `[1]` never exceeded 3 (two options + Pass). A four-row
  window — a discard that simultaneously completes a kan, a pon and a chi from kamicha — is
  possible in the rules and **was never observed**, so whether the third code lands in `[4]` is
  **unknown**. Today `[4]` carries a tile icon on some frames (`[4]=76054→4p`) and a small int on
  others, and `[5]` likewise; in a few frames `[4]==[5]==[2]`, in others not. Do **not** read `[4]`
  or `[5]` as an option code. **[stated uncertainty]**
- **No bitmask was found.** Indices `[9..95]` are copied into `AtkFrame` but have never been logged,
  so they are unexamined, not cleared. Indices ≥ 96 are never copied at all, and `n` reaches 388.
  If a bitmask exists it is in that unexamined range. **[stated uncertainty]** Cheapest next step:
  widen `NoteRefresh` to dump `[1..40]` for types 19/23/25 for one session and look for a field
  whose popcount tracks `[1]`.

### 3c. `AtkComponentList.ListLength` — a genuine struct row count

The call list `1/46/104/3` is an `AtkComponentList`, which **is** typed in FFXIVClientStructs:
**[observed]**

```
0x0F0 AtkComponentList.ListItem* ItemRendererList
0x0F8 int  AllocatedItemRendererListLength
0x118 CStringPointer* ItemLabels
0x120 int  ListLength                 ← rows currently in the list
0x134 int  SelectedItemIndex
0x13C int  HoveredItemIndex
0x176 short NumVisibleRows
```

`EmjOperator.ListRows` already dereferences it
(`Math.Min(comp->ListLength, comp->AllocatedItemRendererListLength)`) — but only at click time.
Neither `EmjStateReader` nor `EventTracker` ever reads it. **[observed]**

It corroborates `[1]` independently: across the session the operator resolved `"Pass"` to **row 1
in 88 clicks and row 2 in 7 clicks**, against 127 type-23 frames with `[1]=2` and 10 with `[1]=3`
— i.e. the live item table and the event agree on how many rows exist. **[observed]** The 7-vs-10
gap is the three-row windows that were answered with the call rather than Pass. **[inferred]**

Its limit is the one `EMJ_ADDON_REFERENCE.md` already records: the panel, list, rows and texts are
identical open and closed, so `ListLength` is a good **count** but never an **open/closed** signal.
Also `ItemRendererList[i].Label` is always empty here, so the list cannot name its own rows — the
row *kinds* still have to come from `[2]`/`[3]`.

### 3d. Per-action verdict

| Action | Authoritative source | How to read it |
|---|---|---|
| **Ron** | `AtkValues[2]` or `[3]` == `2` on a type-19/23 frame | in `EventTracker` `case 19/23`, `f.Int(2)`/`f.Int(3)` |
| **Tsumo** | same, code `1` | as above |
| **Riichi** | same, code `3` | as above |
| **Kan** | same, code `4` | as above. Claim vs ankan still needs the existing self-declare test — the code does not distinguish them **[observed: only `[2]=4` seen for both `"Kan!"` (self) and `"Kan"` beside `"Pon"` (claim)]** |
| **Pon** | same, code `5` | as above |
| **Chi** | same, code `6` | as above; the shape chooser stays on type-25 |
| **Pass** | Implicit: it is the last row whenever a window is open. Cross-check `ListLength` == non-zero codes + 1. | no field of its own |

No action has a struct/agent field. For all seven the answer is *`AtkValues`, but the integer
lane rather than the string lane.*

### 3e. The part that really is missing

The codes fix *how* options are decoded; they do not fix *when* the plugin believes a window is
open, because they live in the same persisting array. Nothing found in this survey gives an
authoritative "a call window is on screen right now" bit — not the struct, not `ListLength`, not
`0x0FF0`. `LIVE_ISSUES_2026_09_22.md` §4's proposal — compute `Riichi`/`Tsumo`/`Ron` legality from
the hand we already read out of `0x0DB8` and gate the click on the button being present — remains
the right answer for that half, and it is independent of this one. **[inferred]**

---

## 4. `AtkEventType`, verbatim

`FFXIVClientStructs.FFXIV.Component.GUI.AtkEventType`, underlying type `System.Byte`, complete and
in declaration order as it exists in 7.56.2.9089: **[observed]**

```
MouseDown = 3                    TimerTick = 64
MouseUp = 4                      TimerEnd = 65
MouseMove = 5                    TimerStart = 66
MouseOver = 6                    TweenProgress = 67
MouseOut = 7                     TweenComplete = 68
MouseWheel = 8                   ChildAddonAttached = 69
MouseClick = 9                   WindowRollOver = 70
MouseDoubleClick = 10            WindowRollOut = 71
InputReceived = 12               WindowChangeScale = 72
InputNavigation = 13             TimelineActiveLabelChanged = 74
InputBaseInputReceived = 15      LinkMouseClick = 75
RawInputData = 16                LinkMouseOver = 76
FocusStart = 18                  LinkMouseOut = 77
FocusStop = 19                   UnregisterAll = 83
Resize = 21
ButtonPress = 23
ButtonRelease = 24
ButtonClick = 25
ValueUpdate = 27
SliderValueUpdate = 29
SliderReleased = 30
ListButtonPress = 31
ListItemRollOver = 33
ListItemRollOut = 34
ListItemClick = 35
ListItemDoubleClick = 36
ListItemHighlight = 37
ListItemSelect = 38
ListItemPadDragDropBegin = 40
ListItemPadDragDropEnd = 41
ListItemPadDragDropInsert = 42
DragDropBegin = 50
DragDropEnd = 51
DragDropInsertAttempt = 52
DragDropInsert = 53
DragDropCanAcceptCheck = 54
DragDropRollOver = 55
DragDropRollOut = 56
DragDropDiscard = 57
DragDropClick = 58
IconTextRollOver = 59
IconTextRollOut = 60
IconTextClick = 61
DialogueClose = 62
DialogueSubmit = 63
```

The seven names the brief asked about, exactly as spelled here:
**`MouseOver` = 6**, **`MouseOut` = 7**, **`MouseDown` = 3**, **`MouseUp` = 4**,
**`MouseClick` = 9**, **`ButtonClick` = 25**, **`ListItemClick` = 35**.

`MouseOut = 7` **does exist** in this version, so the `MouseOut` pairing proposed as fix (b) for
the `AgentEmj.Update` crash is available and needs no new constant. There is no `ButtonUp` or
`ButtonDown`; the button trio is `ButtonPress = 23` / `ButtonRelease = 24` / `ButtonClick = 25`.
For hover on a list row, the pair is `ListItemRollOver = 33` / `ListItemRollOut = 34`, not
`MouseOver`/`MouseOut`. **[observed]**

---

## 5. Recommended change, concretely

In `EventTracker` `case 19: case 23:`, build the option list from the codes and keep the strings as
a fallback only:

```csharp
static string? OptionName(int code) => code switch {
    1 => "Tsumo", 2 => "Ron", 3 => "Riichi", 4 => "Kan", 5 => "Pon", 6 => "Chi", _ => null,
};

var opts = new List<string>(3);
if (f.IsInt(2) && OptionName(f.Int(2)) is { } a) opts.Add(a);
if (f.IsInt(3) && OptionName(f.Int(3)) is { } b) opts.Add(b);
if (opts.Count == 0) { /* existing [6..8] string scan, as a fallback */ }
// type-23 self-check: f.Int(1) should equal opts.Count + 1
```

Keep the existing open/close rules unchanged — this changes only *what the options are*, not *when
the window exists*. Log a line whenever the codes and the strings disagree; on this session's data
that line would never have fired, so it is a cheap regression alarm for a future patch.

`SnapshotBuilder`'s string switch can stay as-is, or take an enum directly; either way the English
dependency disappears from the authoritative path.

---

## 6. Correction to `LIVE_ISSUES` §3 — the dropped Ron was a phantom

`LIVE_ISSUES_2026_09_22.md` §3 concludes that "a second event replaces the window's options and
drops Ron", and proposes taking the **union** of every option set seen while a window is open. The
log does not support that, and the fix would be actively harmful.

Scanning both logs for any type-19/23 that removed `Ron` or `Tsumo` from a set established by an
earlier 19/23 **within the same uninterrupted window** (resetting on type-5/8/13/21/29/32):
**0 occurrences.** **[observed]** Combined with the 257/257 code-vs-string agreement, no event ever
dropped a win option.

What actually happened at 16:25, in full:

```
16:24:40.223 evt type=23 n=388 [1]=2 [2]=2 [3]=0 [6]="Ron!" [7]="Ron" [8]="Pass"   ← real Ron
16:24:43.869 op call "Ron" → "Ron" node 2: row 0: ListItemClick index=0 param=0 →addon
16:24:48.039 evt type=32 [2]="East 3 West Wind" [5]="Called Ron" [6]="40 Fu 3 Han" ← we won
...
16:25:05.034 call window (label edge): [Ron] tile=2m claim=True        ← panel text RESIDUE
16:25:05.039 evt type=23 n=388 [1]=2 [2]=6 [3]=0 [6]="Pass" [7]="Chi" [8]="Pass"   ← Chi only
16:25:05.039 call window (type-19): [Chi] tile=2m claim=True           ← correct
16:25:07.268 op call "Pass" → "Pass" node 21001: row 1                 ← row 1 of 2
```

The Ron the plugin "lost" is the label-edge fallback reading the panel text left over from the Ron
it had won **22 seconds earlier**, on a hand that had already ended. The type-23 that "dropped" it
is the authoritative frame and it says two rows, Chi and Pass — which `[1]=2`, `[2]=6`, the
renderer strings **and** the operator's own row resolution (`"Pass"` → row 1, not row 2) all agree
on. **[observed]** 16:38 is the same pattern: the real Ron was at 16:35:40 (`[1]=3 [2]=2 [3]=6`,
taken at row 0); the 16:38:03 `[Ron,Chi]` label edge is its residue; the type-23 says `[1]=2 [2]=6`,
Chi only, and Pass again resolved to row 1.

**Implication.** Unioning the sets would have had the plugin try to click a Ron row that is not in
a two-row list, on a hand where Ron is not legal. The real defect is the opposite one: the
label-edge fallback invents windows from stale text. `EMJ_ADDON_REFERENCE.md` already warns that
the panel texts "persist after every prompt … so they can only ever be a fallback *open* edge,
never a close signal" — this is that warning coming true. Two changes follow, both cheap:

1. **The event wins.** When a type-19/23 arrives for a window opened by labels, *replace* the
   option set (current behaviour, and correct) and additionally clear the
   `CallWindowFromLabels` flag only if the event agrees. Never union.
2. **Kill the residue.** Discard a label-edge window whose options are not confirmed by a type-19/23
   within ~200 ms; in this session every genuine window had its event within 5 ms. **[observed]**

The genuinely missed wins are the §4 problem, not the §3 one: a tenpai hand that is never offered
anything, because the window is never detected at all. Fixing §3 as written would not have
recovered a single Ron in this session; §4's hand-derived legality would.

---

## 7. Uncertainties, explicitly

- **Untested struct range.** The fixtures cover only `+0x0000..+0x1100`. A call-option field above
  `0x1100` is untested, not ruled out. The region is documented as per-frame node/tween churn,
  which makes it unlikely but not impossible.
- **Only Pon prompts are fixtured.** Both call-prompt dumps are Pon. Even a field that flags "a
  prompt is open" generically could not have been distinguished from "Pon is offered". New
  fixtures at a Ron prompt and a Riichi prompt would settle both at once.
- **`AtkValues[9..95]` unexamined.** Copied but never logged, so a bitmask there has not been ruled
  out. Indices ≥ 96 are never copied; `n` reaches 388.
- **Four-row windows never observed.** Whether a third option code lands in `[4]` is unknown, and
  `[4]`/`[5]` demonstrably carry other things on other frames.
- **Kan is one code for two actions.** `[2]=4` was seen for both the self-declare `"Kan!"` and the
  claim `"Kan"` next to `"Pon"`; nothing in the codes separates ankan / shouminkan / daiminkan.
  `SnapshotBuilder`'s existing `selfDeclare` test still has to do that.
- **Single client, single version.** Everything is EU, Dalamud 15.0.3.5, FFXIVClientStructs
  7.56.2.9089, one player, 2026-09-22. The code table has never been checked on another locale —
  which is the one place it could plausibly differ from the strings and the thing most worth
  confirming if a non-English client is ever available.
- **The real `AgentEmj` was not inspected.** This survey read the DLL, the fixtures and the logs;
  no live process was attached. The claim that `AgentEmj` has no option field rests on
  `EMJ_STRUCT.md`'s earlier live observation ("`AgentEmj` (0x60 bytes) points only at UI-module
  objects"), not on anything measured here.
- The §6 reconstruction reads the operator's `row N` output as proof of the live row count. That is
  `EmjOperator.ListRows` reading `AtkComponentList.ItemRendererList`, which is live state, so the
  inference is strong — but it is an inference from a log line, not a direct `ListLength` capture.
