# Offline four-player simulator and retrieval database

Real-game situations can now enter this pipeline through the
[Tenhou replay importer](REPLAY_IMPORT.md). See the
[Bakuuchi/NAGA/Suphx research notes](research/MAHJONG_AI_APPROACH.md) for the
recommended path from replay data to learned policies and better opponent models.

This experiment generates complete four-player Doman Mahjong matches, samples
decision points, evaluates their legal actions with information-set MCTS, and
packs the resulting action statistics into an immutable database. It reuses the
project's hand analysis, scoring, opponent model, danger tables and decision
guidelines. No game client, network connection or GPU is needed during generation.

## Start on the Windows compute box

Extract `simulator-win-x64.zip` into a local folder with room for the corpus and
database. The package includes its .NET runtime; the compute box does not need an
SDK or Dalamud. Open PowerShell in the extracted folder.

First run a short validation and generation trial:

```powershell
.\Precompute.exe sim-check 10 20260920
.\Precompute.exe sim-run generation.json runs/first 2 2
```

Then extend the same run to 10,000 matches using 12 workers:

```powershell
.\Precompute.exe sim-run generation.json runs/first 12 10000
```

The two completed trial matches are reused. This is the default starting profile
for a **16-core, 32 GB Windows machine**: 12 workers leave CPU headroom; each search
has 16 belief particles and a 4,096-node tree cap. There is no hard process RAM
limit, so observe Task Manager during the trial and reduce workers if necessary.
Prevent the box from sleeping during a long run.

Ctrl+C stops cleanly. Run the same command to resume. Each completed decision
sample is checkpointed atomically; an interrupted search or not-yet-checkpointed
self-play match is repeated deterministically. Worker count and the total match
target can change without invalidating completed work. Search, seed, policy and
rule changes require a **new run directory**. An exclusive lock prevents two
processes from writing the same run.

The package also contains `Run-Simulator.ps1`, a wrapper with these defaults.
The executable commands work without changing PowerShell script execution policy.

## Pack and inspect the database

Stop generation or wait for it to finish, then choose an unused database folder:

```powershell
.\Precompute.exe sim-pack databases/first runs/first
.\Precompute.exe sim-inspect databases/first
.\Precompute.exe sim-export runs/first snapshots.jsonl
.\Precompute.exe sim-probe databases/first snapshots.jsonl
```

`sim-inspect` streams and verifies both database checksums. `sim-probe` tests exact
retrieval from public snapshots and prints the best stored action and its sample
count. It uses the default opponent weights; custom-weight experiments should
query the C# API with matching weights. Export and pack refuse to overwrite their
destinations. Packing consumes completed matches, not unfinished checkpoints.

Keep both the run and packed database until verification succeeds. Disk use is
corpus-dependent: measure the trial output before scaling up. Packing needs space
for the source corpus, JSON data, temporary sorted indexes and final index.
An interrupted pack leaves a `.building-...` directory for diagnosis; the final
database appears only after a successful build. Use a new destination to retry.

## Budgets and distribution

`generation.json` contains every setting; `sim-config <new-file.json>` creates a
fresh default configuration. The baseline is 10,000 full East/South matches, up
to eight sampled decisions per match, 256 rollouts per decision, and at least
eight visits to every legal root action. Actual rollouts are the larger of 256
and `MinimumVisits * legalActionCount`. Up to 80,000 records is a target, not a
guarantee: too few eligible decisions or rejected belief proposals reduce it.

Sampling spans turn/call decisions, early/middle/late turns and riichi threats.
Forced actions and immediately winning choices are omitted from the reservoir.
This is a policy-generated distribution, not a balanced enumeration of every
possible hand. Rare hands and defensive situations may need targeted generation.

For deeper estimates, increase `Search.Iterations` to 1,024 or 4,096 and increase
particles and minimum visits as appropriate, using a new directory. Measure
throughput on the actual box before selecting a multi-day budget. The first
baseline is intended to establish coverage and performance, not strong EV labels.

`SelfPlayPolicy` and `Search.RolloutPolicy` accept `Guideline` or `Existing`.
The default guideline policy combines shanten/ukeire with the project's value,
danger, push/fold, call and riichi models. `Existing` invokes `DecisionPolicy` and
checks its recommendation against simulator legality, falling back to guidelines
when it cannot be mapped. It is usually more expensive.

For multiple boxes, copy the same configuration, set `ShardCount` to the number
of boxes and a distinct zero-based `ShardIndex` on each. Use separate run folders.
Each shard owns match IDs satisfying `id % ShardCount == ShardIndex`.

```powershell
.\Precompute.exe sim-pack databases/combined runs/shard0 runs/shard1
```

Seeds depend on the match and sample IDs, not worker scheduling. Packing verifies
compatible settings and ignores overlapping match IDs instead of multiplying
their visit counts. Resume reproducibility assumes the same simulator build;
keep the package alongside its data. Model version changes invalidate old runs.

## What is simulated

