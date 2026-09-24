# Closed-kan discard failure, 2026-09-23

Initial investigation (before the implementation below) reviewed source at
`9e544b5` (v3.0.0), the retained match log, and an isolated offline reproduction. The client
and Cartographer were unavailable.

The closed kan was accepted and correctly recorded by `EventTracker`. `SnapshotBuilder`
subsequently discarded that information because the struct decoder assumes meld marker
255 means chi. This kan also used 255. Losing one meld made the snapshot omit our drawn
tile and report `OthersTurn`, so autoplay stopped choosing discards until the next hand.

## Recorded sequence

Times are local, Europe/Paris (UTC+2), from `%APPDATA%/XIVLauncher/dalamud.old.log`.
Line numbers below refer to that retained file.

| Time | Evidence |
|---|---|
| 16:43:38.161 | Our first meld is Chi `[5p 6p 7p]` (42413). |
| 16:44:45.540 | Autoplay selects `AnKan 4p [4p 4p 4p 4p]` through the native Kan row (42625). |
| 16:44:45.646 | Type-13 meld payload: `[1]=0 [3]=6 [4]=2 [5]=0 [6]=255 [7]=4 [8]=76053`. Tracker logs the complete `Ankan [4p 4p 4p 4p]` (42633–42635). |
| 16:44:45.657 | Snapshot reports `seat 0 meld 1: chi composition unknown (no type-13 seen)` and `7 closed + 1 meld(s) = 10` (42636). The diagnostic is misleading: the type-13 event was just recorded. |
| 16:44:46.761 | Replacement draw `9s`; type-21 reports seven closed tiles; type-6 enables our turn (42638–42644). |
| 16:45:01.895 onward | Five local discards occur: `9s`, `8m`, `1m`, `4z`, `7s`. None has an autoplay discard dispatch. The first follows the replacement draw by 15.1 seconds, consistent with timeout discard; the log alone cannot distinguish every later timeout from manual intervention. |
| 16:45:24.758 / 16:46:08.342 | Autoplay still answers two Chi prompts with Pass. Its discard path is disabled; it has not stopped running. |
| 16:46:26.441 | Autoplay sends Ron on `5s`, despite its incomplete hand model. The existing game-offered-win override protects the win (43140). |
| 16:46:40.475 | Round reset clears the bad meld state and read health returns to clean (43181). |
| 16:46:53.158 | Autoplay discards normally in the next hand (43196). |

This is a state-reconstruction failure, with a reproducible mechanism separate from the
earlier leaked input-block counter. Prompt actions continued to work during the incident.

## Exact failure path

1. `Core/State/StructFrame.cs:126` assigns `IsChi = (tileIndex == 255)`.
2. `Core/State/SnapshotBuilder.cs:208` collects only sequence melds for these entries.
   Its branch at line 222 consumes the existing chi for slot 0, finds no second chi for
   slot 1, and drops the kan. The tracker still retains both melds.
3. The draw inclusion check at line 31 expects the maximum closed-hand size for **one**
   meld (11). Seven closed tiles plus the replacement draw is eight, so it excludes
   the draw. With the correct **two** melds, eight is exactly the expected size.
4. `ComputePhase` at line 290 receives total 10 instead of 14 and returns `OthersTurn`,
   even though the raw state is 6. The snapshot grants no discard action.
5. Autoplay has no actionable discard. Its stall detector measures time since **any
   snapshot change** (`AutoPlayer.cs:194,433`); ongoing draws, discards and prompts keep
   resetting that timer. Consequently this failure does not produce a useful stall dump.

The type-13 payload directly proves 255 is not exclusive to chi. The snapshot diagnostic
and decoder establish that the struct record took the 255 branch. There is no retained
raw-memory snapshot of this exact late-afternoon incident, so this investigation does
not claim a complete new native meld layout.

## Offline verification

Isolated harness: `../kan-audit-repro-20260923/Program.cs` relative to the repository.
It references the current production project and existing struct-fixture helpers.

The fixture supplies the recorded chi and kan events, two struct marker-255 records
(directions 3 and 0), seven closed tiles and the recorded replacement draw. The closed
tile identities come from the later Ron diagnostic: this is a reconstruction of the
failure conditions, not a byte-for-byte replay of the replacement-draw frame.

```text
Current: tracked=2, snapshotMelds=1, hand=7, draw=, phase=OthersTurn, discard=False
  seat 0 meld 1: chi composition unknown (no type-13 seen)
  hand does not add up: 7 closed + 1 meld(s) = 10, expected 13 or 14
Known kan identity: tracked=2, snapshotMelds=2, hand=8, draw=9s, phase=OurTurn, discard=True
```

