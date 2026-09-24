"""The three acceptance bars of docs/research/CANDIDATE_ASSESSMENT.md, as checks.

    python tools/candidate_bars.py bar1 <candidate metrics.json> <baseline metrics.json>
    python tools/candidate_bars.py bar2 <candidate learn-eval.json> <baseline learn-eval.json> [--policy learned-guarded]
    python tools/candidate_bars.py bar3 [hand_results.csv] [--a learned-guarded] [--b V2] [--population human]

bar1: every head of the trainer's held-out test metrics at least as good as the baseline model.
bar2: the replay harness (same test games) - agreement not lower, chosen-tile deal-in not higher,
      call rate within two points of the humans.
bar3: live matches from the plugin's hand_results.csv - arm A (the candidate) not clearly worse
      than arm B on deal-in, deal-in while an opponent is in riichi, win rate and mean delta,
      with the noise those sample sizes imply (two-proportion z / t on the mean).
No dependencies beyond the standard library. Exit code 1 when a bar fails.
"""
import argparse
import csv
import json
import math
import os
import statistics
import sys


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def verdict(ok):
    return "PASS" if ok else "FAIL"


def bar1(args):
    c, b = load(args.candidate)["test"], load(args.baseline)["test"]
    rows = [  # (label, getter, higher is better, tolerance in the metric's unit)
        ("discard/riichi top-1", lambda t: t["policy_top1"], True, 0.),
        ("reaction top-1", lambda t: t["reaction_top1"], True, 0.),
        ("policy NLL", lambda t: t["policy_nll"], False, 0.),
        ("tenpai Brier (calibrated)", lambda t: t["tenpai_calibrated_with_rules"]["brier"], False, 0.),
        ("conditional ron Brier", lambda t: t["conditional_ron_calibrated_with_safety"]["brier"], False, 0.),
        ("placement NLL", lambda t: t.get("placement_nll"), False, 0.),
        ("value MAE (points)", lambda t: t["value_mae_points"], False, 0.),
    ]
    print(f"bar 1 - trainer test metrics: candidate {args.candidate} vs baseline {args.baseline}")
    print(f"{'head':<28}{'candidate':>12}{'baseline':>12}  verdict")
    failed = False
    for label, get, higher, tolerance in rows:
        cv, bv = get(c), get(b)
        if cv is None or bv is None:
            print(f"{label:<28}{'-':>12}{'-':>12}  n/a")
            continue
        ok = cv >= bv - tolerance if higher else cv <= bv + tolerance
        failed |= not ok
        print(f"{label:<28}{cv:>12.4f}{bv:>12.4f}  {verdict(ok)}")
    print(f"placement top-1 by current rank (reference): candidate {c.get('placement_top1_by_current_rank')}, "
          f"candidate head {c.get('placement_top1')}")
    print("parity: run `Precompute learn-check <model> <parity.json>` (the package's check stage) - must print 'Verified'.")
    print("bar 1:", verdict(not failed))
    return not failed


def cell(report, policy, category):
    return report["Agreement"][policy][category]


