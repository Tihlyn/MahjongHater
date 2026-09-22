# Rule cross-check against the official Doman Mahjong ruleset, 2026-09-22

Sources: the Lodestone [Mahjong Rules](https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/doman-mahjong/),
[Yaku List](https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/doman-mahjong/yaku_list/),
[Advanced Rules](https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/doman-mahjong/special_rule/)
and [Points to Remember](https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/doman-mahjong/faq/),
read 2026-09-22, plus the 23 win screens the client printed in that day's logs.

**Result: no rule discrepancy found.** Every stated rule matches the implementation, and the
payout table reproduces all 23 observed payouts exactly. Two items the Lodestone does not
state are now checked against the game at runtime instead of assumed.

## Yaku values (Yaku List → `Core/YakuDetector.cs`)

| Yaku | Official | Ours |
| --- | --- | --- |
| Riichi / Menzen Tsumo / Ippatsu / Yaku tiles / Tanyao / Pinfu / Pure Double Chi / After a Kan / Robbing a Kan / Last-tile Draw / Last-tile Claim | 1 | 1 ✓ |
| Double Riichi | 2 | 2 ✓ |
| Triple Chi, Pure Straight, Outside Hand | 2 closed / 1 open | 2 / 1 ✓ |
| All Terminals and Honors, Little Three Dragons, All Pon, Triple Pon, Three Concealed Pon, Three Kan, Seven Pairs | 2 | 2 ✓ |
| Twice Pure Double Chi | 3 | 3 ✓ |
| Terminals in All Groups, Half Flush | 3 closed / 2 open | 3 / 2 ✓ |
| Full Flush | 6 closed / 5 open | 6 / 5 ✓ |

## Yakuman (Advanced Rules → `YakuDetector`)

"Only the twelve yakuman below are permitted": Blessing of Heaven, Blessing of Earth, Big
Three Dragons, Four Concealed Pon, All Honors, All Green, All Terminals, Thirteen Orphans,
Big Four Winds, Little Four Winds, Four Kan, Nine Gates.

We implement exactly those twelve, each at 13 han. **Blessing of Man (renhou) is correctly
absent.** The page's "includes Four Concealed Pon on Pair Wait / Pure Thirteen Orphans /
Pure Nine Gates" means those are the same single yakuman, not double — and we award no
upgrade for them. (Multiple *distinct* yakuman in one hand still stack, which the page
neither grants nor forbids.)

## Rules that gate actions

| Rule (source) | Where it lives | Status |
| --- | --- | --- |
| "Riichi may be declared when a player has a minimum of 1,000 points" | live: the game's own offer gates it (`state.Can(Riichi)`); simulator: `RiichiSimulator.cs` `p.Score >= 1000` | ✓ |
| "After declaring riichi, a player may only form a concealed kan when it has no effect on their number of waits" | `RiichiSimulator` compares the wait set before/after and refuses added kan in riichi | ✓ |
| "It is not possible to call chi, pon, or kan on the last tile" | `RiichiSimulator` blocks responses at `LiveWall.Count == 0` | ✓ |
| Kuikae — no claiming then discarding the identical tile; no calling to swap one meld for another | simulator kuikae rules | ✓ |
| Furiten: discard-based, temporary on a declined claim, permanent for the hand after riichi | `SimScoring.Furiten` covers all three | ✓ |
| Thirteen Orphans can be furiten on a 13-sided wait | generic wait/river check, so it applies | ✓ |
| Abortive draws: nine terminals, triple ron, four kan, four same wind | `HandEnd.NineTerminals / TripleRon / FourKans / FourWinds` | ✓ — and **no four-riichi abort**, which the page does not list |
| East and South rounds only; full = 8 hands, quick = 4 | `GameLength`, `HandsInMatch is 4 or 8` | ✓ |
| 25,000 starting points, 136 tiles, three red fives (one per suit) | `StartingScore = 25000`; red fives modelled one per suit | ✓ |
| 120-minute match limit, last hand after 80 minutes | not modelled | documented gap, no decision impact |

## Scoring: checked against the game, not the documentation

The Lodestone states no fu table and no limit rounding. The win screen does:
`[6]="40 Fu 3 Han [Mangan]"`, `[7]` = points ÷ 100, `[3]` = 1 when the winner is the dealer,
`[8]` = 1 on tsumo (all four confirmed across 23 screens).

Replaying every one of those through `ScoringEngine`:

```
23/23 agree — 20/25/30/40/50 fu; 1–5 han; dealer and non-dealer; ron and tsumo
  40 Fu 4 Han Mangan  non-dealer ron    game 8000   ours 8000
  50 Fu 2 Han         dealer ron        game 4800   ours 4800
  40 Fu 3 Han         dealer ron        game 7700   ours 7700
  20 Fu 4 Han         dealer tsumo      game 7800   ours 7800
  25 Fu 5 Han Mangan  dealer tsumo      game 12000  ours 12000
  …
```

So the base-point table, the mangan boundary at 4 han 40 fu, the dealer ×6 / ×4 multipliers
and the round-up-to-100 rule are all confirmed against the client.

**The one untested boundary is round-up ("kiriage") mangan** — 4 han 30 fu and 3 han 60 fu.
No such hand appeared in 23 wins. We keep the standard rule (7,700, not 8,000). Rather than
guess, `EmjStateReader.CheckScoringAgainstTheGame` now compares our table against every win
screen the client prints, for all four seats, and logs a `[Rules] SCORING MISMATCH` warning
the first time they differ. `ScoringRulesTests` pins the observed rows as regression data.

### One apparent inconsistency in the official pages

The Yaku List calls Nagashi-mangan **4 han**, while Advanced Rules states its award as
**12,000 dealer / 8,000 non-dealer** — which is mangan, i.e. what 5 han (or 4 han under
round-up) pays, not the 11,600 / 7,700 that 4 han 30 fu pays under standard rules. The
simulator encodes it as 5 han at 30 fu, so it pays the documented amount. Either the yaku
list's han is a display convention or the game applies round-up; the live check above will
tell us which, and nothing depends on it today.

## Unstated by the Lodestone, so still ours to choose

- **Double-wind pair fu** (a seat-and-round wind pair worth 2 or 4 fu). Configurable,
  default 4. Not stated anywhere official; the win screens give fu but not the hand, so it
  cannot be settled from the logs alone. Settling it needs our own winning hand compared
  against the printed fu — the natural next step for the same oracle.
- Oka/uma placement bonuses, noten penalty amounts, dealer repeat rules, kan dora and ura
  dora timing: not stated on these pages. They are implemented to standard rules and are
  untouched by this check.
