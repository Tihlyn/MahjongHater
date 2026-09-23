# The Emj input protocol, measured — 2026-09-23 (stage 1)

A full NPC match played manually with auto play off, recorded by Cartographer. Every line
below is read off the game's own traffic; nothing is inferred from behaviour. Raw capture in
`artifacts/capture/` (`cb_*.json`, `timeline.json`, `lay_*.json`, `snap_*.json`).

10 hands, 5,424 timeline entries, 282 callbacks, 277 distinct signatures.

## The command channel

The addon's real input channel is `FireCallback`, not synthetic `AtkEvent`s. Each command is
`[head, args…]`. Pairing every callback with the event in the same millisecond identifies
which are **inputs** (a user action produced them) and which are **notifications** (the addon
fired them itself) — the distinction that matters, because replaying a notification as a
command is how the AutoMahjongSolver got stuck in state 32.

| head | args | meaning | paired event | fires |
|---|---|---|---|---|
| **7** | slot 0–13 | **discard** | `ButtonClick param=slot+15 node=9` | 96 |
| **7** | slot | *game discarding for you* (riichi auto-tsumogiri) | **none** | 4 |
| **11** | row index | **call-window row** (Pon/Chi/Kan/Ron/Riichi/Pass) | `ListItemClick param=0 node=3` | 22 |
| **12** | shape index | **pick a chi shape** (state 25) | `ButtonClick param=9+i node=5+i` | 1 |
| **14** | — | **round-recap "Next"** | `ButtonClick param=7 node=97` | 9 |
| **15** | tile icon id | **pointer is on this tile** | **none** (117 of 118) | 118 |
| 9 | — | hand start | `TimelineActiveLabelChanged param=33 node=128` | 10 |
| 17 | — | hand end → score screen | `TimelineActiveLabelChanged param=36 node=54` | 10 |
| −1 / −2 / 0 | — | close / dismiss | mixed | 5 |

**Inputs we may replay:** `7`, `11`, `14`. **Do not replay:** `9`, `17`, `−1`, `−2` — the
addon fires those itself on state transitions.

`15` is special: it fires **without any AtkEvent**, 117 times out of 118. The addon polls the
cursor and reports the tile under it; it is not a response to a `MouseOver` we could
synthesise. A real discard is therefore `[15, icon]` **then** `[7, slot]`, while the plugin
produces only `[7, slot]` via a synthetic ButtonClick, leaving the addon's idea of the
pointed-at tile untouched. That asymmetry is the strongest lead on the stuck-hover bug.

## End of match — our implementation was fiction

```
05:42:40.476  Emj callback [17]                              hand ends
05:42:40.476  Emj refresh -> state 29 (score)
05:42:42.853  Emj callback [14] + ButtonClick param=7 node=97   recap "Next"
05:42:44.392  Emj PostHide  ->  EmjTotalResult PostShow/PostOpen
05:43:03.203  EmjTotalResult ButtonClick param=0 node=26      <- the control that ends it
              EmjTotalResult callback [-1] then [-2]
05:43:03.203  EmjTotalResult PostHide / PostClose
05:43:03.286  Emj callback [-2] -> PostClose -> PreFinalize
```

- The match-ending control is **`EmjTotalResult` node 26, ButtonClick param 0**. There is no
  `"End match"` button in the `Emj` addon, which is where the code searched for the English
  string.
- **No `SelectYesno` at match end.** The only one in the whole capture was the *start*-of-match
  NPC prompt: *"This table is for novices. Would you like to challenge Spriggan, Moogle, and
  Mandragora to a game of mahjong?"* — Yes is callback `[0]`, then `[-1]` to close. The
  confirmation handling written for the end of a match answers a dialog that does not appear
  there, and the old blind-Yes would have answered *this* one.
- `EmjRankResult` never appeared in an NPC match (no callbacks, absent from the timeline); the
  ranked flow showed it after `EmjTotalResult` in earlier sessions, so it is rank-only.

## Assumptions this capture falsified

| Assumed | Measured |
|---|---|
| `"End match"` is a labelled button in `Emj` | it is `EmjTotalResult` node 26, param 0 |
| the end of a match raises a `SelectYesno` | it does not; the only one is at match *start* |
| state code `12` = riichi (`resources/layouts/emj.json`) | state 12 refreshes **140+ times per match**, every few seconds — it is a routine refresh, not riichi |
| node 97 is the recap Next (by trial and error) | **confirmed**, and it emits callback `[14]` |
| discards need a synthetic `ButtonClick` | `[7, slot]` is the command; the game itself fires it with no event at all during riichi |

