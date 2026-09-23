# Response to DOMAN_UI_AUDIT_2026_09_23 — 2026-09-23

What was changed after the interaction audit, and what the evidence for each change actually
is. The audit is in `DOMAN_UI_AUDIT_2026_09_23.md`; it audits `main` at `6e1a9f4` (v2.3.0).

## 0. The audit's central finding is correct, and it overturns ours

The audit says every discard callback in the capture has a matching `ButtonClick` in the same
frame, including the four this project published as event-free. Verified independently against
the same bytes — all three of the audit's SHA-256 fingerprints match our copies:

```
timeline.json   db480ca9cebf291315d9b7bf268be135335137c4414afd4918fcfd33745ef5b0
cb_Emj.json     1711e8abb1ca8576b67ecd6bd89f5e5601862fefe4226e89cb84ecdfe7f9160c
```

Pairing by the capture's own frame number instead of by millisecond:

```
head 7 (discard): 100 fires, 100 paired, 0 unpaired
```

The four "event-free" discards each carry a `ButtonClick param=slot+15 node=9` 23–51 µs away,
on the far side of a millisecond boundary. `param == slot + 15` holds in all four, so they are
real pairs, not coincidence. **Our millisecond bucketing dropped exactly the four cases that
crossed a tick, and a finding was then built on the hole it left.**

Corrected counts, and the tool that produces them: `tools/pair_callbacks.py`.

| head | published | actual |
|---|---|---|
| 7 discard | 96 paired + 4 event-free | **100 paired, 0 unpaired** |
| 11 call row | 22 | 28 (30 fires with repeats) |
| 14 recap next | 9 | 10 |
| 15 pointer | 1 / 117 | 3 / **115** |
| 10 | *(absent)* | 1 fire, unidentified |

**One leg survives:** `[15]` really is addon-generated (115 of 118 unpaired), so the
cursor-polling reading holds. **What does not survive:** that the capture proves a bare
`[7, slot]` is sufficient, and therefore the stated reason for not sending `[15]`.

Where we part company with the audit: bare `[7, slot]` **is** sufficient — v2.3.0 discards
through it for whole live matches. That is known from play, not from the capture, and the
audit (which did no live validation) is right that the *stated* proof was absent. The comments
now say which source each claim rests on.

## 1. Corrections made to the record

`EmjProtocol.cs`, `EmjOperator.FireCommand`, `ADDON_PROTOCOL_2026_09_23.md`. The protocol note
now carries a visible correction block rather than a silent rewrite, the retracted
"after riichi the game discards for you" entry is struck through, and the "Gap: none
outstanding" section is replaced with the real list (bare-callback sufficiency per command,
chi shapes beyond index 0, the cancel callback, `head 10`, `EmjRankResult`, the stuck table,
client-language variants).

`tools/pair_callbacks.py` pairs by frame and refuses to do it any other way, so this particular
mistake cannot recur silently.

## 2. The label-edge path, removed in both senses

Two different things were called this, and both are gone.

**The label button search** (`EmjOperator.FindButtonByLabel`, `LabelTarget`, `Walk`,
`WalkManager`, `EmjActuator.ClickButton`). Already had no production caller: the
`EmjTotalResult` node-26 discovery replaced its only one.

**The label-edge call-window detection** (`EventTracker.OnTick`, source `"label edge"`). This
is the one that mattered. The panel keeps its button text after a prompt closes, so every
window it opened was a guess that an event then had to confirm or expire. It never found a
prompt the events missed — all nine self-declares answered in the 2026-09-22 session came from
the type-19/23 — and a window it opened was marked `CallWindowFromLabels` and then **refused**
by the actuator. Its only observable effect was noise: 219 phantom windows in one 2026-09-23
session, each flipping the phase to SelfDeclare for ~40 ms.

Removed with it: `CallWindowFromLabels`, the 400 ms grace timer, the riichi-label suppression
and draw anchoring (patches that only existed to contain this path), the actuator's label-only
refusal, and the auto player's label-only branch and riichi downgrade. `OnTick` no longer takes
`promptLabels`. Panel text is still *read* for diagnostics; it no longer *decides* anything.

This is also the likely root of the ghost riichi/pass buttons seen under the recap: a stale
"Riichi" label was re-opening a window the game had already closed.

## 3. Dispatch no longer acknowledges itself (audit §2)

Three fabrications, all removed.

**The window closed itself on send.** `MarkCallAnswered` cleared the call window the instant
the actuator dispatched, so a refused or ignored answer was indistinguishable from an accepted
one. It is now `NoteAnswerSent`, which records a `PendingAnswer` and closes nothing. The window
is the game's to close — it does so on the following draw, discard, meld or score event — and
that closing is what confirms the answer. An answer that is never acted on times out after 3 s
and says so in the log.

