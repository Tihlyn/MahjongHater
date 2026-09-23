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

## Gap

The **chi shape chooser** (state 25) did not occur: no chi offered more than one shape all
match. Its row protocol is the one input still unmeasured. Everything else in the action list
was exercised.
