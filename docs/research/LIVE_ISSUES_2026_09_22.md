# Four live issues from the 2026-09-22 session — findings and plan

Evidence: `dalamud.log` / `dalamud.old.log` (2026-09-22, 14:40–17:02), the three crash logs of
that day, `pluginConfigs/MahjongHater.json`, and the code as of `320628c`. 376 auto-played
discards, 95 passes, 15 chi, 11 pon, 8 ron, 7 riichi, 1 tsumo.

| # | Reported | Verdict |
|---|---|---|
| 1 | "Precomputed policy" warning, likely a gz problem | **Not a gz problem.** The model loads fine from the shipped `.gz`; the warning is a different, optional subsystem that is switched on in the saved config with no data files present |
| 2 | Crash in `AgentEmj.Update`, stale address | **Most likely ours**, and not a stale offset: every click fires `MouseOver` and never `MouseOut`, leaving the agent hovering a tile that then leaves the hand |
| 3 | Won't declare winning hands | **Confirmed, but a different cause than first written.** The events never dropped a Ron; the two passes answered *phantom* windows the label-edge fallback invented from stale panel text |
| 4 | Riichi too conservative | **Not conservatism.** 7 of 9 genuine offers were taken; the other 2 were declined because our hand read said 1-shanten while the game said tenpai. The "refusals" seen on screen were phantom label windows |

---

## 1. The "Precomputed policy unavailable" warning

```
17:01:29.217 [INF] Learned policy loaded from ...\2.0.1.0\resources\models\learned_policy-8.json.gz
                   (experimental-unvalidated) as learned-guarded
17:01:29.253 [WRN] Precomputed policy unavailable; using existing policy: Could not find file
                   '...\pluginConfigs\MahjongHater\precomputed_policy.json'
```

The line above the warning is the shipped, gzipped model loading correctly out of the 2.0.1
install directory, so bundling and `.gz` reading both work. The warning comes from a different
feature — the **precomputed / simulation policy** (`PrecomputedPolicy`, `SimulationDatabase`),
an offline lookup table that has never been generated for this install.

`Configuration.PrecomputedPolicyEnabled` defaults to `false`, but the saved config has
`"PrecomputedPolicyEnabled": true` (plugin configs survive reinstalls, so a fresh install of
the plugin does not clear it). With the toggle on and no table on disk, `PolicyTable.Load`
throws `FileNotFoundException` every load.

Cost: one warning line per plugin load. No effect on play.

**Fix options** (small, independent):
- a. Treat "no file" as Information rather than Warning — it is an optional component, not a failure.
- b. Skip the attempt (and clear the toggle) when neither `simulation_policy/` nor `precomputed_policy.json` exists.
- c. Mirror the learned-policy indicator in Diagnostics so the state is visible without the log.

Note the model also costs **~1.0 s of synchronous load** at plugin start (17:01:28.213 →
17:01:29.217) for the 85 MB JSON. Moving it to a background task would remove the hitch;
unrelated to the warning but worth doing in the same pass.

---

## 2. The crash in `AgentEmj.Update`

Three crashes, all on 2026-09-22, none before that day:

| crash | faulting function | fault address | our last click | gap |
|---|---|---|---|---|
| 15:59:22.296 | `Utf8String.GetString` | `25D98A37D68` (heap) | 15:59:02.70 discard slot 2, node 1340002 (9m) | 19.6 s |
| 16:16:37.172 | `Utf8String.SetString+0x23` | `FFFFFFFFFFFFFFFF` | 16:16:34.88 discard slot 2, node 1340002 (1p) | 2.3 s |
| 16:53:11.166 | `Utf8String.GetString` | heap | 16:53:08.68 discard slot 7, node 1340007 (1s) | 2.5 s |

All three have the identical stack:

```
Utf8String.Get/SetString
sub_1410E3E50 + 0x13F/0x14B
Client::UI::Agent::AgentEmj.Update + 0xED6
… AgentModule.Update → RaptureAtkModule.Update → UIModule.Update → Framework.Tick
```

