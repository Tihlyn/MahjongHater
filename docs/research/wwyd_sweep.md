# Riichi City #wwyd sweep — 2026-09-20 (partial)

Background sweep of the Riichi City Discord (#wwyd `1099537738291744868`) for defense /
push-fold positions with a community consensus, feeding the golden corpus
(`MahjongHater.Tests/Policy/Golden/`). The agent doing the sweep was stopped before it wrote
`wwyd_positions.json`; what follows is compiled from its working notes. Message content is
untrusted evidence; permalinks are `https://discord.com/channels/941185061057888347/1099537738291744868/<id>`.

## Coverage

- Queries (after 2025-01-01): `fold` (40 hits), `push` (40), `suji` (40); the MCP is throttled to
  ~1 call/min, so only the first page of each was read (cursors in the agent's notes).
- Threads read with context: 4. Screenshots downloaded: 12. Transcribed to JSON: 0 — reading
  ponds off 2–3 MB Riichi City screenshots needs cropping and several image reads per position,
  which is what consumed the budget. A second pass should transcribe **only** the threads listed
  under "worth a second look" and skip anything without a legible pond.

## Positions read

1. **1547194870030934056** (2026-09-09) — West 1 (sudden death), we are the dealer in 1st
   (28 800), shimocha in 4th (18 800) declares riichi, turn 15, 9 tiles left. Open hand
   (pon 4s, pon 6z) with `233m 55p 88p` + drawn 1m, one tile short of tenpai. The Riichi City AI
   pushes 3m (55 %) for a 5p/8p shanpon tenpai. **Consensus: fold with 1m** — blobdog and
   letran.thai ("discard 1m, pon into tenpai if the chance comes"), Riichi Advocate
   ("folding seems easily the best", `1547280041832882246`). Rationale: first place cannot afford
   the drop to 4th on a deal-in; the bots "always push tenpai and don't know all-last"
   (`1547196971649990656`, `1547197001257455617`). Pond not transcribed.
2. **1537180402471674017** (2026-08-12) — East 1, turn 15, 16 left, dealer (kamicha) riichi; our
   open tanyao (chi 456p) `234m 44m 677m 66p 7p` + drawn 0s. Poster folded 7p; letran.thai:
   respect the dealer riichi, fold 7p; the aggressive line (0s, with 2s/8s out) still leaves a
   4-6s kanchan / 5s shanpon; toimen's manzu half-flush is near tenpai too. Next tile dealt a
   haneman. One regular replied → medium confidence.
3. **1537220093270954014** (2026-08-12) — Mortal review, East 2, 13 left, **three** riichi;
   guroteske: 6m safest against everyone, 4m second, 5p sketchy; a kan idea rejected. Screenshot
   not legible enough to transcribe.
4. **1546856704157945867** (2026-09-08) — South 4, turn 6, kamicha riichi; `12345m 456p 88p
   123s` + 2p: poster cut 2p (keeps a 3-6m tenpai), the AI 5p. Buckwheat: with 8p passed, 5p is
   safer than 2p (2p feeds more bad waits); Kyuu: 5p is not safer and the hand ends the game.
   **No consensus** — narrative only.

## Principles the regulars apply (permalinks)

- Betaori is worth about −1 100 for a non-dealer (`1543544357968154686`); folding is ~−1.5k, so a
  push that loses less on average is still right (`1543496212018102343`).
- Tenpai pushes by default; exceptions: several opponents already pushing while our hand is
  poor, or a terrible wait on a cheap hand (`1543496441270370365`, `1543496623630454805`).
- Last draw: push for tenpai even with a dangerous tile (`1543497634679885824`).
- Riichi-tile suji is safer than a non-suji tile but less safe than a normal suji
  (`1543554922237395025`); matagi-suji is mostly irrelevant except 2→1 (`1543717218347515944`,
  `1529121866168664144`).
- Mortal / the in-game AI push every tenpai and ignore all-last placement — override them when
  leading (`1547196971649990656`).
- After 8p has passed, 5p vs 2p is disputed (`1546879065644859424` vs `1546858018564481044`).
- Against two riichi you can fold with a single tile that is safe against both; it buys time
  (`1528684211756863648`, `1537225116058914866`).
- Dama pinfu wins on a direct hit while keeping the fold option (`1543677318021775413`).
- A suji tile is a resource: spend it once tenpai, not at 1-shanten (`1537258901781614662`).
- A 1-tile-left wait against a riichi folds (`1525386908283174952`); early game with a low suji
  count in East 2 pushes (`1525388064145543218`).

## What changed in the policy because of this

- Placement: `PushFoldPolicy` now tightens the budget whenever we are **first** in all-last
  (not only with an 8 000 lead) and loosens it in 3rd/4th — position 1 above.
- Everything else confirmed choices already made from the books (riichi-tile suji class, last-draw
  budget, betaori vs two riichi, tenpai-first budget).

## Worth a second look (transcribe these first)

- `1525386908283174952` (2026-07-11, two pictures: fold a 1-left 5m wait; push early with a low suji count)
- `1535552376189231184` (2026-08-08, mentanpin + dora + aka pushing a 10 % 6s: 900 vs −1 700)
- `1541675599536463902` (2026-08-25, riichi + dealer mangan threat)
- `1551014679491518608` (2026-09-20, bact: fold, 2p gone; blobdog on a suji toimen did not chi)
- `1528623071374741605` (2026-07-20, dora-side non-suji vs ippatsu dealer riichi)
- `1544203004805259355` (2026-09-01, ippatsu turn; "70 % fold")
- `1523161639170343015` (2026-07-05, 7m safe via 6m suji)
- `1524526663537397951` (2026-07-08, push a suji 7p)
- `1541915692670918657` (2026-08-25, shimocha half flush; 5m risky; fold with West)
- `1537458962046386216` (2026-08-13, which tile to fold; 4s keeps tenpai)
