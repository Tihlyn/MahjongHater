"""Build resources/policy/deal_in_rates.json from the research CSVs.

Source: "Path of Houou", Analysis - Tile deal-in rates by live suji (1.2 M Houou games),
docs/research/houou_dealin_*.csv (see docs/research/README.md). Every rate is the chance that a
discard of that class deals into ONE seat that is in riichi, indexed by how many of that seat's
18 suji are still live (column 18 down to 0; null = no data). The C# side
(Core/Policy/DealInRateTable.cs) never contains a number that belongs here.

Usage: python tools/make_deal_in_rates.py
"""
from __future__ import annotations

import csv
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RESEARCH = ROOT / "docs" / "research"
OUT = ROOT / "resources" / "policy" / "deal_in_rates.json"


def pct(cell: str) -> float | None:
    cell = cell.strip()
    if not cell or cell.upper() in {"N/A", "NO DATA"}:
        return None
    return round(float(cell.rstrip("%")) / 100.0, 5)


def read(path: Path) -> dict[str, dict]:
    rows: dict[str, dict] = {}
    with path.open(newline="", encoding="utf-8") as f:
        reader = csv.reader(f)
        header = next(reader)
        assert header[1] == "Total" and header[2] == "18" and header[-1] == "0", header
        for line in reader:
            if not line or not line[0].strip():
                continue
            rows[line[0].strip()] = {
                "total": pct(line[1]),
                "byLiveSuji": [pct(c) for c in line[2:21]],  # index 0 = 18 live suji ... 18 = 0
            }
    return rows


def smooth(values: list[float | None], window: int = 1) -> list[float | None]:
    """Centred moving average over sampled neighbours (null stays null). The 18/17 live-suji
    columns hold a few dozen samples each and swing by ±2 points; the average keeps the shape."""
    out: list[float | None] = []
    for i, v in enumerate(values):
        if v is None:
            out.append(None)
            continue
        neighbours = [values[j] for j in range(max(0, i - window), min(len(values), i + window + 1)) if values[j] is not None]
        out.append(round(sum(neighbours) / len(neighbours), 5))
    return out


def numbers(rows: dict[str, dict]) -> dict:
    # Category name in the CSV -> key used by the C# lookup.
    categories = {
        "Non-suji": "nonSuji",
        "Suji": "suji",
        "Riichi suji": "riichiSuji",
        "Half suji": "halfSuji",
        "Half suji via riichi tile": "halfSujiRiichi",
        "Nakasuji with riichi tile": "nakasujiRiichi",
    }
    out: dict[str, dict[str, dict]] = {v: {} for v in categories.values()}
    pattern = re.compile(r"^(.*?)\s+(\d)$")
    for name, row in rows.items():
        m = pattern.match(name)
        if not m:
            raise SystemExit(f"unrecognised number row: {name}")
        key = categories[m.group(1)]
        out[key][m.group(2)] = {"total": row["total"], "byLiveSuji": smooth(row["byLiveSuji"])}
    # The sheet's plain "Suji 4/5/6" rows are nakasuji (both sides safe) and are the
    # non-riichi-tile counterpart of "Nakasuji with riichi tile".
    out["nakasuji"] = {n: out["suji"][n] for n in ("4", "5", "6")}
    for n in ("4", "5", "6"):
        del out["suji"][n]
    return out


def honor_shape(rows: dict[str, dict]) -> list[float]:
    """Honor danger grows as suji die but each honor row is thin per column; take the shape
    (rate / total) from the best-sampled row, Dragon with 0 visible, smoothed and clamped."""
    row = rows["Dragon with 0 visible"]
    total = row["total"]
    shape = []
    for v in smooth(row["byLiveSuji"], window=2):
        shape.append(round(min(3.0, max(0.4, v / total)), 4) if v is not None and total else 1.0)
    return shape


def honors(rows: dict[str, dict]) -> dict:
    # "Dora Seat Wind with 2 visible" -> honors[dora=true][seat][2]
    pattern = re.compile(r"^(Dora )?(Double Wind|Dragon|Guest Wind|Round Wind|Seat Wind) with (\d) visible$")
    keys = {"Double Wind": "double", "Dragon": "dragon", "Guest Wind": "guest", "Round Wind": "round", "Seat Wind": "seat"}
    out = {"plain": {k: [None] * 4 for k in keys.values()}, "dora": {k: [None] * 4 for k in keys.values()}}
    for name, row in rows.items():
        m = pattern.match(name)
        if not m:
            raise SystemExit(f"unrecognised honor row: {name}")
        group = "dora" if m.group(1) else "plain"
        out[group][keys[m.group(2)]][int(m.group(3))] = row
    return out


def main() -> None:
    doc = {
        "source": "Path of Houou, 'Analysis - Tile deal-in rates by live suji' (May 2020), 1.2M Tenhou Houou games; "
                  "https://pathofhouou.blogspot.com/2020/05/analysis-tile-deal-in-rates-by-live-suji.html",
        "retrieved": "2026-09-20",
        "semantics": "P(deal in | discard this tile, target seat is in riichi). byLiveSuji[i] is for 18-i live suji "
                     "of the target seat; null = no sample. Multipliers come from Fukuchi Makoto, 'Riichi Mahjong "
                     "Strategy' ch. 2 (kabe, early-outside, dora) and are applied by the C# model.",
        "numbers": numbers(read(RESEARCH / "houou_dealin_numbers_by_live_suji.csv")),
        "honors": honors(read(RESEARCH / "houou_dealin_honors_by_live_suji.csv")),
        "honorShape": honor_shape(read(RESEARCH / "houou_dealin_honors_by_live_suji.csv")),
        "multipliers": {
            "earlyOutside": 0.6,
            "doraTile": 1.3,
            "doraTerminal": 1.7,
            "doraNeighbour": 1.1,
            "oneChanceEarly": 0.75,
            "oneChanceLate": 1.0,
            "doubleOneChance": 0.5,
            "oneChanceLateLiveSuji": 8,
            "visibleCopies": [1.0, 0.95, 0.85, 0.6],
            "honorKokushiFloor": 0.002,
        },
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
