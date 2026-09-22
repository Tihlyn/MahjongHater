# Missed wins and call windows, 2026-09-22 — where the gap actually is

Evidence: `dalamud.old.log` (14:40–16:53, v2.0.1.0) and `dalamud.log` (16:53–18:29, the last
15 minutes on v2.0.2.1), reconstructed frame by frame. 275 prompt events, 331 logical call
windows, 43 finished hands in `hand_results.csv`.

The reported symptom is real and expensive, but it is **not** a window-visibility problem.
The window plumbing measures clean; the hand read underneath it does not.

## What the logs establish

| Measurement | Result |
| --- | --- |
| type-19/23 prompt frames the game sent | 275 |
| …that opened a call window | 255 |
| …suppressed as the echo of an answer just given | 19 |
| …rejected because codes and rows disagreed | 1 (the `[1]=13 [2]=0 [3]=1 [4]=2 … [8]=6` index ramp — correctly rejected) |
| Option payloads on undecoded event types (15/20/22/10/…) with no 19/23 nearby | 196, **all** stale panel text riding along in unrelated events — no lost prompt |
| Logical windows confirmed by a prompt event | 146 |
| …answered by the operator | **142** |
| Phantom (label-only) windows | 185 |
| …ever answered by the operator | **0** |

So: every prompt the game sent was seen, none was invented into a click, and the decode from
the integer option lane agreed with the row strings everywhere it was checked.

## The four confirmed windows that went unanswered

```
15:23:23  [Kan]     lived 4.00 s, ended by our own discard
15:56:45  [Riichi]  lived 3.25 s, ended by our own discard
15:58:13  [Riichi]  lived 3.03 s, ended by our own discard
16:20:01  []        the index-ramp frame, 0.10 s
```

The two riichi declines are the ones already recorded in
[LIVE_ISSUES](LIVE_ISSUES_2026_09_22.md) §4: the game offers riichi only on a hand that is
tenpai after a discard, and both times our analysis called the hand **1-shanten**.

## The expensive one: a Ron passed on our own declared wait

```
16:19:42.948  AutoPlay seq 75 SelfDeclare -> Riichi 7m | "Riichi 7m: waits: 6m, 9m."
16:20:01.618  [Meld] seat 0 type-13 from=0: Ankan [3z 3z 3z 3z]
16:20:37.170  evt type-23 [1]=2 [2]=2 [6]="Ron!" [7]="Ron" [8]="Pass"
              call window (type-19): [Ron] tile=6m claim=True        <- read perfectly
16:20:40.598  op call "Pass"
              AutoPlay seq 118 CallPrompt -> Pass | "No discard is currently legal."
16:21:26.519  type-29 "Draw" — exhaustive draw, we were the only tenpai (+2000)
```

`hand_results.csv` confirms `ourRiichi=1` for that hand. We declared riichi on a 6m/9m wait
and then **passed on 6m**. The window was decoded correctly, with the right tile and the
right claim flag; the decline came from `DecisionPolicy.WinningHan` scoring the hand below
the Doman han minimum — impossible for a riichi hand that wins, so the hand it scored was
not the hand we held.

Two things made this invisible:

1. **The summary lied.** Declining a win and then falling through to the call/discard path
   produces `"No discard is currently legal."` — the Pass text, not the reason. Nothing in
   the log said a Ron had been evaluated at all.
2. **The health notes were never logged.** `SnapshotBuilder` already emits notes for a wrong
   closed count, undecodable slots and unsourced melds, but they only ever reached the
   overlay and stall dumps. A session log therefore cannot show that a decision was taken on
   a drifted hand. Across both logs: zero health notes, because there was nowhere to write
   them.