There is **no MahjongHater frame on the stack** — the game faults in its own agent update. Two
observations make us the likely trigger anyway:

1. **Every click we make begins with an unpaired `MouseOver`.** `EmjOperator.ClickNode` fires
   the node's registered `MouseOver` chain (needed because "a slot's MouseOver and ButtonClick
   live on different registered chains"), then the activation chain. There is no `MouseOut`
   anywhere in the operator. Real input always pairs them; ours never does, so the agent keeps
   treating a hand slot as hovered forever.
2. **The hover demonstrably makes the agent build a tile-name string.** Every one of our
   discards is immediately preceded in the log by a type-30 event carrying exactly that tile's
   name — the game's own tooltip text:

   ```
   16:53:08.676 evt type=30 [1]="Bamboo (1)" [2]=76059→1s   ← our MouseOver on slot 7
   16:53:08.676 op discard slot=7 node=1340007 (1s): MouseOver param=7; ButtonClick param=22
   16:53:11.166 CRASH in Utf8String.GetString inside AgentEmj.Update
   ```

Mechanism, then: we hover slot *n* → the agent records slot *n* as hovered and builds its
description string → we click → the tile leaves the hand and the slot nodes are rebuilt → the
agent still thinks slot *n* is hovered and refreshes that string from a stale index on a later
`Update` → `SetString` with `-1`, or `GetString` on freed memory.

That also explains the rarity (3 crashes in 376 clicks): it needs the agent to refresh the
tooltip while the slot is in an invalid state.