def bar2(args):
    c, b = load(args.candidate), load(args.baseline)
    p = args.policy
    print(f"bar 2 - replay harness, policy {p}: candidate {args.candidate} vs baseline {args.baseline}")
    if c["Games"] != b["Games"] or c["Corpus"] != b["Corpus"]:
        print(f"  note: different game sets (games {c['Games']} vs {b['Games']}, corpus {c['Corpus'][:12]} vs {b['Corpus'][:12]}); numbers are not strictly paired")
    failed = False

    def line(label, cv, bv, ok, fmt="{:.2%}"):
        nonlocal failed
        failed |= not ok
        print(f"{label:<40}{fmt.format(cv):>10}{fmt.format(bv):>10}  {verdict(ok)}")

    print(f"{'metric':<40}{'candidate':>10}{'baseline':>10}  verdict")
    for category in ("all", "vs-riichi", "vs-open", "tenpai", "all-last"):
        if category in c["Agreement"][p] and category in b["Agreement"][p]:
            cv, bv = cell(c, p, category)["Agreement"], cell(b, p, category)["Agreement"]
            line(f"agreement, {category}", cv, bv, cv >= bv - args.agreement_tolerance)
    for category in ("all", "vs-riichi", "late"):
        if category in c["Agreement"][p] and category in b["Agreement"][p]:
            cv, bv = cell(c, p, category)["PolicyDealInRate"], cell(b, p, category)["PolicyDealInRate"]
            line(f"chosen-tile deal-in, {category}", cv, bv, cv <= bv + args.dealin_tolerance)
    if "reaction" in c["Agreement"][p]:
        rc, rb = cell(c, p, "reaction"), cell(b, p, "reaction") if "reaction" in b["Agreement"][p] else None
        human = rc["HumanSafeChoices"] / rc["Decisions"]
        rate = rc["SafeChoices"] / rc["Decisions"]
        line("reaction agreement", rc["Agreement"], rb["Agreement"] if rb else float("nan"), rb is None or rc["Agreement"] >= rb["Agreement"] - args.agreement_tolerance)
        ok = abs(rate - human) <= args.call_rate_window
        failed |= not ok
        print(f"{'call rate (human ' + f'{human:.1%}' + ')':<40}{rate:>10.2%}{(rb['SafeChoices'] / rb['Decisions']) if rb else float('nan'):>10.2%}  {verdict(ok)}")
    if c.get("Placement"):
        for view, pc in c["Placement"].items():
            n = pc["Count"]
            print(f"{'placement ' + view + ' NLL / rank top-1':<40}{pc['LearnedLogLoss'] / n:>10.4f}{pc['RankTop1'] / n:>10.2%}  info")
    print("bar 2:", verdict(not failed))
    return not failed


def default_hand_results():
    return os.path.join(os.environ.get("APPDATA", ""), "XIVLauncher", "pluginConfigs", "MahjongHater", "hand_results.csv")


def two_proportion(k1, n1, k2, n2):
    """z statistic and two-sided p for p1 - p2 (pooled), plus the difference and its 95 % half-width."""
    if n1 == 0 or n2 == 0:
        return float("nan"), float("nan"), float("nan"), float("nan")
    p1, p2 = k1 / n1, k2 / n2
    pooled = (k1 + k2) / (n1 + n2)
    se = math.sqrt(pooled * (1 - pooled) * (1 / n1 + 1 / n2)) if 0 < pooled < 1 else float("nan")
    z = (p1 - p2) / se if se and se > 0 else 0.
    p = math.erfc(abs(z) / math.sqrt(2)) if not math.isnan(z) else float("nan")
    half = 1.96 * math.sqrt(p1 * (1 - p1) / n1 + p2 * (1 - p2) / n2)
    return z, p, p1 - p2, half


def welch(a, b):
    """Welch t statistic and approximate two-sided p for mean(a) - mean(b)."""
    if len(a) < 2 or len(b) < 2:
        return float("nan"), float("nan")
    va, vb = statistics.variance(a), statistics.variance(b)
    se = math.sqrt(va / len(a) + vb / len(b))
    if se == 0:
        return 0., 1.
    t = (statistics.fmean(a) - statistics.fmean(b)) / se
    return t, math.erfc(abs(t) / math.sqrt(2))   # normal approximation; fine at these sizes


