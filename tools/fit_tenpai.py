"""Fit the opponent tenpai estimate to recorded ground truth.

Input: tenpai_calibration.csv written by the plugin at every hand end
(%AppData%\\XIVLauncher\\pluginConfigs\\MahjongHater\\tenpai_calibration.csv).
One row per opponent: features as they stood when the
hand ended and whether that opponent was tenpai (draw screens label every seat; a win
proves the winner only).

Fits the same logistic form the plugin uses (Core/Policy/TenpaiEstimator.cs):

    logit p = intercept + perDiscard*discards + perMeld*openMelds
              + earlyOutside*early + lateMiddle*late

with plain gradient descent (stdlib only), reports log-loss / Brier of the shipped
weights vs the fit vs a constant baseline, prints a reliability table by discard bucket,
and emits the C# initializers to paste into PolicyWeights. Riichi rows are excluded
(the estimate is 1 by rule).

    python tools/fit_tenpai.py [path/to/tenpai_calibration.csv] [--l2 0.01] [--iters 5000]
"""
import csv
import math
import os
import sys

SHIPPED = {"intercept": -4.1, "perDiscard": 0.25, "perMeld": 0.95, "earlyOutside": 0.3, "lateMiddle": 0.5}
CAP = 0.9
FEATURES = ["perDiscard", "perMeld", "earlyOutside", "lateMiddle"]


def default_path():
    return os.path.join(os.environ.get("APPDATA", ""), "XIVLauncher", "pluginConfigs", "MahjongHater", "tenpai_calibration.csv")


def load(path):
    rows = []
    with open(path, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            if r["riichi"] == "1":
                continue
            rows.append({
                "x": [int(r["discards"]), int(r["openMelds"]), float(r["earlyOutside"]), float(r["lateMiddle"])],
                "y": 1 if r["tenpai"] == "1" else 0,
                "shipped": float(r["predicted"]),
                "source": r["source"],
            })
    return rows


def sigmoid(z):
    return 1 / (1 + math.exp(-z)) if z > -700 else 0.0


def predict(w, x, cap=CAP):
    z = w["intercept"] + sum(w[k] * v for k, v in zip(FEATURES, x))
    return min(cap, sigmoid(z))


def fit(rows, l2=0.01, iters=5000, lr=0.02):
    w = dict(SHIPPED)
    n = len(rows)
    # Scale discards so one step moves every coefficient on a comparable scale.
    scale = [18.0, 4.0, 1.0, 1.0]
    for _ in range(iters):
        grad = {"intercept": 0.0, **{k: 0.0 for k in FEATURES}}
        for r in rows:
            p = sigmoid(w["intercept"] + sum(w[k] * v for k, v in zip(FEATURES, r["x"])))
            err = p - r["y"]
            grad["intercept"] += err
            for k, v, s in zip(FEATURES, r["x"], scale):
                grad[k] += err * v / s
        w["intercept"] -= lr * grad["intercept"] / n
        for k, s in zip(FEATURES, scale):
            w[k] -= lr * (grad[k] / n + l2 * w[k]) / s
    return w


def score(rows, prob):
    ll = brier = 0.0
    for r in rows:
        p = min(max(prob(r), 1e-6), 1 - 1e-6)
        ll -= r["y"] * math.log(p) + (1 - r["y"]) * math.log(1 - p)
        brier += (p - r["y"]) ** 2
    return ll / len(rows), brier / len(rows)


def reliability(rows, prob, buckets=((0, 6), (7, 10), (11, 14), (15, 18), (19, 30))):
    print("\n discards   n   melds>0   tenpai%   predicted%")
    for lo, hi in buckets:
        grp = [r for r in rows if lo <= r["x"][0] <= hi]
        if not grp:
            continue
        rate = sum(r["y"] for r in grp) / len(grp)
        pred = sum(prob(r) for r in grp) / len(grp)
        melds = sum(1 for r in grp if r["x"][1] > 0)
        print(f" {lo:2d}-{hi:<2d}   {len(grp):4d}   {melds:6d}   {100 * rate:6.1f}    {100 * pred:6.1f}")


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    opts = dict(zip(sys.argv[1::1], sys.argv[2::1]))
    path = args[0] if args else default_path()
    l2 = float(opts.get("--l2", 0.01))
    iters = int(opts.get("--iters", 5000))
    rows = load(path)
    if len(rows) < 30:
        print(f"{len(rows)} usable rows in {path} — need a few dozen hands before a fit means anything.")
        if rows:
            reliability(rows, lambda r: r["shipped"])
        return
    draws = sum(1 for r in rows if r["source"] == "draw")
    print(f"{len(rows)} rows ({draws} from draws, {len(rows) - draws} winners), tenpai rate {sum(r['y'] for r in rows) / len(rows):.2%}")

    base = sum(r["y"] for r in rows) / len(rows)
    fitted = fit(rows, l2=l2, iters=iters)
    for name, prob in (("constant", lambda r: base), ("shipped", lambda r: r["shipped"]), ("fitted", lambda r: predict(fitted, r["x"]))):
        ll, br = score(rows, prob)
        print(f"{name:9s} log-loss {ll:.3f}  brier {br:.3f}")

    reliability(rows, lambda r: predict(fitted, r["x"]))
    print("\nPolicyWeights initializers (fitted):")
    print(f"    TenpaiLogitIntercept = {fitted['intercept']:.2f};")
    print(f"    TenpaiLogitPerDiscard = {fitted['perDiscard']:.3f};")
    print(f"    TenpaiLogitPerMeld = {fitted['perMeld']:.2f};")
    print(f"    TenpaiLogitEarlyOutside = {fitted['earlyOutside']:.2f};")
    print(f"    TenpaiLogitLateMiddle = {fitted['lateMiddle']:.2f};")


if __name__ == "__main__":
    main()
