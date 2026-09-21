"""Train expert imitation + opponent heads, optionally assisted by offline Q labels.

CPU-only training works on Windows. Inputs are exported by `Precompute learn-data`;
the C# encoder is the sole feature implementation. Hidden hands are targets only.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import random

import numpy as np
import torch
from torch import nn
from torch.nn import functional as F

FEATURES, ACTIONS, OPPONENTS, OUTPUTS = 64 * 34, 74, 207, 355
ROW = FEATURES + ACTIONS + 1 + OPPONENTS + ACTIONS


class Network(nn.Module):
    def __init__(self, channels=24, hidden=64):
        super().__init__()
        self.conv1 = nn.Conv1d(64, channels, 3, padding=1)
        self.conv2 = nn.Conv1d(channels, channels, 3, padding=1)
        self.dense = nn.Linear(channels * 34, hidden)
        self.output = nn.Linear(hidden, OUTPUTS)
        with torch.no_grad():
            self.output.bias[74:77] = -1.5
            self.output.bias[77:179] = -3
            self.output.bias[179:281] = -2.5

    def forward(self, x):
        x = F.relu(self.conv1(x.reshape(-1, 64, 34)))
        x = F.relu(self.conv2(x)).flatten(1)
        return self.output(F.relu(self.dense(x)))


def unpack(rows):
    x = rows[:, :FEATURES]
    legal = rows[:, FEATURES:FEATURES + ACTIONS] > 0
    human = rows[:, FEATURES + ACTIONS].long()
    targets = rows[:, FEATURES + ACTIONS + 1:FEATURES + ACTIONS + 1 + OPPONENTS]
    return x, legal, human, targets, rows[:, -ACTIONS:]


def masked_loss(prediction, target, kind):
    mask = torch.isfinite(target) if kind == "q" else target >= 0
    if not mask.any():
        return prediction.sum() * 0
    if kind == "binary":
        return F.binary_cross_entropy_with_logits(prediction[mask], target[mask])
    if kind == "value":
        prediction = F.softplus(prediction)
    return F.mse_loss(prediction[mask], target[mask])


def objective(output, legal, human, targets, q):
    labeled = human >= 0
    policy = F.cross_entropy(output[labeled, :74].masked_fill(~legal[labeled], -1e9), human[labeled]) if labeled.any() else output.sum() * 0
    return (policy + .5 * masked_loss(output[:, 74:77], targets[:, :3], "binary")
            + .5 * masked_loss(output[:, 77:179], targets[:, 3:105], "binary")
            + masked_loss(output[:, 179:281], targets[:, 105:], "value")
            + .2 * masked_loss(output[:, 281:], q, "q"))


def load_dataset(path):
    manifest = json.loads((path / "manifest.json").read_text(encoding="utf-8-sig"))
    if manifest["Schema"] != 1 or manifest["Features"] != "public-tiles-v1" or manifest["RowFloats"] != ROW:
        raise ValueError("Incompatible learning dataset schema")
    arrays = {}
    for split in ("train", "validation", "test"):
        file = path / f"{split}.f32"
        with file.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest().upper()
        if digest != manifest["Sha256"][split] or file.stat().st_size != manifest["Rows"][split] * ROW * 4:
            raise ValueError(f"Dataset checksum or length mismatch: {split}")
        if manifest["HumanRows"][split] < 1:
            raise ValueError(f"No human examples in {split}; import more games before training")
        arrays[split] = np.memmap(file, mode="r", dtype="<f4", shape=(manifest["Rows"][split], ROW))
    return manifest, arrays


@torch.no_grad()
def predict(model, data, batch):
    model.eval()
    return torch.cat([model(torch.from_numpy(np.array(data[i:i + batch, :FEATURES]))) for i in range(0, len(data), batch)])


def fit_calibration(logits, targets):
    mask = targets >= 0
    x, y = logits[mask].detach(), targets[mask].detach()
    if len(y) < 30 or y.sum() < 5 or (1 - y).sum() < 5:
        return {"Slope": 1., "Bias": 0.}, "insufficient validation positives/negatives; identity calibration"
    raw_slope = nn.Parameter(torch.tensor(math.log(math.expm1(1.))))
    bias = nn.Parameter(torch.tensor(0.))
    optimizer = torch.optim.LBFGS([raw_slope, bias], max_iter=60, line_search_fn="strong_wolfe")

    def closure():
        optimizer.zero_grad()
        slope = F.softplus(raw_slope).clamp(.01, 100)
        loss = F.binary_cross_entropy_with_logits(x * slope + bias.clamp(-100, 100), y)
        loss.backward()
        return loss

    optimizer.step(closure)
    return {"Slope": float(F.softplus(raw_slope).detach().clamp(.01, 100)), "Bias": float(bias.detach().clamp(-100, 100))}, "fitted on validation only"


def probability(logits, calibration):
    return torch.sigmoid(logits * calibration["Slope"] + calibration["Bias"])


def binary_metrics(p, target):
    mask = target >= 0
    p, target = p[mask].double(), target[mask].double()
    if len(target) == 0:
        return {"count": 0}
    bins = []
    ece = 0.
    for lo in range(10):
        selected = ((p >= lo / 10) & (p < (lo + 1) / 10)) if lo < 9 else p >= .9
        if selected.any():
            confidence, observed = float(p[selected].mean()), float(target[selected].mean())
            ece += int(selected.sum()) / len(p) * abs(confidence - observed)
            bins.append({"lower": lo / 10, "count": int(selected.sum()), "predicted": confidence, "observed": observed})
    return {"count": len(target), "positives": int(target.sum()), "brier": float(((p - target) ** 2).mean()),
            "log_loss": float(F.binary_cross_entropy(p.clamp(1e-9, 1 - 1e-9), target)), "ece_10": ece, "bins": bins}


def evaluate(output, data, tenpai_cal, wait_cal, value_scale=1.):
    rows = torch.from_numpy(np.array(data))
    x, legal, human, targets, q = unpack(rows)
    labeled = human >= 0
    logits = output[labeled, :74].masked_fill(~legal[labeled], -1e9)
    raw_tenpai = torch.sigmoid(output[:, 74:77])
    tenpai = probability(output[:, 74:77], tenpai_cal)
    wait = probability(output[:, 77:179], wait_cal).reshape(-1, 3, 34)
    features = x.reshape(-1, 64, 34)
    for seat in range(1, 4):
        tenpai[:, seat - 1] = torch.where(features[:, 36 + seat, 0] > 0, 1., tenpai[:, seat - 1])
        channel = 8 + 6 * seat
        safe = (features[:, channel, :] > 0) | (features[:, channel + 5, :] > 0)
        wait[:, seat - 1, :].masked_fill_(safe, 0.)
    wait = wait.flatten(1)
    points = (F.softplus(output[:, 179:281]) * value_scale).clamp(0, 4)
    value_mask = targets[:, 105:] >= 0
    valid_q = torch.isfinite(q)
    return {"human_decisions": int(labeled.sum()), "policy_top1": float((logits.argmax(1) == human[labeled]).float().mean()),
            "policy_nll": float(F.cross_entropy(logits, human[labeled])),
            "uniform_legal_nll": float(legal[labeled].sum(1).float().log().mean()),
            "tenpai_raw": binary_metrics(raw_tenpai, targets[:, :3]),
            "tenpai_calibrated_with_rules": binary_metrics(tenpai, targets[:, :3]),
            "conditional_ron_raw": binary_metrics(torch.sigmoid(output[:, 77:179]), targets[:, 3:105]),
            "conditional_ron_calibrated_with_safety": binary_metrics(wait, targets[:, 3:105]),
            "value_positive_count": int(value_mask.sum()),
            "value_mae_points": float((points[value_mask] - targets[:, 105:][value_mask]).abs().mean() * 32000) if value_mask.any() else None,
            "search_q_rmse_points": float(((output[:, 281:][valid_q] - q[valid_q]) ** 2).mean().sqrt() * 32000) if valid_q.any() else None}


def atomic_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, allow_nan=False, separators=(",", ":")), encoding="utf-8")
    os.replace(temporary, path)


def layer(module):
    weight = module.weight.detach().cpu()
    return {"Inputs": weight.shape[1], "Outputs": weight.shape[0], "Kernel": weight.shape[2] if weight.ndim == 3 else 1,
            "Weight": weight.flatten().tolist(), "Bias": module.bias.detach().cpu().tolist()}


def train(args):
    if args.epochs < 1 or args.batch < 1 or args.threads < 1 or not math.isfinite(args.lr) or args.lr <= 0:
        raise ValueError("Epochs, batch, threads and learning rate must be positive")
    torch.set_num_threads(args.threads)
    torch.manual_seed(args.seed)
    np.random.seed(args.seed)
    random.seed(args.seed)
    manifest, arrays = load_dataset(args.dataset)
    identity = hashlib.sha256(json.dumps({"dataset": manifest, "seed": args.seed, "batch": args.batch, "lr": args.lr,
                                         "architecture": "cnn24-dense64-v1"}, sort_keys=True).encode()).hexdigest()
    if args.output.exists() and not args.resume:
        raise ValueError("Output exists; use a new directory or --resume")
    args.output.mkdir(parents=True, exist_ok=True)
    model = Network()
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    start, best_loss, history = 0, math.inf, []
    checkpoint = args.output / "checkpoint.pt"
    if args.resume:
        # Only a checkpoint written by this trainer, never a third-party pickle.
        saved = torch.load(checkpoint, map_location="cpu", weights_only=True)
        if saved["identity"] != identity:
            raise ValueError("Resume configuration or dataset identity differs")
        model.load_state_dict(saved["model"])
        optimizer.load_state_dict(saved["optimizer"])
        torch.set_rng_state(saved["rng"])
        start, best_loss, history = saved["epoch"], saved["best_loss"], saved["history"]
    # Test data is not evaluated until model selection and calibration are frozen.
    validation_rows = torch.from_numpy(np.array(arrays["validation"]))
    _, validation_legal, validation_human, validation_targets, validation_q = unpack(validation_rows)
    for epoch in range(start, args.epochs):
        model.train()
        order = np.random.default_rng(args.seed + epoch).permutation(len(arrays["train"]))
        total_loss = 0.
        for i in range(0, len(order), args.batch):
            rows = torch.from_numpy(np.array(arrays["train"][order[i:i + args.batch]]))
            x, legal, human, targets, q = unpack(rows)
            optimizer.zero_grad(set_to_none=True)
            loss = objective(model(x), legal, human, targets, q)
            if not torch.isfinite(loss):
                raise FloatingPointError("Nonfinite training loss")
            loss.backward()
            nn.utils.clip_grad_norm_(model.parameters(), 5.)
            optimizer.step()
            total_loss += float(loss.detach()) * len(rows)
        output = predict(model, arrays["validation"], args.batch)
        validation_loss = float(objective(output, validation_legal, validation_human, validation_targets, validation_q))
        entry = {"epoch": epoch + 1, "train_loss": total_loss / len(order), "validation_loss": validation_loss}
        history.append(entry)
        print(json.dumps(entry), flush=True)
        if validation_loss < best_loss:
            best_loss = validation_loss
            torch.save(model.state_dict(), args.output / "best.pt.tmp")
            os.replace(args.output / "best.pt.tmp", args.output / "best.pt")
        torch.save({"identity": identity, "model": model.state_dict(), "optimizer": optimizer.state_dict(),
                    "epoch": epoch + 1, "best_loss": best_loss, "history": history, "rng": torch.get_rng_state()}, args.output / "checkpoint.pt.tmp")
        os.replace(args.output / "checkpoint.pt.tmp", checkpoint)
    model.load_state_dict(torch.load(args.output / "best.pt", map_location="cpu", weights_only=True))
    validation = predict(model, arrays["validation"], args.batch)
    # Riichi is a deterministic tenpai fact, and known-safe tiles are hard rules.
    # Fit probabilistic calibration only where those rules do not decide the result.
    calibration_targets = validation_targets.clone()
    features = validation_rows[:, :FEATURES].reshape(-1, 64, 34)
    for seat in range(1, 4):
        calibration_targets[features[:, 36 + seat, 0] > 0, seat - 1] = -1
        channel = 8 + 6 * seat
        safe = (features[:, channel, :] > 0) | (features[:, channel + 5, :] > 0)
        calibration_targets[:, 3 + (seat - 1) * 34:3 + seat * 34].masked_fill_(safe, -1)
    tenpai_cal, tenpai_note = fit_calibration(validation[:, 74:77], calibration_targets[:, :3])
    wait_cal, wait_note = fit_calibration(validation[:, 77:179], calibration_targets[:, 3:105])
    value_mask = validation_targets[:, 105:] >= 0
    value_scale = float((validation_targets[:, 105:][value_mask].mean() / F.softplus(validation[:, 179:281])[value_mask].mean()).clamp(.1, 10)) if value_mask.sum() >= 30 else 1.
    artifact = {"Schema": 1, "Features": manifest["Features"], "Corpus": manifest["Corpus"], "Rules": manifest["Rules"],
                "Conv1": layer(model.conv1), "Conv2": layer(model.conv2), "Dense": layer(model.dense), "Output": layer(model.output),
                "TenpaiCalibration": tenpai_cal, "WaitCalibration": wait_cal, "ValueScale": value_scale,
                "HasSearchLabels": manifest["SearchRows"] > 0, "Status": "experimental-unvalidated"}
    atomic_json(args.output / "learned_policy.json", artifact)
    test = predict(model, arrays["test"], args.batch)
    identity_cal = {"Slope": 1., "Bias": 0.}
    report = {"dataset": manifest, "training_identity": identity, "history": history,
              "calibration": {"tenpai": tenpai_note, "conditional_ron": wait_note, "value_scale": value_scale},
              "validation": evaluate(validation, arrays["validation"], tenpai_cal, wait_cal, value_scale),
              "test": evaluate(test, arrays["test"], tenpai_cal, wait_cal, value_scale),
              "limitations": ["Whole-game split; players and time periods can overlap.",
                              "Imitation accuracy is not playing strength; evaluate complete games before promotion.",
                              "Discard/riichi only; call and kan decisions retain the existing policy.",
                              "Opponent values exclude ura, honba and red winning-tile bonuses."]}
    atomic_json(args.output / "metrics.json", report)
    vectors = [{"Input": np.array(arrays["test"][i, :FEATURES]).tolist(), "Output": test[i].tolist()} for i in range(min(8, len(test)))]
    atomic_json(args.output / "parity.json", vectors)
    print(json.dumps({"model": str(args.output / "learned_policy.json"), "test_policy_top1": report["test"]["policy_top1"],
                      "test_tenpai_brier": report["test"]["tenpai_calibrated_with_rules"]["brier"]}), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dataset", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--epochs", type=int, default=20)
    parser.add_argument("--batch", type=int, default=128)
    parser.add_argument("--threads", type=int, default=8)
    parser.add_argument("--lr", type=float, default=.001)
    parser.add_argument("--seed", type=int, default=20260921)
    parser.add_argument("--resume", action="store_true")
    train(parser.parse_args())