**A sent win forced the phase to `RoundEnd`.** `WinDeclared` is now `WinAnswerPending` and is
plainly our own intent. `SnapshotBuilder` reports the phase the *game* is showing and carries
our intent separately as `StateSnapshot.AwaitingOurWin`, which empties `Legal` without
relabelling what the game is doing. The auto player waits on it instead of entering recap
handling — which is what used to press recap Next against a live table.

**Any sequence change counted as acceptance.** The snapshot sequence moves on every refresh
(another seat's discard, a timer, a score tick), so an ignored answer was acknowledged by the
next unrelated frame. `PendingDispatch.WasAcceptedIn` now checks the specific change the action
should cause: a discard means our hand got shorter; anything else means the window we aimed at
is gone or replaced. A dispatch that never produces its effect is reported as unacknowledged
after 4 s and kept for the retry ladder (which runs at 6 s) rather than forgotten.

## 4. The list cast is checked (audit §4)

`EmjOperator.Rows` accepted any node with `Type >= 1000` and cast its component to
`AtkComponentList`. That number only means "some component".

**Confirmed reachable on the live client, 2026-09-23.** In the running table, the call list is
`1/46/104/3` → `Component:List`, while its own parent node `104` is `Component:Base` — and a
second `Component:List` at node 93 is the *yaku recap* list, not a call list. All three are
`Type >= 1000`. A path that resolved one node short, or a layout change, and the old code would
have read `ItemRendererList`, both length fields and every `Label` out of unrelated memory on
the framework thread.

`Rows` now requires `GetComponentType() == ComponentType.List`, validates both length fields
and bounds, and returns a `why` explaining any refusal — an empty list *with* a reason is not
the same thing as a list with no rows, and `AnswerCall` now reports it instead of calling it
"the call list is empty".

The enabled predicate was `!list->GetItemDisabledState(i) || renderer->IsEnabled`, a permissive
OR defended by the idea that the game would ignore a click on a row it considered disabled —
which stopped being true once we bypassed the widget and sent the addon's command directly.
Disagreement between the two is now `ListRow.EnabledDisputed` and is refused, because neither
field has been established as authoritative.

## 5. The Yes/No confirmation is gone (audit §5)

`ConfirmYesNo` clicked Yes on any `SelectYesno` within six seconds of our own result-screen
close, having read the prompt text and then ignored it. The capture shows **no `SelectYesno` at
the end of a match at all** — the only one in the whole match was the NPC table's
start-of-match challenge — so the guard could only ever fire on a dialog it was not written
for. A duty-leave confirmation landing in that window would have been accepted.

Replaced by `BlockedByDialog`, which refuses to act while any Yes/No is up and logs its wording
once. When a real end-of-match confirmation is ever measured, it can be answered by identity —
prompt, owner, pending transaction — rather than by elapsed time.

## 6. The dead route toggle is gone (audit §7)

`EmjOperator.Route` / `Configuration.NativeListSelection` and its UI advertised a choice between
the registered `ListItemClick` and a native `SelectItem` for call rows. `AnswerCall` has gone
through `FireCommand` since the protocol was measured and has not reached `SelectRow` since, so
the "comparison" compared nothing. `SelectRow` remains as `ClickNode`'s safe branch for list
nodes and now says so.

## Validation

- Release build clean; **518 tests pass**, 0 failed, 0 skipped.
- Live read-only validation against the running client via Cartographer: the call-list
  component type (§4 above) and the current addon state.
- **Not yet validated live:** every behavioural change. The running plugin is the old build;
  nothing here has played a hand. The audit's validation matrix is the right checklist for it.

## Still open

- Bare-callback sufficiency for `[11]`, `[12]`, `[14]` individually.
- The chi cancel callback; chi shape indices other than 0.
- `head 10`.
- `EmjRankResult`'s close control (assumed to be node 26, never observed).
- The stuck table: still undiagnosed. `[15]` is one hypothesis among several, and the
  `/mhater focus` output is evidence, not a diagnosis. Needs same-frame pointer coordinates,
  tile bounds, collision state, modal ownership and ImGui capture, with idle nudges isolated.
- Audit §6 (raw hand-slot identity carried through decoding instead of derived from a filtered
  visual node list), §8 (addon setup/finalize generation), §9 (generic event fallback crossing a
  native mutation boundary), §10 (per-result-addon adapters), §11 (English labels and client
  language). None started.
