# Addon interaction rework, 2026-09-22 — what was implemented

Implements the reviewed findings of [the call-window audit](CALL_WINDOW_AUDIT_2026_09_22.md)
and stages 1–3 of [the integration proposal](ADDON_INTERACTION_PLAN_2026_09_22.md). Those two
documents stay as the research and the open questions; this one records what the code now
does, what is deliberately left off by default, and what still needs a live session.

The goal is narrow and testable: **an operate call either dispatches at a target it has just
verified, or it refuses and says which guard refused.** No synthetic clicks, no row 0, no
dispatch at a control the game is not showing, and no "success" that only means an event was
sent.

## Rules the code now enforces

| Rule | Where | Defect it closes |
|---|---|---|
| Full ancestor chain must be visible, not just the matched node | `EmjOperator.IsChainVisible`, used by every entry point | `ClickByLabel` checked the text node alone; the 18:27:48 recovery dispatched Pass to a list the stall dump had already logged hidden |
| A call row is resolved from the live item table, by exactly one matching label, with a renderer and enabled | `CallRowResolver.Resolve` + `EmjOperator.Rows` | `ListRendererIndex` fell back to renderer state and finally to **row 0**, turning "target not found" into "click the first row" |
| The list's rows must still carry the options the open window advertises | `CallRowResolver.Resolve` | Stale rows (`Ron`, `Pass`) outliving the window the tracker has open (`Chi`) |
| Only an event-confirmed window may be answered | `EmjActuator.AnswerCall` | Label-only windows are panel residue; answering one fires a list click at a closed list |
| List events get a zeroed, list-shaped payload with a renderer from the item table | `EmjOperator.SelectRow` | `AtkEventData` is a union: `MouseData.PosX/PosY` overlap `ListItemData.ListItemRenderer` at offset 0, so a mouse payload leaves **screen coordinates in a pointer field** whenever the renderer is missing |
| Every diagnostic scalar is copied before `ReceiveEvent` | `EmjOperator.FireChain`, `SelectRow`, `EmjActuator.DiscardTile` | Event type, chain param and node ids were read *after* dispatch, i.e. after a handler was free to rebuild the tree |
| Hover is a matched pair on one holder or it is skipped | `EmjOperator.HoverAndRelease` | `MouseOver` and `MouseOut` were found independently, and a missing `MouseOut` was synthesised against the addon even when `MouseOver` had gone to a component |
| The answer names the window it was aimed at | `EventTracker.CallWindowGeneration` + `MarkCallAnswered(isWin, generation)` | A handler can open the next prompt *inside* `ReceiveEvent` (a Chi row raising its type-25 chooser); clearing "the current window" then discarded the prompt actually on screen, and a failed Ron still set `WinDeclared` |
| A recovery discard requires `LegalAction.Discard` | `AutoPlayer.RecoverDiscard` | During the 18:27 stall all 13 slots had a registered activation while the snapshot said `legal=None` on another player's turn — and recovery discarded anyway |
| Dispatch and acceptance are different outcomes | `OperateResult.Status`, `AutoPlayer.PendingDispatch` | `result.Ok` meant "an event was sent"; the journal reported ignored clicks as successes |
| Label matching is for buttons only, and ambiguity is a rejection | `EmjOperator.FindButtonByLabel` | Pooled panel text could resolve to any of several copies, and a list row could be clicked as if it were a button |

## Defaults, and what is opt-in

- **Click style is `Activation`** and is now reset to it at every match boundary. Before, the
  one escalation to `HoverCycle` had no assignment back and held until the assembly reloaded.
- **Hover escalation is off** (`Configuration.HoverEscalation`, Diagnostics → Addon
  interaction). When on, it requires that a **node activation** was really delivered and the
  game then did not move; a rejected dispatch or an unanswered list row can no longer change
  the click style, because neither says anything about how clicks are sent.
- **Call rows use the registered `ListItemClick`** — the route verified in the 2026-07/09
  sessions. `Configuration.NativeListSelection` switches to
  `AtkComponentList.SelectItem(index, dispatchEvent: true)` for comparison (proposal stage 3).
  It is read every frame, so a comparison needs no reload. Nothing about it is verified here.
- **Row enabled state is read leniently**: a row counts as disabled only when the list's
  disabled flag and the renderer's button agree. Neither reading has been checked against a
  live Emj call list; a wrong "disabled" would refuse every call, while a wrong "enabled"
  only produces a dispatch the game ignores — which is now observed and logged.

## What the logs will show

`Diagnostics → ADDON INTERACTION` shows the click style, the list route and one line for the
last action: *dispatched, waiting for the game* → *accepted after N ms*, or *refused: …* with
the guard's reason. The stall dump gains the call list's chain visibility, per-row renderer
and enabled state, the window generation and whether the window is label-only.

Refusals are warnings in the Dalamud log and journal lines in the overlay. **Expect to see
some**: that is the change. A refusal that repeats every turn is a bug in a guard, not in the
game, and names itself.

## Coverage

`MahjongHater.Tests/Operate/CallRowResolverTests.cs` (12 cases) covers the resolution rules:
hidden list, stale rows, missing renderer, disabled row, missing label (no row-0 fallback),
duplicate labels, no window, an option the window does not offer, the `!` banner suffix, and
Pass being answerable without being an advertised option.
`EventTrackerTests` adds the generation rules, including the ordering the audit asked for —
a type-25 chooser that opens *before* the answer is marked (the reverse of the existing
mark-then-type-25 test), and a superseded win answer that must not set `WinDeclared`.

Unit tests cannot establish anything about the native client. 448 tests pass.

## Still open — needs a live session

1. **Does the guarded path still play a match cleanly?** Every call now has preconditions
   that have never run against the live addon. First run should be attended: watch for
   refusals on ordinary Pon/Chi/Riichi/Ron windows, which would mean a guard reads something
   differently than assumed (the row enabled state is the most likely candidate).
2. **The crash rate.** Unchanged from the previous build's expectation: three crashes in 376
   clicks was the baseline, so only a long unattended run says anything.
3. **The 18:26:58 stall itself.** None of this explains it. The audit's conclusion stands:
   the last discard was acknowledged, no call window was being answered, and the cause is
   still open. What changed is that recovery can no longer dispatch into a dead UI, and that
   a dispatch the game ignores is now visible instead of silent.
4. **Native list selection**, if it is ever to be adopted, needs the capture comparison in
   the plan's stage 1 — not a toggle flipped on a hunch.