The engine tracks all 136 physical tiles, including red fives, four concealed
hands, the live wall, dead wall, replacement draws, indicators and ura indicators.
It implements discard and reaction windows; chi/pon priority; all three kan
types; riichi, double riichi and ippatsu; ron/tsumo; permanent, temporary and riichi
furiten; kuikae; scoring, honba and riichi escrow; exhaustive and abortive draws;
nagashi mangan; and dragon/wind responsibility payments. Matches include dealer
continuations, bankruptcy, all-last termination and initial-seat tie breaking.

The rule profile follows the project's Doman conventions and the
[official special-rules guide](https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/doman-mahjong/special_rule/).
Furiten and call restrictions also reference the
[official FAQ](https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/doman-mahjong/faq/).
The implementation has unit and conservation tests, but has **not** been
differentially validated against a reference engine or complete game-client
traces. Double-wind pair fu is configurable (default four); kan indicators are
revealed when the kan completes. Real-time match limits are not simulated.

The engine fails if a hand/match guard is exceeded; it never silently labels a
truncated rollout as a completed game. Tile and point conservation are checked
throughout self-play by default and at the end of every search rollout.

## Beliefs, search and retrieval

Policies receive only their own hand and public information. Training reconstructs
hidden opponents and wall from that observation; it does not use the original
self-play opponents' concealed hands as privileged inputs. Riichi particles must
be tenpai, visible physical tile counts must agree, and a bounded swap procedure
conditions proposals toward the existing opponent model's tenpai estimates.
Impossible or unsatisfied proposals are recorded as explicit rejected samples,
not zero-return labels. Check the `rejected` count before investing in a large run.

These proposals are **approximate beliefs**, not an exact Bayesian posterior.
Discard likelihoods and unknown temporary furiten are not fully reconstructed.
Opponents use observation-based policies, not independently trained human models.
Particles may be correlated; visit counts are rollout counts, not proof of
independent evidence or statistical confidence.

Information-set UCT shares decisions by public/own observations, expands the
acting player's decisions, and rolls out opponents with the selected policy.
Every rollout reaches the **end of the current hand**. Utility defaults to point
change, including deposits and settlement. Optional `Search.PlacementWeight`
adds a hand-end rank-change term; it is not full-match placement EV. Self-play
provides full-match contexts, but the search does not look through future hands.

Each record contains a public snapshot, model/profile identity, exact and
experimental abstract keys, seed, and per-action visits, mean utility, utility
M2, mean point change, wins, deal-ins, draws and hand-analysis summaries.

The exact key reuses the existing component-based incremental state hash with a
separate verification digest. Hash equality identifies the same state;
**differences between cryptographic hashes do not measure strategic similarity**.
A separate abstraction groups structural hand/context features for research.
It preserves enough detail that coverage is still sparse. Approximate retrieval
requires a held-out quality/coverage evaluation before use in live decisions.

The packed format is `manifest.json.gz`, `data.bin` and `index.bin`. A sorted
48-byte index entry stores a SHA-256 key, data offset, length and visit count;
the index starts with a 16-byte header. External sorting bounds packing buffers.
Lookup binary-searches the index on disk and reads only the selected JSON record;
the full corpus is never loaded into RAM. Duplicate lookup keys select the most
sampled record; they are not pooled into misleading independent sample counts.

## Use from the plugin

Copy the **entire verified database folder** to
`pluginConfigs/MahjongHater/simulation_policy`, enable **Experimental precomputed
policy**, save and reload the plugin. To ship it with the plugin instead, put the same
folder at `resources/policy/simulation_policy` before building; the plugin searches the
config folder first, then next to the DLL, then `resources/policy`. The simulator database takes precedence
over the earlier `precomputed_policy.json` table. The plugin checks the model and
rule/weight profile before loading it.

Offline records cover calls, declarations and open hands, but the current live
adapter uses only exact matches for verified closed-hand ordinary discard
positions, with complete legal-action coverage and at least eight visits per
action. Other positions and misses use the existing policy. The experimental
abstract index is not automatically used in live play. Exact matches from random
self-play will be rare; producing a large database alone does not solve coverage.
The records also provide a starting corpus for later abstraction evaluation or
policy distillation; no neural policy training is included yet.

## Build and validate from source

Requires the .NET 10 SDK. From the repository root:

```powershell
dotnet test MahjongHater.Tests -c Release
dotnet run --project tools/Precompute -c Release -- sim-check 100 20260920
dotnet run --project tools/Precompute -c Release -- sim-config generation.json
dotnet run --project tools/Precompute -c Release -- sim-run generation.json runs/first 2 2
powershell -ExecutionPolicy Bypass -File tools/publish_simulator.ps1
```

The publishing script creates `artifacts/simulator-win-x64.zip`. It refuses to
replace an existing package; pass `-OutputDirectory` for subsequent builds. It
does not commit, tag, push or modify the main branch. The simulator source lives
in `Core/Simulation`; CLI commands in `tools/Precompute/SimulationCommands.cs`;
and rule/search/persistence regression tests in `MahjongHater.Tests/Simulation`.

The older `example`, `train` and `probe` commands remain available for the initial
short-horizon discard proxy. Use the `sim-*` commands for this four-player engine.
