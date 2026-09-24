# Hand-result logging correction — 2026-09-24

The last-match log and saved Cartographer Emj events confirm two independent errors:

- Type 32 `[1]` was interpreted as a winner seat. It is null or zero even on named opponent wins.
- `RecordTenpaiGroundTruth` finalized the CSV row on that screen, before type 29 arrived.
  `LastScoreDelta` was still its initial zero, and the pending row had already been consumed.

Type 29 has five values: the header and four relative-seat transfers in hundreds of points.
The native case at `0x1416ED302` multiplies them by `0x64`. Type 32 carries the win details;
the long persistent addon array seen at state 29 also contains leftover type-32 fields.

## Captured examples

Source: `artifacts/emj-audit-20260923/monitor-events.jsonl`, Emj refreshes, UTC timestamps.
The regression fixture retains the first nine values and source sequence numbers, omitting names.

| Win / draw announcement | Payment | Transfers (hundreds) | Correct result for us |
|---|---|---|---|
| 13:13:41, ron | 13:13:46 | `20,0,-20,0` | win-ron, +2,000 |
| 13:20:28, draw | 13:20:30 | `10,0,-30,10` | draw, +1,000 |
| 13:24:28, opponent tsumo | 13:24:33 | `-40,-21,92,-21` | tsumo-loss, -4,000 |
| 13:26:28, opponent ron | 13:26:33 | `0,0,20,-20` | other-ron, 0 |
| 13:27:37, opponent ron | 13:27:43 | `0,80,0,-80` | other-ron, 0 |
| 13:30:08, draw | 13:30:10 | `-15,5,5,-15` | draw, -1,500 |

The later match gives the same signature: at local 16:05:41, a named opponent's type-32
screen had `[1]=0`; the old logger claimed our win. At 16:05:46 the actual payment was
`0,-39,39,0`: seat 2 won from seat 1, with no point change for us.

## Corrected behavior

The tracker holds a settlement only after a complete, typed type-29 payload. For a
recognized win, a single positive seat identifies the winner; ron requires one negative
seat (the payer), tsumo three. Draws use their separate observed type-31 marker. Multiple
positive seats, unmapped announcements, and inconsistent payment patterns remain `unknown`.
No winner is inferred from a default zero or from point gain alone.

Logging waits for this settlement, preserves its actual delta, and consumes the pending
hand once. Repeated score events do not increment session statistics twice; a repeated win
screen cannot overwrite a settled result. New-hand reset clears all result evidence.
Without a complete payment event, no fabricated zero-delta row is emitted. Win round/hand
metadata uses the refreshed tracker values rather than the potentially stale in-play label.

The recap comparison also waits for settlement before deciding whether the shown hand was
ours. Unknown outcomes cannot produce tenpai/deal-in ground truth. A ron whose payer differs
from the last observed discard drops that tile and skips deal-in calibration rather than
labeling the wrong discard. Draw tenpai labels are accepted only when the draw banners are
complete at settlement; otherwise the result is simply `draw` with the correct payment.

The CSV schema remains unchanged; `unknown` is a new outcome. `ab_summary.py` reports and
excludes unknown rows. Candidate comparison refuses arms containing unknown results.
Historical hand-result and calibration CSVs are preserved: their old winner labels and
zero deltas are not repaired by this code change and should not be used as validated data.

## Validation and limits

Tests replay the six captured settlements and cover delayed payment, ron/tsumo for us and
opponents, a positive draw payment, malformed transfers, repeated events, reset, ambiguous
results, and inconsistent last-discard evidence. Type-32 recap details are tested separately
from the subsequent type-29 payment.

These changes concern observation and logging; no autoplay action, callback, delay, or
recovery rule changes are required. Client and Cartographer are unavailable, so a fresh
live-match validation remains outstanding. Rare multi-winner/exceptional settlement forms
are deliberately unclassified until captured rather than being assigned to us.

Validation completed: all 597 .NET tests pass (including normal autoplay, meld reconciliation,
and recovery regressions); Release build has zero warnings/errors. CSV analysis smoke checks
confirm that unknown rows are reported/excluded and candidate comparisons remain incomplete.