## Player-observed behaviour worth recording

- **After riichi the game discards for you.** Four `[7, slot]` callbacks fired with no
  `ButtonClick` at 05:36:04, 05:36:20, 05:37:19, 05:37:56 — a riichi hand in that window.
  Normal game behaviour, not a plugin action, and it explains "tiles were played without my
  input while auto play was off".
- **Clicking the drawn tile while Tsumo is offered discards it and forfeits the win**, leaving
  you furiten (observed once; the other tsumo was declared from the call list). The win must
  be answered through the call list (`[11, row]`), never by touching the hand. This is direct
  support for taking an offered win on a confirmed window rather than re-judging it.
- **The round recap has hoverable elements listing the individual han.** Not yet captured —
  a reading opportunity that would let the scoring oracle verify our `YakuDetector` yaku by
  yaku, instead of only fu/han totals.

## The round recap carries the game's own scoring, in full

State 29 (`AtkValues[0] == 29`) is the end-of-round recap, and its values hold everything the
game used to score the hand. **No hovering is needed** - the hoverable tooltips on screen are
just these strings; they are already in the value array. Captured live on 2026-09-23:

| index | type | meaning |
|---|---|---|
| 0 | Int | state code, 29 |
| 1–4 | Int | per-seat score delta / 100 (`26` = +2,600) |
| 5 | ConstString | how it was won (`"Called Ron"`) |
| 6 | String | `"40 Fu 2 Han"` — the total already used by the scoring oracle |
| 14 | UInt | number of tiles in the hand array |
| 24 … 24+n-1 | Int | the winner's hand, tile icon ids |
| 41 | Int | the winning tile |
| **42** | UInt | **yaku count** |
| **43 + i** | String | **yaku name** (18 slots) |
| **61 + i** | String | **that yaku's han** (18 slots) |
| **79 + i** | ConstString | **that yaku's description** (18 slots) |
| 97 / 98 | UInt / Int | dora indicator count and tile |
| 103 / 104 | UInt / Int | ura dora count and tile |

Three parallel 18-slot arrays behind a count: `43 + 18 = 61`, `61 + 18 = 79`, `79 + 18 = 97`,
and index 97 is the next field. The observed hand decodes to a valid winning shape —
`1m1m 678m 999m 456p 23s` completed by `1s`, scored `Riichi` + `Ura Dora` = 2 han 40 fu —
which is what confirms the mapping rather than an assumed offset table.

Note the game lists **dora as named entries** in the yaku array (`"Ura Dora"` /
*"Awards bonus han but is not a yaku by itself."*), while our `YakuDetector` counts dora
separately, so a comparison has to account for that.

This turns every win in every match into a verification of the whole scoring path: the
winner's actual hand (against our hand read, at the exact moment our read matters most),
the yaku list **yaku by yaku** with per-yaku han, the fu/han total, and the dora counts.
Yesterday's oracle could only check the payout table.

## The chi shape chooser, measured 2026-09-23

Captured live while the chooser was open. Accepting Chi from the call list refreshes the addon
into state 25, whose values carry the offered shapes exactly as `EventTracker` already decodes
them — `[3]` = shape count, then `[4+4i]`..`[6+4i]` tiles with a `76041` placeholder at
`[7+4i]`. The observed chooser offered **3s4s5s** and **4s5s6s** on a claimed 4s, both using a
red 5s (`76077`), which is what the existing decode produced.

```
06:25:44.274  callback [11,0] + ListItemClick param=0 node=3   accept Chi from the call list
06:25:44.280  refresh -> state 25                              the chooser opens
06:28:26.530  callback [12,0] + ButtonClick param=9 node=5     pick shape 0
06:28:26.536  refresh -> state 10 -> 20                        the meld is made
```

The panel is node `1/46/52`: shape *i* is child `5+i` with `ButtonClick param=9+i`, and the
cancel button is child `11` with `param=8`. Buttons past the offered count are hidden. Those
are the paths the layout already carried (`chiShapeButtons`, `chiShapeCancel`) — now confirmed
against the live chooser instead of assumed, along with the callback each produces.

Still unmeasured: the callback the **cancel** button emits (no cancel was performed).

## Gap

None outstanding from the original action list.
