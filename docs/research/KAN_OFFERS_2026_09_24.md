# Kan offers in the last two matches, 2026-09-24

The repeated Kan windows were separate own-turn offers of the same added kan on
6m. All 11 were followed by successful discards that closed the current prompt.
The evidence supports policy rejection followed by the intended discard path;
there is no sign of a stuck window or failed Pass click in these occurrences.
The exact rejection metrics were not logged.

## Scope and sources

All times below are local, UTC+02:00. Match boundaries are the queue's
`Accepted -> InContent` and `InContent -> None` transitions in
`%APPDATA%/XIVLauncher/dalamud.log`, inspected after 16:25.

| Match | In content | Kan offers | Responses |
| --- | --- | --- | --- |
| Penultimate | 14:55:05.283–15:27:21.005 | 2 opponent-discard offers | Both explicitly Passed |
| Latest | 15:30:43.797–16:17:03.677 | 1 opponent-discard offer, 12 own-turn offers | 1 Pass, 11 discards, 1 added Kan |

The plugin loaded testing version 3.0.0.1 at 10:55:40, using `learned-guarded`.
The installed DLL matches `artifacts/published-testing-3.0.0.1.zip` by SHA-256.
The plugin's `autoplay_stalls.log` was last modified on September 23, so it has
no dumps from either match. `hand_results.csv` contains these matches' outcomes,
but no Kan reasoning. Snapshot capture is disabled in the inspected config and
the snapshot file was last modified on September 22.

## Opponent-discard offers

| Offer time | Window | Tile | Response time |
| --- | --- | --- | --- |
| 15:06:26.460 | 346 | 5s | Pass at 15:06:28.972 |
| 15:10:30.730 | 361 | 1s | Pass at 15:10:32.943 |
| 15:31:24.149 | 396 | 3z | Pass at 15:31:26.928 |

All three selected row 2 of `[Kan, Pon, Pass]`. The game advanced and the plugin
confirmed each Pass. None was answered by a discard.

## Repeated own-turn offers

At **15:33:35.664**, seat 0's pon of `6m 6m 6m` was confirmed. At
**15:33:57.664**, seat 0 drew the fourth 6m, and a confirmed `[Kan]`,
`claim=False` window opened. The native prompt labels were `Discard`, `Kan`,
`Pass`.

| Offer time | Window | Discard time | Discard |
| --- | --- | --- | --- |
| 15:33:57.664 | 400 | 15:34:01.460 | 7z |
| 15:34:13.164 | 401 | 15:34:15.910 | 1m |
| 15:34:33.848 | 402 | 15:34:36.460 | 8p |
| 15:34:45.782 | 403 | 15:34:48.494 | 3m |
| 15:34:56.781 | 406 | 15:34:59.411 | 3p |
| 15:35:28.149 | 407 | 15:35:32.244 | 2m |
| 15:35:53.167 | 410 | 15:35:57.029 | 9p |
| 15:36:13.734 | 413 | 15:36:16.645 | 2m |
| 15:36:44.350 | 416 | 15:36:47.079 | 8m |
| 15:36:57.633 | 417 | 15:37:00.629 | 4s |
| 15:37:17.967 | 418 | 15:37:20.912 | 3p |

Every entry has a game-side type-8 discard event, a `call window cleared:
discard` message, and acceptance within 83–134 ms of dispatch. Each subsequent
offer follows a new draw. Keeping the fourth 6m preserves the added-kan option,
which explains the repeated prompts without a stuck-window hypothesis.

At **15:37:34.884**, the retained 6m was consumed in a confirmed chi of
`5m 6m 7m`. The next draw did not open a Kan prompt.

Relevant original log lines: 41618 (pon), 41658–41676 (first draw, offer and
discard), 42175–42191 (last repeated offer and discard), 42233 (chi).

## Why a discard appears instead of Pass

`SnapshotBuilder.cs:125` maps an own-turn Kan offer to both `AnKan` and
`ShouMinKan`; it also enables discard for a complete own-turn hand.
`DecisionPolicy.cs:125` evaluates calls first. If the call is rejected, it
returns Pass only when discard is unavailable; otherwise it continues into
discard selection. Thus `SelfDeclare -> Discard` is the normal representation
of declining an optional own-turn Kan while completing the turn.

`LearnedCallPolicy.cs:19` leaves own-turn kans to the heuristic. In
`CallPolicy.cs:44`, a Kan is rejected if the before/after analysis is invalid,
shanten worsens, or live improving tiles decrease. The additional wait-set
restriction applies in riichi.

Keeping 6m for sequences is a plausible strategic explanation: the early
discards retain reported waits on 2m/2z, and the fourth 6m is eventually used
in a chi. This is an inference, not a recovered per-window policy explanation.
The available records do not contain each full hand and before/after analysis,
so they cannot prove which comparison rejected each of the 11 offers.

## Successful added kan in the same match

At **16:15:02.596**, window 480 offered Kan. At **16:15:05.758**, the plugin
selected `ShouMinKan 5z`, with the explanation:
`Kan preserves shanten and live improving tiles.`
At **16:15:05.862**, the game confirmed `seat 0 type-14: Shouminkan 5z`.
This shows that added-kan recognition and execution worked in this session.
Original log lines: 47907–47922.

## Diagnostic limitation and follow-up

`AutoPlayer.cs:355` logs the final action summary, omitting `choice.Steps`,
where the call rejection reason lives. Moreover, `CallPolicy` currently returns
a generic decline explanation rather than the individual Kan comparison.
For a definitive future audit, log one record per answered Kan window with
the candidate, before/after shanten and ukeire, rejection reason, and final
action. No gameplay or policy changes were made during this investigation.
