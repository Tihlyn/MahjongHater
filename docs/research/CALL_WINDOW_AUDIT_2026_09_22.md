# Call-window and hover audit, 2026-09-22

Read-only runtime audit of commit `4755867` (released as v2.0.2.1). This report is
separate from the anti-idle implementation and proposes interaction changes for review.

## What the reported freeze establishes

The user confirms that chat, other players' play, and leaving the match worked while
only their local mahjong table stopped updating. This narrows the symptom to the
local EMJ presentation/state path; it does not identify its native cause. The plugin
cannot explain this as merely a slow analysis result or a missing recommendation.

The recorded 18:26:58 event silence remains the best available matching incident,
although the user cannot independently supply its timestamp. The last discard was
acknowledged. There was no hover escalation in that match. A successful Chi at
18:25:37.962 was followed by the accepted meld at 18:25:38.084, and Ron at
18:26:20.113 was followed by a new round. No call window was being answered at the
onset. Therefore these logs do not support blaming HoverCycle or a missed list
selection for that incident. Delayed native side effects remain unexcluded.

## The actual interaction paths

1. `EmjStateReader` copies addon AtkValues on PostRefresh; `EventTracker` interprets
   types 19/23 as options and type 25 as a separate Chi-shape chooser. Labels are a
   temporary fallback. `SnapshotBuilder` derives phase and legal actions from this
   tracker state plus the decoded hand.
2. `AutoPlayer` waits for a fresh analysis and a 2–4 second action delay, then calls
   `EmjActuator.Execute`. Calls use `ClickLabel`; a Chi-shape choice or cancellation
   uses the configured chooser button path.
3. `ClickByLabel` searches the entire addon node pool for matching text. If the
   owning component belongs to a list, it uses the item table to select a row, then
   calls `SelectListItem` directly. Otherwise it calls `ClickNode`.
4. `SelectListItem` sends the list's registered ListItemClick to its listener with
   row data. **It does not inspect ClickStyle and never executes HoverCycle.**
   The ordinary call-list path is consequently identical in both styles.
5. `ClickNode` executes HoverCycle only for tile/button paths, including the
   Chi-shape chooser. After any reported successful non-discard dispatch, the
   actuator immediately marks the tracker window answered.

## Confirmed code defects and their limits

### The hover escalation does not diagnose a failed click

At `Core/Operate/AutoPlayer.cs:194–212`, an unchanged analysis fingerprint after six
seconds switches the static style to HoverCycle. It does not require that the
previous operation successfully dispatched anything. A missing target or a
noninteractive tile can trigger the same switch. A failed list action cannot gain
anything from it because list selection bypasses HoverCycle; instead subsequent
tile/chooser/recap actions change behavior. There is no assignment restoring
Activation outside its static initializer, so the switch persists across matches
until the assembly is reloaded, rather than being reset at a match boundary.

This is a confirmed control-flow problem, not evidence the fallback ran in the
recorded frozen match.

### MouseOver and MouseOut are not guaranteed to form a pair

`Core/Operate/EmjOperator.cs:48–57` independently finds each event anywhere in the
subtree. Selection ranks visibility and addon listeners but does not require equal
holder, listener, target or parameter. A registered MouseOut on another component
can be paired with an addon-bound MouseOver. If no MouseOut is found, the code
synthesizes it against the addon even when MouseOver was delivered to a component.
The code therefore cannot guarantee its comment's claim that hover is cleared.

Even before activation, MouseOver itself can synchronously invoke addon code and
refresh UI state. The subsequent walk and the fallback's `over->Param` access reuse
old pointers without a generation/lifetime check. This is a lifetime hazard; logs
do not demonstrate that a node was destroyed in this interval.

### List payload can contain a coordinate value where a pointer belongs

`Core/Operate/EmjOperator.cs:93` initializes list data with `BuildMouseData`.
`AtkEventData` is a union: MouseData.PosX/PosY overlap the ListItemRenderer pointer
at offset zero. The pointer is overwritten only if the supplied renderer node is
a component. If the renderer is missing, the coordinates remain as a non-null
native pointer. The generic `ClickNode` ListItemClick branch at line 63 also uses
`FireChain`'s mouse payload without constructing any list fields.

The normal observed list clicks supplied a valid renderer, overwriting the
coordinate bytes. This conditional payload defect is not proof of corruption in
those successful clicks. Use a zeroed list-specific payload and reject missing,
wrong-type or unmatched renderers instead of guessing.