The only kan of the entire day is 36 seconds before the only missed Ron. That is
circumstantial (n=1) and the kan arithmetic itself checks out — a concealed kan counted as
three for hand arithmetic decomposes correctly, and
`MahjongHater.Tests/Policy/WinDeclarationTests` proves the same shape wins when the closed
read is right. What the log cannot show is what `state.Hand` actually held at 16:20:37;
`docs/EMJ_STRUCT.md` still lists kans as unobserved ("tile-count deltas and whether `+0x240`
stores a kan the same way"), so a post-kan closed read that keeps the four kan tiles is the
open suspect, not a finding.

## What changed

1. **A win the game offers is taken.** The game opens a Tsumo/Ron window only for a complete,
   yaku-bearing hand, so re-deriving legality from our own read can only lose won hands.
   `DecisionPolicy` now declares the offered win and records its own han count as an
   explanation instead of a gate.
2. **…but only on a confirmed window.** `StateSnapshot.CallWindowConfirmed` carries whether a
   prompt *event* opened the window. On a label-guessed window the old yaku gate still
   applies, so a phantom "Tsumo" can never become a declaration. (185 phantoms in these logs;
   none was ever answered, and this keeps it that way.)
3. **The disagreement is loud.** When the game offers a win our read scores as no win, the
   choice carries a `win-read-disagrees` step and `AutoPlayer` logs the closed hand, melds,
   riichi flag and offered tile at Warning. A win offer answered with anything else logs even
   louder. The next occurrence arrives with its evidence attached.
4. **Hand arithmetic is checked where it is built.** A hand is 13 or 14 with every meld
   counted as three; anything else is now a snapshot health note rather than a silent zero
   inside the win evaluation.
5. **Health notes reach the log.** `EmjStateReader` logs the note set at Warning whenever it
   changes (and "clean" when it clears), once per change rather than per frame.
6. **A corroborated window is never vetoed by our own read.** `EventTracker` dropped any claim
   window whose tile our closed hand could not call — the guard that tells our prompt apart from
   another seat's Pon!/Chi! banner, which shares the type-19 event. That guard consumed the
   drifting hand read, so it could silently discard a real prompt. It now applies only to the
   uncorroborated sources. A **type-23 whose `[1]` row count equals its option codes plus Pass**
   describes a row list the game is showing *us*; an announcement has none, and this held on
   145/145 type-23 frames. Such a window opens regardless of what our hand says, and notes the
   disagreement.
7. **Every claim window is now an assertion about the hand.** `HandTracking.InferClaims` returns
   the specific claims a tile allows, and `SnapshotBuilder` compares them against the game's
   offer on every confirmed claim window (see below).

## Can the window be worked backwards instead?

Asked as a sanity check: rather than reacting to the prompt, infer it from the table state, so a
window is never unknown. Measured against these logs, the inference itself is sound — the coarse
version already in `HandTracking` agreed with the game on **all 242 confirmed claim windows**
(the single apparent contradiction is two different seats discarding 6m two seconds apart, where
it correctly allowed chi from kamicha and refused it from toimen).

It still must not become the source, for one reason: **it consumes the hand read, which is the
thing that is broken.** Deriving the window from a drifted hand converts a state bug into a
missing or invented prompt — strictly worse than today, where the event is ground truth and the
drift is contained to the decision. Ron inference additionally needs furiten and yaku, i.e. the
exact computation that just failed, and kan-during-riichi needs wait preservation. The event, by
contrast, is free, locale-independent and was measured at 255 windows from 275 frames with no
misses.

So the inference is used in the two places where it is strictly better than nothing:

1. **As an oracle over the offer.** Chi/Pon/Kan are pure tile counting, so on every confirmed
   claim window the game's offer and our read must agree, and a difference names the direction of
   the drift. That is hundreds of free assertions about the hand read per session.
2. **As a filter, never a source.** It still gates the uncorroborated windows (bare type-19, label
   edge), where it is the only evidence available. At 14:44:46 the panel over-reported
   `[Pon,Chi]` where the game offered only `[Chi]` — that is the shape of error it catches.

And one thing it must stop doing: vetoing a **corroborated** window. See the row-count
discriminator above.

## Still open

- **The hand read itself.** Nothing here fixes the drift; it makes it self-reporting. The
  next session's log will either be clean or name the note. Watch a hand with a kan in
  particular, and the two riichi-decline dumps that v2.0.2.1 already added.
- **The `[Kan]` decline at 15:23:23** was not investigated; it may be a correct policy call.
- These logs predate the addon-interaction rework
  ([ADDON_INTERACTION_2026_09_22.md](ADDON_INTERACTION_2026_09_22.md)), so the answer path
  they measure is the old one. The window *decode* they measure is unchanged.