Confidence: **high but unproven** — no debugger was available to read the dump, and the
correlation is circumstantial. Ruled out: no other loaded plugin touches Emj (checked the
session's plugin list and searched the log), and no crash of this kind exists before today.

**Fix candidates**, cheapest first:
- a. **Drop the `MouseOver` and fire only the activation chain.** Test whether discards still
  register; if they do, the whole stale-hover class disappears. (The comment says MouseOver was
  added because the chains differ, not that the click needs it — worth re-testing.)
- b. **Pair it**: `MouseOver` → activation → `MouseOut` on the same holder, re-resolving the
  node before the `MouseOut` and skipping it if the node is gone or invisible.
- c. Fire `MouseOut` *before* the activation, so nothing is hovered when the hand changes.
- d. Belt and braces: after any click that changes the hand, clear the hover on the next tick
  from a node that still exists.

**Verification**: the crash needs ~100 discards to show up once, so the test is a long
unattended run (auto play, requeue on) with the change in place and the current build as the
control. The three crash logs give us a baseline rate of about 1 per 125 clicks.

---

## 3. Winning hands not declared — phantom windows, not dropped options

**This section replaces an earlier, wrong diagnosis** (that a refresh dropped `Ron` from a live
window, and that the fix was to union the options). The struct survey
(`EMJ_STRUCT_SURVEY.md`) established that `AtkValues` carries the offered actions as **integer
codes**: `[2]` is the first row, `[3]` the second (`1`=Tsumo, `2`=Ron, `3`=Riichi, `4`=Kan,
`5`=Pon, `6`=Chi, `0`=none) and, on a type-23, `[1]` is the row count including Pass. Re-read
through that lane, the events never dropped a win:

```
16:24:40.223 evt type=23 [1]=2 [2]=2 [3]=0  "Ron!"/"Ron"/"Pass"   <- a real Ron on 5s, taken, hand won
16:25:05.034 call window (label edge): [Ron] tile=2m              <- stale panel text, 25 s later
16:25:05.039 evt type=23 [1]=2 [2]=6 [3]=0  "Pass"/"Chi"/"Pass"   <- the real window: Chi only
16:25:07.268 AutoPlay -> Pass ... row 1: ListItemClick index=1    <- Pass sat at row 1 of 2
```

Two independent confirmations that Ron was not on screen at 16:25:05: the option code was `6`
(Chi) with a row count of 2, and the operator resolved "Pass" to **row 1 of 2** — in a
Ron+Chi+Pass list Pass would be row 2. 16:38:03 has the same shape, residue of the real Ron at
16:35:40. Unioning the options would have made the plugin click a Ron row that does not exist,
and would have recovered **zero** wins.

The real defect is that the label-edge fallback can invent a window at all: the panel keeps its
texts after a prompt closes, so it re-reads a finished hand's buttons. Across the session it
produced 83 of 92 riichi-labelled windows, none of which led to a declaration.

**Implemented**: options are decoded from the integer codes (locale-independent, no `"Pass"`
convention, no `"Discard"` banner leaking in as an option), cross-checked against the row
strings — a type-19 also carries unrelated integer payloads, and one live frame read
`[1]=13 [2]=0 [3]=1 [4]=2 ... [8]=6`, a plain index ramp whose `[3]=1` would otherwise decode as
"Tsumo offered". A frame whose codes and rows disagree is ignored rather than trusted. A
label-edge window now expires after 400 ms unless an event confirms it; every genuine window in
the session had its event within 5 ms.

## 4. Riichi — 7 of 9 taken; the other two are a hand-read disagreement

The policy is not conservative. Every genuine offer in the session (option code 3):

| offers | taken | declined |
|---:|---:|---:|
| 9 | 7 | 2 (15:56:45, 15:58:13) |

Both declines answered a real two-row window (`[1]=2 [2]=3`, rows "Riichi"/"Pass") with a plain
discard, and both times our analysis called the hand **1-shanten**. The game only offers riichi
on a hand that is tenpai after a discard, so our hand read and the game disagree — that is the
defect worth chasing, and the log alone cannot settle it because the closed hand is not recorded
at that moment.

The three *other* "declined riichi" in the session were phantom label windows on 1-shanten
hands (§3), where discarding was correct.

An earlier example here — a "missed riichi at 16:35:02" — was wrong: we had ponned 5p at
16:35:00 (`[Meld] seat 0 type-13 from=2: Pon [5p 5p 5p]`), so the hand was open and riichi was
illegal.

**Implemented**: when the game offers riichi and the policy answers with a discard, the plugin
logs the closed hand, the drawn tile and the meld count at Warning and records it in the
auto-play journal, so the next occurrence is reproducible instead of inferred.

## Plan

Ordered by (damage × confidence) / effort:

| # | Work | Why first | Effort |
|---|---|---|---|
| 1 | **Decode options from the integer codes**, cross-checked against the rows (#3) | Locale-independent, drops the banner and `"Pass"` conventions, rejects non-option payloads | done |
| 2 | **Expire unconfirmed label-edge windows** after 400 ms (#3, #4) | Removes the phantom windows behind the apparent refusals | done |
| 3 | **Never leave hover set** (#2): activation-only by default, `MouseOver`+`MouseOut` before the click as the fallback the auto player escalates to | Stops crashing the game | done, unverified in play |
| 4 | **Warning hygiene** (#1) and a Diagnostics line for the optional tables | Cosmetic | done |
| 5 | **Diagnose the 2/9 riichi declines** (#4) from the new hand dump, then fix the hand read or the shanten path | The one remaining decision-quality defect in the data | needs a live session |

What is left to verify in play:

- **The crash fix** needs an unattended run: 3 crashes in 376 clicks is the baseline, so a
  session of comparable length with no crash is the first real evidence. If a click ever fails
  to register, the auto player escalates to the hover style and says so in the journal and log.
- **The riichi declines** need the new hand dump from a live session before anything in the
  analyzer is touched; guessing at the hand read without it would be premature.

Not proposed: computing self-declare legality ourselves — the integer codes are authoritative
and locale-independent, so the visibility gap closes without it — and no change to the riichi
thresholds or the call gate, since nothing in the data suggests the policy is too conservative.