The field overlap is documented in the primary
[FFXIVClientStructs AtkEventData definition](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Component/GUI/AtkEventData.cs).
Validate against the installed client/library when implementing the replacement.

### Hidden or stale labels remain actionable

`ClickByLabel` checks only the matched text node's visibility, not its parent chain
or the owning list. `ListRendererIndex` can fall back to renderer state, ultimately
to row zero, when the item table does not contain the matched target. `SelectListItem`
does not validate visibility, item bounds, renderer identity, enabled state or an
addon-bound listener. `ClickNode`'s chain search also falls back to hidden nodes.

The 18:27:48 recovery actually dispatches Pass against a list logged hidden with
stale Ron/Pass labels. This is a confirmed live defect after onset. Checking
visibility is necessary but not sufficient: prior NPC-session observations showed
list contents and much of the tree persisting across closed prompts. A current
prompt identity and live item-table match are also required.

### Dispatch is mistaken for acknowledgement

`EmjActuator.cs:61–62` calls `MarkCallAnswered` whenever native dispatch returns
without a managed failure. That clears the prompt even if the game ignores it.
The resulting changed snapshot prevents the unchanged-fingerprint retry from
retrying the original window. A failed Ron/Tsumo dispatch additionally sets
WinDeclared, and `SnapshotBuilder.cs:78–80` reports RoundEnd until another game
event dislodges it. These can stall plugin decisions but do not by themselves
freeze the game's mahjong UI.

There is also an event-order defect: a native call may synchronously refresh the
addon, opening a newer prompt before returning. `MarkCallAnswered` operates on
whichever window is current after the native call, with no captured generation.
If a Chi selection synchronously opens its type-25 chooser, marking the old Chi
row answered clears the new chooser instead. The existing tracker test covers
only mark-then-type25 ordering (`EventTrackerTests.cs:334`), not the reverse.

Logs establish reentrant-looking call refreshes before the dispatch note at the
same millisecond for Pass/Ron. The one recorded older type-25 example arrived four
milliseconds after Chi dispatch (16:56:52.177 → 16:56:52.181), and completed normally.
Thus the wrong-generation clear is a demonstrable code path, not an observed cause
of this incident.

### Native pointer diagnostics are read after dispatch

`FireChain` reads `evt->State.EventType` after ReceiveEvent; `SelectListItem` reads
`chain->Param` afterward; `ClickByLabel` and `DiscardTile` read target NodeIds after
activation. A handler is allowed to rebuild UI state. Copy these scalar diagnostic
fields before invoking the handler. No use-after-free is proven by current logs.

The current implementation also passes the registered AtkEvent itself to native
handlers, exposing its mutable flags. The primary
[AtkEvent definition](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Component/GUI/AtkEvent.cs)
describes handled, return and dispatch flags. Whether mutation of those flags on
the registered EMJ chain causes this freeze requires comparison with the native
dispatcher; it should not be asserted from structure definitions alone.

## Proposed validation before integration

Keep any external-library/dispatch replacement separate from the anti-idle change.
The interaction proposal should include these requirements regardless of library:

- Resolve a semantic target from the current prompt and its item table. Require
  the expected addon, list/button type, visible ancestor chain and current legality;
  reject an unmatched or disabled row. Never use pooled label matches to guess row 0.
- Use event-type-specific payloads. Capture all identifiers and diagnostic values
  before native dispatch; avoid touching original node/event pointers afterward.
- Treat call selection as a pending operation tied to the original window identity.
  Recognize its acceptance by the corresponding game transition; preserve a newer
  Chi chooser even if it opens synchronously during dispatch. Handle Riichi's
  intermediate selection and subsequent discard separately.
- Make retries bounded and action-specific, requiring fresh targets and unchanged
  semantic state. A missing/hidden target is not evidence that hover is needed.
  Keep unverified hover escalation disabled pending a matched manual-input trace.
- Compare manual and automated discard, Pass, Pon, Chi (single and multiple shapes),
  Riichi, Ron/Tsumo and recap. Capture event type, listener/target identity, parameter,
  event payload, prompt generation, refresh nesting and accepted transition.
- Add focused deterministic coverage for missing renderer payloads, hidden-parent
  lists, stale row labels, no-dispatch retries and both synchronous/asynchronous
  Chi-chooser ordering. Native freeze validation still requires controlled live
  play; unit tests cannot establish that the client's UI no longer stalls.

No runtime interaction changes or live game actions were made by this audit.