The second case feeds the known kan identity through the existing non-chi reconciliation
path. Both assertions pass. This isolates the cause and shows that retaining the kan
restores a legal discard turn; it is not a tested implementation of the proposed fix.

## Proposed repair

- Treat 255 as an unresolved meld tile index, not a definitive meld type. Correct the
  `IsChi`/`MeldTileIndexChi` naming and the corresponding layout documentation.
- Reconcile struct records with tracked melds one-to-one, respecting the struct count,
  order and available direction/type evidence. A recorded concealed kan must be eligible
  for a marker-255 record. Preserve its four tiles and closed status. Do not simply
  classify every 255 as kan: real chis also use it.
- Keep unresolved records explicit when an event was missed. Do not fabricate a pon or
  silently shrink the meld count and call it an opponent turn. Report that the game is
  offering a turn but meld reconstruction is incomplete.
- Add regressions for a lone concealed kan, chi then kan, kan then chi, multiple concealed
  kans, red-five kans, missing events and round reset. Verify both replacement-draw
  inclusion and normal discard decisions, while retaining the game-offered-win override.
- Diagnose persistent reconstruction failure independently of general snapshot activity.
  Avoid forcing arbitrary discards as a workaround for a corrupted hand model.

Two adjacent findings deserve separate coverage. An earlier added kan at 16:05:31 uses
refresh **14**, with `[1]=0 [2]=30 [3]=76071`; autoplay resumes discards at 16:05:36.
`EventTracker` has no explicit case 14, so updating a pon into an added kan is another
mapping gap, although it did not cause this freeze. Also, the affected Ron prompt produces
135 repeated disagreement warnings in roughly two seconds; log that diagnosis once per
window rather than once per frame.

## Preserved evidence

`artifacts/closed-kan-audit-20260923/last-match.log` contains only MahjongHater lines from
16:30–17:00, prefixed with original line numbers. Artifacts are gitignored.

Original retained log SHA-256:
`48365562e10c6deef8e115562fcea19763c56eee9b46e7df9bcce3c3f1476b17`.

## Implemented repair

The follow-up change replaces the chi-only branch with one-to-one reconciliation against
an event ledger. Confirmed type-13 events retain their post-refresh struct slot and event direction;
identical sequences in different slots remain distinct, duplicate refreshes do not add
sets, and empty struct records retain their positions. A known event can resolve an
unfamiliar marker when slot and direction agree. Malformed, truncated or unrecognized
meld payloads are left unresolved rather than guessed.

Type-14 now upgrades an existing pon into a Shouminkan in place. If the original pon
arrives late, the retained upgrade completes it; repeated old pon events do not downgrade
it. Meld construction preserves red-five identity. Counts and upgrades clear at round
boundaries. The existing hand-delta fallback can recover missed concealed-kan events.

Snapshots retain native meld counts separately from known compositions. An unresolved
local meld no longer hides the replacement draw or mislabels the turn. Strategic actions
wait for a complete read; confirmed wins and prompt passes remain available. The actuator
also refuses recovery discards from an incomplete meld read. A separate five-second watch
writes a READ FAILURE diagnostic even while snapshot sequences change, once per continuous
failure. Win-read disagreement messages are limited to once per prompt.

Regression coverage includes the recorded incident, all 24 orderings of identical chis
and mixed kans, one through four concealed kans, opponent kans, red fives, repeated and
late events, missing slots, unfamiliar markers, malformed payloads, added-kan upgrades,
round reset, analysis cache identity, legacy layout keys, and confirmed wins while the
read is incomplete. The new layout key is `tileIndexUnknown`; `tileIndexChi` remains
accepted for existing custom layouts.

The normal-play sanity check also replays four captured local call snapshots (pon,
multiple chis, and a pon plus two chis). It caught and removed a proposed use of payload
[4] as ordinal: that field repeats across different melds. Live slot identity comes from
the native meld count read in PostRefresh, after the handler increments it. The saved
native routine increments the counter at static address `0x1416F66B0`. Normal captured
positions must remain discardable and produce discard decisions.

Recovery continues for ordinary complete states. Incomplete reads and confirmed winning
prompts cannot enter the blind Pass/discard/recap recovery ladder; diagnostics do not
start that ladder. The existing action dispatch and recap input-block guards remain.

Validation: all 581 tests pass. Unknown composition still requires evidence: loading
mid-hand after missing its meld events can suspend strategic actions until reconstruction
or the next hand. The code no longer manufactures a pon from a tile index alone. Input
dispatch is unchanged. Offline regressions validate the repair; live validation awaits
an available client.
