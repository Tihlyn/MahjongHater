# Real-game replay import

The standalone tool imports Tenhou `mjloggm` XML, gzip-compressed `.mjlog` files,
ZIP archives and directories. It produces an auditable corpus of public decision
observations, recorded actions, separate supervised targets and source-game hashes.
It can then run the existing information-set search on those observations and
pack a retrieval database using the existing `sim-pack` command.

This is the **Tenhou format** adapter. Native Mahjong Soul replay URLs/protobuf
records are not decoded yet. Do not rename a Soul JSON/protobuf file to `.mjlog`.
The maintained [Mahjong Soul API wrapper](https://github.com/MahjongRepository/mahjong_soul_api)
documents the protobuf and authentication work a separate adapter would require.

## Data sources

[Tenhou's official rankings page](https://tenhou.net/ranking.html) links replay
archives, but public availability does not mean unrestricted training reuse.
Its [archive notice](https://tenhou.net/sc/raw/) directs general-mahjong applications
to contact its operator. On 2026-09-21, the project owner reported having contacted
Tenhou with this project's scope and receiving no concerns. We proceed within
that stated scope; this is not a claim of unrestricted third-party redistribution.
The importer itself works with local files and does not authenticate to accounts.

`tools/fetch_replay_samples.ps1` downloads three small system-test fixtures from
[MahjongRepository/tenhou-python-bot](https://github.com/MahjongRepository/tenhou-python-bot),
pinned to revision `df83948546d424ca8c2abd2e48aba72da1e224d3`. It saves URLs,
checksums and the upstream MIT notice. These fixtures include mixed ranks and
bot/test games; they validate the parser and are **not** an elite training set.
The bit-packed meld layout was checked against that project's `tenhou/decoder.py`.

```powershell
powershell -ExecutionPolicy Bypass -File tools/fetch_replay_samples.ps1
```

## Import on the compute box

Use the new portable package, or substitute
`dotnet run --project tools/Precompute -c Release --` for `.\Precompute.exe`.
Copy the configuration from your simulator run so target rules agree:

```powershell
.\Precompute.exe replay-import generation.json raw-logs corpus/competitive 16 [workers]
.\Precompute.exe replay-inspect corpus/competitive
```

The rank argument is the minimum **acting player's** Tenhou rank code:
`10` = first dan, `13` = fourth dan, `16` = seventh dan, `19` = tenth dan,
`20` = Tenhoui. Default is `16`; `0` disables rank filtering for parser fixtures.
An opponent at a different rank does not cause the whole game to be rejected.
Ranks alone do not prove that a file is authentic or that every participant is human.
`workers` (default: cores − 1) parses games in parallel; archive reading and
de-duplication stay sequential, so the corpus is identical whatever the worker count
(four Phoenix archives, 21 011 games: 9 min on 10 workers).

The importer checks physical tiles, draw/discard/call order, concealed hand sizes,
riichi payments and settlement base scores. It keeps only complete matches with
final `owari` results. It rejects three-player/no-red/special formats, incompatible
East-only/East-South or kuitan settings, malformed/unknown events and incomplete
matches. The manifest lists file rejection reasons and skipped-decision counts.
Empty filtered corpora are still published with their diagnostic manifest, and
the CLI returns failure rather than training zero rows unnoticed.

It normalizes XML formatting and excludes display names/shuffle seeds from the
game identity. Duplicate games are counted once even when gzip/ZIP wrappers or
whitespace differ. Each game is written as a checksummed gzip file. ZIP members
are streamed, never extracted using their paths. Individual inputs/decompressed
XML are bounded to 32 MiB; XML external entities and DTDs are prohibited.
Import refuses an existing destination or a destination inside its source folder.
Interrupted imports leave a `.building-...` diagnostic directory; import is not
resumable, whereas subsequent search generation is.

## Produce search labels from the real situations

```powershell
.\Precompute.exe replay-run generation.json corpus/competitive runs/replays 12 100
.\Precompute.exe replay-run generation.json corpus/competitive runs/replays 12 10000
.\Precompute.exe sim-pack databases/replays runs/replays
.\Precompute.exe sim-inspect databases/replays
```

The final argument limits **source games**, capped at the corpus size.
`StatesPerMatch` controls the number of sampled decisions per source game.
The sampler balances public open/closed-hand, turn and riichi-threat contexts;
it does not select based on whether the player later won. Search uses the config's
iterations, particles, rollout policy and weights. Recorded human actions are
not forced into search and recorded outcomes are not copied into EV statistics.

Workers, checkpointing, resume and shard semantics match `sim-run`. For two boxes,
copy the **same corpus folder** and same config; set `ShardCount=2` and indexes
`0` and `1`, and use different run folders. Pack their completed jobs together.
The corpus fingerprint is part of run identity. A different corpus, changed
search settings or a simulator-only run cannot be silently merged into this run.
Keep the source corpus with its training artifacts for provenance.

Existing self-play run fingerprints remain unchanged, including runs already in
progress before this importer was added. No replacement of their packages or
configuration is required.

## Export supervised learning data

```powershell
.\Precompute.exe replay-export corpus/competitive train.jsonl train
.\Precompute.exe replay-export corpus/competitive validation.jsonl validation
.\Precompute.exe replay-export corpus/competitive test.jsonl test
```

An approximately 80/10/10 split is assigned by **whole-game hash**. Every seat,
hand and decision from the same game stays together. Small corpora may have an
empty partition. This split is not also a player-disjoint or chronological split.

Each JSONL row has:

- `Inputs`: actor's own hand, visible discards/melds/dora, public context and
  private information known to that actor, such as their own temporary furiten.
  Opponents' hands, wall order, shuffle seeds and ura are absent.
- `LegalActions`: target-rule actions available at that decision.
- `Targets.HumanAction`: the recorded discard / riichi-discard choice, or on a claim
  window the recorded reaction (`Pass`, `Chi`, `Pon`, `OpenKan`).
- `Targets.ObservedHandDelta`: the recorded hand-end point change from this
  snapshot; this is one observed return under source rules, **not action EV**.
- `Targets.ObservedFinalPlacement`: recorded match placement under source rules.
- `Targets.Opponents`: relative seats 1-3, concealed-hand-derived tenpai and
  shape wait masks, furiten, and ron payments for each ordinary tile kind under
  the target Doman scoring profile. These payments exclude ura and honba, are
  zero when ron is unavailable, and are **not** deal-in probabilities or EV.

Opponent hidden information is permitted only in those supervised targets.
Feed only `Inputs` and legal actions to a policy/opponent predictor. Search
receives only the observation and resamples its own hidden-state particles.
The parser never learns future walls from the log's shuffle seed.

## Current scope and rule differences

Decision samples cover **discard and riichi-discard decisions**, including open-hand
turns, and since corpus importer `tenhou-decisions-v2` the **claim-window reactions**:
for every discard, each other seat that could legally chi, pon or open-kan it gets a
decision whose observation is the simulator's `DiscardResponses` phase for that seat and
whose action is the recorded call (composition from the meld event) or `Pass`. Windows
whose only option is pass carry no decision; windows where ron was legal are skipped as
available wins (about 26 % of a corpus's decisions are reactions; 16 % of them are calls).
Own-turn closed/added kan choices are still not emitted. Forced moves and positions
with an available immediate win are skipped. Source extension rounds and states
waiting for a delayed source kan-dora reveal are also skipped. These filters are
visible in the manifest and bias the exported training distribution accordingly.

This is a transfer of real situations into the **Doman simulator**, not a complete
Tenhou rules emulator. Source point outcomes and final placements stay explicitly
separate from Doman counterfactual values. Match-end rules, special yakuman, kan
details and rank incentives can differ. Rank-aware learning must account for that
domain difference before deployment.

No neural model is trained or activated by these commands. They prepare the
policy, opponent and placement targets needed for the approach described in
[the research notes](research/MAHJONG_AI_APPROACH.md). Retraining opponent models,
policy learning, distillation and measured live strength are subsequent work.
