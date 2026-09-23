# The table stops taking mouse input — measured live, 2026-09-23

The mahjong table goes unhoverable and unclickable while the addon itself keeps working, so
auto play is unaffected and manual play is impossible. Everything here was read off the live
client **while it was happening** (04:43–05:01 UTC / 06:43–07:01 local), via Cartographer and
the plugin's own `[Input]` logging. The raw node tree from the stuck moment is kept in
`artifacts/capture/tree_Emj_STUCK_STATE.txt` (gitignored).

The cause is still open. What follows is what is ruled out, what is measured, and the one
outstanding lead.

## The addon's real input protocol (read off the game's own traffic)

29 distinct `FireCallback` signatures were recorded while a human played. Grouped by the
head value, which is the command selector:

| head | args | meaning | evidence |
|---|---|---|---|
| **7** | slot index | **discard** | 9 distinct slots; each followed by the game's own type-8 discard of that tile |
| **11** | row index | **call-window row** (Pon / Chi / Pass) | `11,1` and `11,2`, each followed by the window closing |
| **15** | tile icon id | **pointer is on this tile** (hover handshake) | `15,76057`, `15,76064`, `15,76068`, each matching a type-30 tooltip in the plugin log at the same millisecond |
| 9 | — | fired once, at match start | unidentified |
| 14 | — | fired once | unidentified; the AutoMahjongSolver research notes report `[14]` as a *notification*, not a command — do not replay without capture evidence |

**This is the point of the rework.** A real discard is `[15, icon]` then `[7, slot]`. The
plugin instead synthesises an `AtkEvent` ButtonClick at a node, which produces `[7, slot]`
with the pointer state never updated. We have been simulating a mouse when the addon has a
documented command channel.

## The stuck-UI state, measured

`tree_Emj_STUCK_STATE.txt` in this folder is the full node tree captured **while the table
was refusing all mouse input**. Alongside it, from the same moment:

- Focus list: **empty** (`focus=[none]`), table visible. An addon regains focus by being
  clicked, and clicks are routed by hit-testing — a deadlock.
- Addon: visible, ready, 330 nodes, 677 refreshes, agent alive.
- All 14 hand slots present at their correct screen coordinates, each with a **visible
  collision node** (42x55) and live `MouseOver`/`MouseOut`/`ButtonClick` chains. 95 nodes in
  total still carried hover chains.
- Nothing covering it: no modal, no ghost window. `ContextMenu` and `Talk` were open but
  self-hidden (`ShowHideFlags` bit 0), which is their normal resting state all match.
- Dalamud's ImGui layer was **not** capturing the mouse.
- The plugin's own `ButtonClick` dispatches kept landing throughout — which is exactly why
  auto play is unaffected and manual play is impossible.

So the addon was entirely healthy and the exclusion was happening above it, in the game's
input routing.

### Onset

```
06:43:18–06:43:57   plugin discards x7 (auto play)
06:44:01.694        auto play OFF
06:44:03.388        HOVER "Bamboo (6)"      <- hover works, 6 s after the plugin's last click
06:44:07.174        manual discard of 1z    <- clicking works
06:44:11.918        HOVER "Dots (8)" (76057)   <- LAST HOVER EVER
06:44:12.624        manual discard of 8p    <- the tile under the cursor
                    --- hover dead from here ---
06:45:53.113        auto play OFF (the user notices)
```

The plugin was idle at onset and the UI demonstrably worked after its last action. That does
not rule out a latent condition set earlier, and n=1. Note also that only **3 hover events
exist in the entire log**, so there is no sample of hover working *during* auto play — a gap
the capture should close by resting the pointer over the hand while auto play runs.

`MouseOver param=12` received its `MouseOut`; params 13 and 7 never did.

## Probes that did NOT recover it

- `WindowRollOver(70)` on the root node — delivered (`AddonReceiveEvent` recorded), focus
  unchanged.
- `MouseOut param=7` on the stuck slot — delivered, focus unchanged.
- `AtkStage` exposes `GetFocus` / `SetFocus` / `ClearFocus`; FFXIVClientStructs binds
  `GetFocus` and `ClearFocus` but **not** `SetFocus`, which would need an address-based call.
- AtkStage's first 512 bytes showed no changes over 10 s.
