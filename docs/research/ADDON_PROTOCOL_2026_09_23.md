# The Emj input protocol, measured — 2026-09-23 (stage 1)

A full NPC match played manually with auto play off, recorded by Cartographer. Raw capture in
`artifacts/capture/` (`cb_*.json`, `timeline.json`, `lay_*.json`, `snap_*.json`).

10 hands, 5,424 timeline entries, 282 callbacks, 277 distinct signatures.

> ## Correction — 2026-09-23, after the interaction audit
>
> **The counts and one finding in the original version of this note were wrong.** They were
> produced by pairing each callback with events in the same *millisecond*. The capture carries
> a frame number, and pairing by frame changes the result:
>
> | head | published | actual (frame-paired) |
> |---|---|---|
> | 7 discard | 96 paired + **4 with no event** | **100 paired, 0 unpaired** |
> | 11 call row | 22 | 28 in `timeline.json`, 30 fires in `cb_Emj.json` with repeats |
> | 14 recap next | 9 | 10 |
> | 15 pointer | 1 paired / 117 not | 3 paired / 115 not |
> | 10 | *(absent)* | 1 fire, unidentified |
>
> The four "event-free" discards each have a `ButtonClick param=slot+15 node=9` between 23 and
> 51 µs away, on the far side of a millisecond boundary:
>
> ```
> cb 05:36:04.8419588 [7,13]  ev 05:36:04.8420099 param=28  -51us   841 -> 842
> cb 05:36:20.2579840 [7,13]  ev 05:36:20.2580311 param=28  -47us   257 -> 258
> cb 05:37:19.8059571 [7, 3]  ev 05:37:19.8060069 param=18  -49us   805 -> 806
> cb 05:37:56.5879954 [7, 9]  ev 05:37:56.5880189 param=24  -23us   587 -> 588
> ```
>
> **Consequence.** The sentence *"the game itself fires `[7, slot]` with no event at all, which
> is the clearest proof that the callback is the command"* has no evidence behind it. Every
> input command in this capture arrived with an event. The capture establishes each command's
> **payload**; it establishes nothing about whether replaying the callback alone is sufficient.
>
> Bare `[7, slot]` *is* sufficient — v2.3.0 discards through it for whole matches — but that is
> known from **live play**, and this note is not where it was learned. The player-observed
> "after riichi the game discards for you" entry below is likewise unsupported by the capture;
> those four discards look exactly like the other 96.
>
> Methodology this note should have followed, and now does: separate **source facts** (bytes in
> the capture) from **correlations** (what accompanied what) from **tested postconditions**
> (what an action actually did). "Observed during a successful human action" is a starting
> point, not proof that replaying that one operation reproduces the action.
>
> Verified independently against the same artifacts (SHA-256 `timeline.json`
> `db480ca9…745ef5b0`, `cb_Emj.json` `1711e8ab…dfe7f9160c`), audit in
> `DOMAN_UI_AUDIT_2026_09_23.md`.

## The command channel

The addon's real input channel is `FireCallback`, not synthetic `AtkEvent`s. Each command is
`[head, args…]`. Pairing every callback with the event in the same millisecond identifies
which are **inputs** (a user action produced them) and which are **notifications** (the addon
fired them itself) — the distinction that matters, because replaying a notification as a
command is how the AutoMahjongSolver got stuck in state 32.

Counts are frame-paired (see the correction above). "unpaired" means no addon-bound event in
the same frame.

| head | args | meaning | paired event | fires | unpaired |
|---|---|---|---|---|---|
| **7** | slot 0–13 | **discard** | `ButtonClick param=slot+15 node=9` | 100 | 0 |
| **11** | row index | **call-window row** (Pon/Chi/Kan/Ron/Riichi/Pass) | `ListItemClick param=0 node=3` | 28 | 0 |
| **12** | shape index | **pick a chi shape** (state 25) | `ButtonClick param=9+i node=5+i` | 1 | — |
| **14** | — | **round-recap "Next"** | `ButtonClick param=7 node=97` | 10 | 0 |
| **15** | tile icon id | **pointer is on this tile** | — | 118 | **115** |
| 9 | — | hand start | `TimelineActiveLabelChanged param=33 node=128` | 10 | 0 |
| 17 | — | hand end → score screen | `TimelineActiveLabelChanged param=36 node=54` | 10 | 0 |
| 10 | — | **unidentified** | — | 1 | — |
| −2 | — | close | — | 1 | — |

**Inputs we may replay:** `7`, `11`, `14`. **Do not replay:** `9`, `17`, `−1`, `−2` — the
addon fires those itself on state transitions — and `10`, which we simply cannot account for.

Every one of those input commands arrived **with** an event. The capture therefore fixes each
command's payload and says nothing about a bare callback's sufficiency; that question is
answered by live play, not here.

`15` is the exception that is genuinely unpaired: 115 of 118 fires have no event behind them.
The addon polls the cursor and reports the tile under it, so it is not a response to a
`MouseOver` we could synthesise. A real discard is `[15, icon]` **then** `[7, slot]`; ours
sends only `[7, slot]`, leaving the addon's idea of the pointed-at tile untouched. That
asymmetry is real. Whether it has anything to do with the stuck-hover bug is **unknown** — the
earlier claim that it was "the strongest lead" rested on the retracted finding above plus a
single correlated incident.

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
| discards need a synthetic `ButtonClick` | ~~the game fires it with no event during riichi~~ **retracted, see the correction above.** `[7, slot]` alone does work, established in live play |

## Player-observed behaviour worth recording

- ~~**After riichi the game discards for you.** Four `[7, slot]` callbacks fired with no
  `ButtonClick`…~~ **Retracted.** All four carry a `ButtonClick param=slot+15 node=9` in the
  same frame, identical to the other 96. The player's report that "tiles were played without my
  input while auto play was off" is still a real observation, but *this capture does not
  contain its signature* and nothing here explains it. Left open.
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

## Gaps

Rewritten after the audit. "None outstanding" was only true of the original action list, which
was itself built on the mistaken reading above.

- **Bare-callback sufficiency per command.** Known for `[7, slot]` from live play. **Unknown**
  for `[11, row]`, `[12, shape]` and `[14]` — they work in live play too, but no experiment has
  distinguished "the callback did it" from "the callback plus whatever else we do did it".
- **Chi shapes.** One sample, index 0, one chooser. No other index, and **no cancel** — the
  cancel callback is still unmeasured, so `ChiCancelParam = 8` is a layout path, not a
  measured command.
- **`head 10`.** One fire, `updateState=true`, 06:11:03.229. Unidentified.
- **`EmjRankResult`.** Never loaded in this NPC capture. Reusing `ResultCloseNodeId = 26` for
  it is an assumption, not a measurement.
- **The stuck table.** No capture yet with same-frame pointer coordinates, expected tile
  bounds, collision state, modal ownership and ImGui capture. Until then the `[15]` theory is
  one of several, and the `/mhater focus` output is evidence rather than a diagnosis.
- **Client language / addon variants.** One `Emj` layout on one English client. Row resolution
  and wind parsing both go through English strings.
