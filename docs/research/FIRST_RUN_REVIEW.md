# First generation run review — 2026-09-21

The completed baseline is already present at
`artifacts/simulator-win-x64/runs/first`, with its packed database at
`artifacts/simulator-win-x64/databases/first`. Database checksum verification
passed. Preserve both directories and their original generator package.

| Check | Result |
| --- | ---: |
| Completed matches | 10,000 |
| Pending jobs | 0 |
| Stored decision records | 79,993 |
| Rejected samples | 7 |
| Median visits to the selected action | 53 |
| Median minimum visits across legal actions | 46 |

All seven rejections exhausted the belief budget while attempting to construct
tile-conserving tenpai hands for every riichi opponent. A low rejection count
demonstrates pipeline completion, not label accuracy. The baseline uses 256 search
iterations, 16 particles and a minimum of eight visits per legal action.

## Competitive replay coverage

Following the project owner's report that Tenhou had no concerns about the
explained project scope, a bounded sample was retrieved from the
[official rankings archive](https://tenhou.net/ranking.html):
[2024-11-13 player archive](https://tenhou.net/0/log/mjlog_pf4-20_n24.zip).

The downloaded archive contains 1,696 entries. The pilot selects the latest 20
four-player East/South Phoenix-room games with all four recorded ranks at least
seventh dan. Import produced 8,599 eligible decisions, zero duplicate games and
zero rejected files. These decisions cover discards and riichi declarations;
they are not a complete dataset of call decisions.

The first database returned **zero exact hits across these 8,599 public
snapshots** using `sim-probe`. This measures the existing exact lookup path, not
abstract retrieval, EV calibration or playing strength. The sample comes from
one player's archive and is not a representative population benchmark.

Local reproducibility artifacts are under `artifacts/tenhou-competitive/`:
`provenance.json`, `corpus/`, `public-snapshots.jsonl`, `baseline-probe.log`,
`coverage-audit.json` and `baseline-audit.json`. The artifacts are ignored by Git.

## Next compute decision

Do not start the full two-box deeper run yet. Keep this baseline for training and
comparison. Exact full-state hashes provide efficient cache access but cannot
generalize to nearby states; greater search depth does not fix cache misses.

First evaluate a learned policy/value model or validated feature-based retrieval
on held-out games. Keep the current policy as the runtime fallback. For a small
label-quality pilot, compare the same public states at the baseline budget and
at 4,096 iterations, 32 particles and 16 minimum visits, then repeat across search
seeds. Measure action stability, uncertainty and throughput before scaling.
More visits reduce some sampling noise but do not remove belief, rollout-policy
or simulator bias. Observed replay results alone are not counterfactual action EV.

When scaling becomes justified, use fresh run directories. A two-shard run with
`Matches: 10000` on **both** boxes assigns 5,000 match IDs to each box; setting
`Matches: 5000` on both assigns 2,500 each. The boxes must share the same seed,
rules, search settings and shard count, with shard indexes zero and one.
Different search configurations cannot be packed together with the baseline.
