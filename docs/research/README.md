# Research data for the defense rework

Raw inputs referenced by [`../DEFENSE_PLAN.md`](../DEFENSE_PLAN.md). Kept verbatim so the
phase-1 tables (`resources/policy/deal_in_rates.json`) can be regenerated and audited.

- `houou_dealin_numbers_by_live_suji.csv`, `houou_dealin_honors_by_live_suji.csv` — deal-in rate
  of a discard against a single riichi, by tile class and the number of live suji for that
  riichi, from 1.2 M Tenhou Houou-room games. Source: "Path of Houou" blog, *Analysis – Tile
  deal-in rates by live suji* (May 2020),
  <https://pathofhouou.blogspot.com/2020/05/analysis-tile-deal-in-rates-by-live-suji.html>
  (public Google Sheets exported as CSV on 2026-09-20). Columns: `Total` then live-suji count
  18 → 0; `N/A` / `No Data` = no samples. Honor rows are split by yakuhai status for the riichi
  seat (Double/Seat/Round/Guest wind, Dragon) × copies visible, with and without dora.
