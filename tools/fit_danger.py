"""Calibrate the tile danger model against recorded deal-ins.

Input: dealin_calibration.csv written by the plugin at every hand end
(%AppData%\\XIVLauncher\\pluginConfigs\\MahjongHater\\dealin_calibration.csv). One row per
(own discard, threatening seat): the model's conditional deal-in estimate at the time
(`predicted`, P(deal in | that seat is tenpai)), its class/rank, the seat's live-suji count,
the tenpai estimate for that seat, and whether the seat ron'd the tile (`dealtIn`).

The model's base rates are Tenhou Houou measurements against riichi (docs/DEFENSE_PLAN.md);
this script measures how they fit the opponents we actually meet:

  * riichi rows: observed deal-in rate per class / rank / live-suji bucket vs the mean
    prediction, with a shrunk ratio (observed+prior)/(predicted+prior) per class that can
    be pasted into resources/policy/deal_in_rates.json as "populationMultipliers";
  * open-hand rows (no riichi): the same, but the label is confounded by the tenpai
    estimate, so the report uses predicted × tenpai as the reference;
  * overall log-loss / Brier of predicted (× tenpai for non-riichi rows) vs a constant.

    python tools/fit_danger.py [path/to/dealin_calibration.csv] [--population human] [--model V2] [--prior 20]
"""
import csv
import json
import math
import os
import sys
from collections import defaultdict


def default_path():
    return os.path.join(os.environ.get("APPDATA", ""), "XIVLauncher", "pluginConfigs", "MahjongHater", "dealin_calibration.csv")


def load(path, population=None, model=None):
    rows = []
    with open(path, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            if population and r.get("population", "") != population:
                continue
            if model and r.get("model", "") != model:
                continue
            rows.append({
                "riichi": r["riichi"] == "1",
                "tenpai": float(r["tenpai"]),
                "cls": r["class"],
                "rank": r["rank"],
                "predicted": float(r["predicted"]),
                "live": int(r["liveSuji"]) if r["liveSuji"] not in ("", "-1") else None,
                "dealt": r["dealtIn"] == "1",
                "tile": r["tile"],
            })
    return rows


def score(rows):
    """Log-loss and Brier of the reference probability vs a constant baseline."""
    ll = br = 0.0
    n = 0
    base_rate = sum(r["dealt"] for r in rows) / max(1, len(rows))
    ll0 = br0 = 0.0
    for r in rows:
        p = min(0.999, max(0.001, r["predicted"] * (1 if r["riichi"] else r["tenpai"])))
        y = 1 if r["dealt"] else 0
        ll -= y * math.log(p) + (1 - y) * math.log(1 - p)
        br += (p - y) ** 2
        p0 = min(0.999, max(0.001, base_rate))
        ll0 -= y * math.log(p0) + (1 - y) * math.log(1 - p0)
        br0 += (p0 - y) ** 2
        n += 1
    n = max(1, n)
    return ll / n, br / n, ll0 / n, br0 / n, base_rate


def table(rows, key, prior):
    groups = defaultdict(list)
    for r in rows:
        groups[key(r)].append(r)
    out = []
    for k, g in sorted(groups.items(), key=lambda kv: str(kv[0])):
        n = len(g)
        obs = sum(r["dealt"] for r in g)
        pred = sum(r["predicted"] * (1 if r["riichi"] else r["tenpai"]) for r in g)
        ratio = (obs + prior * pred / max(n, 1)) / (pred + prior * pred / max(n, 1)) if pred > 0 else float("nan")
        out.append((k, n, obs / n if n else 0, pred / n if n else 0, ratio))
    return out


def live_bucket(live):
    if live is None:
        return "?"
    if live >= 13:
        return "13-18"
    if live >= 9:
        return "9-12"
    if live >= 5:
        return "5-8"
    return "0-4"


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    opts = {a.split("=")[0][2:]: (a.split("=")[1] if "=" in a else True) for a in sys.argv[1:] if a.startswith("--")}
    # also accept "--population human" style
    argv = sys.argv[1:]
    for i, a in enumerate(argv):
        if a in ("--population", "--model", "--prior") and i + 1 < len(argv):
            opts[a[2:]] = argv[i + 1]
            if argv[i + 1] in args:
                args.remove(argv[i + 1])
    path = args[0] if args else default_path()
    prior = float(opts.get("prior", 20))
    rows = load(path, opts.get("population"), opts.get("model"))
    if not rows:
        print(f"no rows in {path}")
        return
    riichi_rows = [r for r in rows if r["riichi"]]
    open_rows = [r for r in rows if not r["riichi"]]
    print(f"{len(rows)} rows ({len(riichi_rows)} vs riichi, {len(open_rows)} vs open/dama), "
          f"population={opts.get('population', 'all')} model={opts.get('model', 'all')}")

    ll, br, ll0, br0, base = score(rows)
    print(f"log-loss {ll:.4f} (constant {ll0:.4f})   Brier {br:.4f} (constant {br0:.4f})   base rate {base:.3%}")

    def show(title, rows_, key):
        print(f"\n{title}")
        print(f"{'group':<28}{'n':>6}{'observed':>11}{'predicted':>11}{'ratio':>8}")
        for k, n, obs, pred, ratio in table(rows_, key, prior):
            print(f"{str(k):<28}{n:>6}{obs:>10.2%}{pred:>10.2%}{ratio:>8.2f}")

    if riichi_rows:
        show("vs riichi - by class", riichi_rows, lambda r: r["cls"])
        show("vs riichi - by rank", riichi_rows, lambda r: r["rank"])
        show("vs riichi - by live suji", riichi_rows, lambda r: live_bucket(r["live"]))
    if open_rows:
        show("vs open / dama (prediction x tenpai estimate) - by class", open_rows, lambda r: r["cls"])

    if riichi_rows:
        multipliers = {k: round(ratio, 3) for k, n, obs, pred, ratio in table(riichi_rows, lambda r: r["cls"], prior) if n >= 30 and ratio == ratio}
        print("\npopulationMultipliers (paste into resources/policy/deal_in_rates.json once classes have >= 30 rows):")
        print(json.dumps(multipliers, indent=1))
        thin = [k for k, n, *_ in table(riichi_rows, lambda r: r["cls"], prior) if n < 30]
        if thin:
            print(f"thin classes (< 30 rows, not emitted): {', '.join(map(str, thin))}")


if __name__ == "__main__":
    main()
