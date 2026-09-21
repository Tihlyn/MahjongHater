"""Compare defense models / opponent populations from the per-hand result log.

Input: hand_results.csv written by the plugin at every hand end
(%AppData%\\XIVLauncher\\pluginConfigs\\MahjongHater\\hand_results.csv), one row per hand:
outcome (win-ron, win-tsumo, dealin, tsumo-loss, other-ron, draw-tenpai, draw-noten, draw),
our point delta, whether we were in riichi, how many opponents were, and the tags
`population` (human / npc, from the config toggle) and `model` (V2 / Legacy).

Prints, per (model, population): hands, win rate, deal-in rate, tsumo-loss rate, draw-tenpai
rate, mean and total delta, and — the numbers that matter for the rework — deal-in rate
while an opponent was in riichi and our own riichi rate. Run a batch of matches with each
model (config → Defense model v2 on/off) and compare.

    python tools/ab_summary.py [path/to/hand_results.csv]
"""
import csv
import os
import sys
from collections import defaultdict


def default_path():
    return os.path.join(os.environ.get("APPDATA", ""), "XIVLauncher", "pluginConfigs", "MahjongHater", "hand_results.csv")


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else default_path()
    groups = defaultdict(list)
    with open(path, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            groups[(r.get("model", "?"), r.get("population", "?"))].append(r)
    if not groups:
        print(f"no rows in {path}")
        return

    header = f"{'model':<8}{'pop':<7}{'hands':>6}{'win':>7}{'dealin':>8}{'tsumo-':>8}{'drawT':>7}{'riichi':>8}{'dealin@R':>10}{'mean d':>9}{'total d':>10}"
    print(header)
    for (model, pop), rows in sorted(groups.items()):
        n = len(rows)
        def rate(pred):
            return sum(1 for r in rows if pred(r)) / n
        wins = rate(lambda r: r["outcome"].startswith("win"))
        dealin = rate(lambda r: r["outcome"] == "dealin")
        tsumo_loss = rate(lambda r: r["outcome"] == "tsumo-loss")
        draw_t = rate(lambda r: r["outcome"] == "draw-tenpai")
        riichi = rate(lambda r: r["ourRiichi"] == "1")
        vs_riichi = [r for r in rows if int(r.get("riichiSeats", "0") or 0) > 0]
        dealin_vs_riichi = (sum(1 for r in vs_riichi if r["outcome"] == "dealin") / len(vs_riichi)) if vs_riichi else float("nan")
        deltas = [int(r["delta"]) for r in rows]
        mean = sum(deltas) / n
        print(f"{model:<8}{pop:<7}{n:>6}{wins:>7.1%}{dealin:>8.1%}{tsumo_loss:>8.1%}{draw_t:>7.1%}{riichi:>8.1%}{dealin_vs_riichi:>10.1%}{mean:>9.0f}{sum(deltas):>10}")
    print("\ndealin@R = deal-in rate over hands in which at least one opponent declared riichi.")


if __name__ == "__main__":
    main()