def bar3(args):
    path = args.hand_results or default_hand_results()
    with open(path, newline="", encoding="utf-8") as f:
        rows = [r for r in csv.DictReader(f) if not args.population or r.get("population") == args.population]
    arms = {tag: [r for r in rows if r.get("model") == tag] for tag in (args.a, args.b)}
    if any(r.get("outcome") == "unknown" for arm in arms.values() for r in arm):
        print("bar 3: INCOMPLETE - an arm contains unknown hand outcomes; resolve them before comparing win/deal-in rates.")
        return False
    print(f"bar 3 - live matches from {path}, population {args.population or 'any'}: A = {args.a} ({len(arms[args.a])} hands), B = {args.b} ({len(arms[args.b])} hands)")
    tags = sorted({r.get("model") for r in rows})
    if any(len(v) == 0 for v in arms.values()):
        print(f"  an arm has no hands; tags present: {tags}")
        return False
    failed = False

    def proportion(label, pred, worse_if_higher, subset=None):
        nonlocal failed
        sa = [r for r in arms[args.a] if subset is None or subset(r)]
        sb = [r for r in arms[args.b] if subset is None or subset(r)]
        ka, kb = sum(1 for r in sa if pred(r)), sum(1 for r in sb if pred(r))
        z, p, diff, half = two_proportion(ka, len(sa), kb, len(sb))
        bad = diff > 0 if worse_if_higher else diff < 0
        clearly_worse = bad and not math.isnan(p) and p < 0.05
        failed |= clearly_worse
        print(f"{label:<34} A {ka / len(sa) if sa else float('nan'):>7.1%} (n={len(sa):>4})   B {kb / len(sb) if sb else float('nan'):>7.1%} (n={len(sb):>4})   "
              f"diff {diff:+.1%} +/- {half:.1%}   p={p:.2f}   {'CLEARLY WORSE' if clearly_worse else 'not clearly worse'}")

    proportion("deal-in rate", lambda r: r["outcome"] == "dealin", True)
    proportion("deal-in while an opponent riichi'd", lambda r: r["outcome"] == "dealin", True, subset=lambda r: int(r.get("riichiSeats", "0") or 0) > 0)
    proportion("win rate", lambda r: r["outcome"].startswith("win"), False)
    proportion("tsumo-loss rate", lambda r: r["outcome"] == "tsumo-loss", True)
    proportion("own riichi rate (info)", lambda r: r["ourRiichi"] == "1", False)
    da = [int(r["delta"]) for r in arms[args.a]]
    db = [int(r["delta"]) for r in arms[args.b]]
    t, p = welch(da, db)
    clearly_worse = statistics.fmean(da) < statistics.fmean(db) and not math.isnan(p) and p < 0.05
    failed |= clearly_worse
    print(f"{'mean point delta per hand':<34} A {statistics.fmean(da):>7.0f} (n={len(da):>4})   B {statistics.fmean(db):>7.0f} (n={len(db):>4})   "
          f"diff {statistics.fmean(da) - statistics.fmean(db):+.0f}   p={p:.2f}   {'CLEARLY WORSE' if clearly_worse else 'not clearly worse'}")
    n = min(len(da), len(db))
    print(f"power note: with {n} hands per arm a deal-in difference of about +/-{1.96 * math.sqrt(2 * 0.1 * 0.9 / n):.1%} is the smallest detectable; "
          f"'not clearly worse' at small n is weak evidence - read the overlay's reasons too.")
    print("bar 3:", verdict(not failed))
    return not failed


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="bar", required=True)
    p1 = sub.add_parser("bar1"); p1.add_argument("candidate"); p1.add_argument("baseline")
    p2 = sub.add_parser("bar2"); p2.add_argument("candidate"); p2.add_argument("baseline")
    p2.add_argument("--policy", default="learned-guarded")
    p2.add_argument("--agreement-tolerance", type=float, default=0.001, help="allowed drop in agreement (fraction)")
    p2.add_argument("--dealin-tolerance", type=float, default=0.0005, help="allowed rise in chosen-tile deal-in (fraction)")
    p2.add_argument("--call-rate-window", type=float, default=0.02, help="allowed distance from the human call rate")
    p3 = sub.add_parser("bar3"); p3.add_argument("hand_results", nargs="?")
    p3.add_argument("--a", default="learned-guarded"); p3.add_argument("--b", default="V2"); p3.add_argument("--population", default="human")
    a = parser.parse_args()
    ok = {"bar1": bar1, "bar2": bar2, "bar3": bar3}[a.bar](a)
    sys.exit(0 if ok else 1)
