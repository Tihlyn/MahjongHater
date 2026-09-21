"""Train expert imitation + opponent heads, optionally assisted by offline Q labels.

CPU-only training works on Windows. Inputs are exported by `Precompute learn-data`;
the C# encoder is the sole feature implementation. Hidden hands are targets only.

Memory: nothing is memory-mapped or copied whole. Training reads shuffled chunks of
rows straight from the split file into a bounded shuffle buffer (--buffer-rows), and
validation/test are streamed through the model with metrics accumulated per batch, so a
36 GB dataset trains in about 2 GB of process memory. --memory-log prints the resident
set after every epoch (Windows/Linux, no psutil needed).
"""
import argparse
import ctypes
import hashlib
import json
import math
import os
from pathlib import Path
import random
import sys

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


class Split:
    """Rows of one split, read on demand from the file (never mapped, never copied whole)."""

    def __init__(self, file, rows):
        self.file, self.rows = file, rows

    def __len__(self):
        return self.rows

    def read(self, start, count):
        count = max(0, min(count, self.rows - start))
        with self.file.open("rb") as source:
            data = np.fromfile(source, dtype="<f4", count=count * ROW, offset=start * ROW * 4)
        return data.reshape(count, ROW)

    def batches(self, batch):
        for start in range(0, self.rows, batch):
            yield start, torch.from_numpy(self.read(start, batch))

    def shuffled_batches(self, batch, buffer_rows, rng):
        """Random chunk order, rows shuffled inside a bounded buffer of several chunks."""
        chunk = max(batch, min(buffer_rows // 8, 8192))
        starts = list(range(0, self.rows, chunk))
        rng.shuffle(starts)
        per_buffer = max(1, buffer_rows // chunk)
        for i in range(0, len(starts), per_buffer):
            rows = np.concatenate([self.read(start, chunk) for start in starts[i:i + per_buffer]])
            rng.shuffle(rows)
            for j in range(0, len(rows), batch):
                yield torch.from_numpy(np.ascontiguousarray(rows[j:j + batch]))


def load_dataset(path):
    manifest = json.loads((path / "manifest.json").read_text(encoding="utf-8-sig"))
    if manifest["Schema"] != 1 or manifest["Features"] != "public-tiles-v1" or manifest["RowFloats"] != ROW:
        raise ValueError("Incompatible learning dataset schema")
    splits = {}
    for split in ("train", "validation", "test"):
        file = path / f"{split}.f32"
        with file.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest().upper()
        if digest != manifest["Sha256"][split] or file.stat().st_size != manifest["Rows"][split] * ROW * 4:
            raise ValueError(f"Dataset checksum or length mismatch: {split}")
        if manifest["HumanRows"][split] < 1:
            raise ValueError(f"No human examples in {split}; import more games before training")
        splits[split] = Split(file, manifest["Rows"][split])
    return manifest, splits


def resident_mb():
    """Resident set size in MB without psutil (Windows via psapi, else /proc)."""
    try:
        if sys.platform == "win32":
            class Counters(ctypes.Structure):
                _fields_ = [("cb", ctypes.c_uint32), ("PageFaultCount", ctypes.c_uint32), ("PeakWorkingSetSize", ctypes.c_size_t),
                            ("WorkingSetSize", ctypes.c_size_t), ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t), ("PagefileUsage", ctypes.c_size_t),
                            ("PeakPagefileUsage", ctypes.c_size_t)]
            counters = Counters()
            counters.cb = ctypes.sizeof(Counters)
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.GetCurrentProcess.restype = ctypes.c_void_p
            query = getattr(kernel32, "K32GetProcessMemoryInfo", None) or ctypes.WinDLL("psapi").GetProcessMemoryInfo
            query.argtypes = [ctypes.c_void_p, ctypes.POINTER(Counters), ctypes.c_uint32]
            query.restype = ctypes.c_int
            if not query(kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
                return float("nan"), float("nan")
            return counters.WorkingSetSize / 2 ** 20, counters.PeakWorkingSetSize / 2 ** 20
        with open("/proc/self/status", encoding="utf-8") as status:
            values = {line.split(":")[0]: int(line.split()[1]) for line in status if line.startswith(("VmRSS", "VmHWM"))}
        return values.get("VmRSS", 0) / 1024, values.get("VmHWM", 0) / 1024
    except Exception:  # noqa: BLE001 - diagnostics only
        return float("nan"), float("nan")


@torch.no_grad()
def stream(model, split, batch):
    """Yield (rows, output) per batch with the model in eval mode; nothing is retained."""
    model.eval()
    for _, rows in split.batches(batch):
        yield rows, model(rows[:, :FEATURES])


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


class BinaryMetrics:
    """Streaming Brier / log-loss / ECE (10 bins) over masked binary targets."""

    def __init__(self):
        self.count = self.positives = 0
        self.brier = self.log_loss = 0.
        self.bin_count = [0] * 10
        self.bin_predicted = [0.] * 10
        self.bin_positives = [0] * 10

    def add(self, p, target):
        mask = target >= 0
        p, target = p[mask].double(), target[mask].double()
        if len(target) == 0:
            return
        self.count += len(target)
        self.positives += int(target.sum())
        self.brier += float(((p - target) ** 2).sum())
        self.log_loss += float(F.binary_cross_entropy(p.clamp(1e-9, 1 - 1e-9), target, reduction="sum"))
        bins = (p * 10).long().clamp(0, 9)
        for lo in range(10):
            selected = bins == lo
            n = int(selected.sum())
            if n:
                self.bin_count[lo] += n
                self.bin_predicted[lo] += float(p[selected].sum())
                self.bin_positives[lo] += int(target[selected].sum())

    def report(self):
        if self.count == 0:
            return {"count": 0}
        bins, ece = [], 0.
        for lo in range(10):
            if self.bin_count[lo]:
                confidence = self.bin_predicted[lo] / self.bin_count[lo]
                observed = self.bin_positives[lo] / self.bin_count[lo]
                ece += self.bin_count[lo] / self.count * abs(confidence - observed)
                bins.append({"lower": lo / 10, "count": self.bin_count[lo], "predicted": confidence, "observed": observed})
        return {"count": self.count, "positives": self.positives, "brier": self.brier / self.count,
                "log_loss": self.log_loss / self.count, "ece_10": ece, "bins": bins}


def rule_masks(x):
    """Riichi seats (tenpai = 1 by rule) and known-safe tiles (ron = 0 by rule) per row."""
    features = x.reshape(-1, 64, 34)
    riichi = torch.stack([features[:, 36 + seat, 0] > 0 for seat in range(1, 4)], 1)
    safe = torch.cat([(features[:, 8 + 6 * seat, :] > 0) | (features[:, 8 + 6 * seat + 5, :] > 0) for seat in range(1, 4)], 1)
    return riichi, safe


def evaluate(model, split, batch, tenpai_cal, wait_cal, value_scale=1.):
    """Streaming version of the report: one batch of rows in memory at a time."""
    decisions = top1 = 0
    nll = uniform = 0.
    tenpai_raw, tenpai_rules, ron_raw, ron_safety = BinaryMetrics(), BinaryMetrics(), BinaryMetrics(), BinaryMetrics()
    value_count = q_count = 0
    value_abs = q_square = 0.
    for rows, output in stream(model, split, batch):
        x, legal, human, targets, q = unpack(rows)
        labeled = human >= 0
        if labeled.any():
            logits = output[labeled, :74].masked_fill(~legal[labeled], -1e9)
            decisions += int(labeled.sum())
            top1 += int((logits.argmax(1) == human[labeled]).sum())
            nll += float(F.cross_entropy(logits, human[labeled], reduction="sum"))
            uniform += float(legal[labeled].sum(1).float().log().sum())
        riichi, safe = rule_masks(x)
        tenpai = probability(output[:, 74:77], tenpai_cal).masked_fill(riichi, 1.)
        wait = probability(output[:, 77:179], wait_cal).masked_fill(safe, 0.)
        tenpai_raw.add(torch.sigmoid(output[:, 74:77]), targets[:, :3])
        tenpai_rules.add(tenpai, targets[:, :3])
        ron_raw.add(torch.sigmoid(output[:, 77:179]), targets[:, 3:105])
        ron_safety.add(wait, targets[:, 3:105])
        points = (F.softplus(output[:, 179:281]) * value_scale).clamp(0, 4)
        value_mask = targets[:, 105:] >= 0
        value_count += int(value_mask.sum())
        value_abs += float((points[value_mask] - targets[:, 105:][value_mask]).abs().sum())
        valid_q = torch.isfinite(q)
        q_count += int(valid_q.sum())
        q_square += float(((output[:, 281:][valid_q] - q[valid_q]) ** 2).sum())
    return {"human_decisions": decisions, "policy_top1": top1 / decisions if decisions else None,
            "policy_nll": nll / decisions if decisions else None,
            "uniform_legal_nll": uniform / decisions if decisions else None,
            "tenpai_raw": tenpai_raw.report(), "tenpai_calibrated_with_rules": tenpai_rules.report(),
            "conditional_ron_raw": ron_raw.report(), "conditional_ron_calibrated_with_safety": ron_safety.report(),
            "value_positive_count": value_count,
            "value_mae_points": value_abs / value_count * 32000 if value_count else None,
            "search_q_rmse_points": math.sqrt(q_square / q_count) * 32000 if q_count else None}


def validation_pass(model, split, batch):
    """Validation loss plus the small per-row pieces calibration needs (logits and masked
    targets of the tenpai/ron heads, value ratio terms); never the feature planes."""
    total = 0.
    tenpai_logits, tenpai_targets, wait_logits, wait_targets = [], [], [], []
    value_target_sum = value_pred_sum = 0.
    value_count = 0
    for rows, output in stream(model, split, batch):
        x, legal, human, targets, q = unpack(rows)
        total += float(objective(output, legal, human, targets, q)) * len(rows)
        riichi, safe = rule_masks(x)
        tenpai_logits.append(output[:, 74:77].clone())
        tenpai_targets.append(targets[:, :3].masked_fill(riichi, -1))
        wait_logits.append(output[:, 77:179].clone())
        wait_targets.append(targets[:, 3:105].masked_fill(safe, -1))
        value_mask = targets[:, 105:] >= 0
        value_count += int(value_mask.sum())
        value_target_sum += float(targets[:, 105:][value_mask].sum())
        value_pred_sum += float(F.softplus(output[:, 179:281])[value_mask].sum())
    return (total / len(split), torch.cat(tenpai_logits), torch.cat(tenpai_targets), torch.cat(wait_logits), torch.cat(wait_targets),
            value_count, value_target_sum, value_pred_sum)


def atomic_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, allow_nan=False, separators=(",", ":")), encoding="utf-8")
    os.replace(temporary, path)


def layer(module):
    weight = module.weight.detach().cpu()
    return {"Inputs": weight.shape[1], "Outputs": weight.shape[0], "Kernel": weight.shape[2] if weight.ndim == 3 else 1,
            "Weight": weight.flatten().tolist(), "Bias": module.bias.detach().cpu().tolist()}


def train(args):
    if args.epochs < 1 or args.batch < 1 or args.threads < 1 or not math.isfinite(args.lr) or args.lr <= 0 or args.buffer_rows < args.batch:
        raise ValueError("Epochs, batch, threads and learning rate must be positive; buffer-rows must cover a batch")
    torch.set_num_threads(args.threads)
    torch.manual_seed(args.seed)
    np.random.seed(args.seed)
    random.seed(args.seed)
    manifest, splits = load_dataset(args.dataset)
    if args.channels < 1 or args.channels > 256 or args.hidden < 1 or args.hidden > 512:
        raise ValueError("channels must be 1-256 and hidden 1-512 (LearnedModel limits)")
    identity = hashlib.sha256(json.dumps({"dataset": manifest, "seed": args.seed, "batch": args.batch, "lr": args.lr,
                                         "architecture": f"cnn{args.channels}-dense{args.hidden}-v1"}, sort_keys=True).encode()).hexdigest()
    if args.output.exists() and not args.resume:
        raise ValueError("Output exists; use a new directory or --resume")
    args.output.mkdir(parents=True, exist_ok=True)
    model = Network(args.channels, args.hidden)
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
    for epoch in range(start, args.epochs):
        model.train()
        rng = np.random.default_rng(args.seed + epoch)
        total_loss, seen = 0., 0
        for rows in splits["train"].shuffled_batches(args.batch, args.buffer_rows, rng):
            x, legal, human, targets, q = unpack(rows)
            optimizer.zero_grad(set_to_none=True)
            loss = objective(model(x), legal, human, targets, q)
            if not torch.isfinite(loss):
                raise FloatingPointError("Nonfinite training loss")
            loss.backward()
            nn.utils.clip_grad_norm_(model.parameters(), 5.)
            optimizer.step()
            total_loss += float(loss.detach()) * len(rows)
            seen += len(rows)
        validation_loss = validation_pass(model, splits["validation"], args.batch)[0]
        entry = {"epoch": epoch + 1, "train_loss": total_loss / max(1, seen), "validation_loss": validation_loss}
        if args.memory_log:
            current, peak = resident_mb()
            entry["rss_mb"], entry["peak_rss_mb"] = round(current), round(peak)
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
    # Riichi is a deterministic tenpai fact, and known-safe tiles are hard rules.
    # Fit probabilistic calibration only where those rules do not decide the result.
    _, tenpai_logits, tenpai_targets, wait_logits, wait_targets, value_count, value_target_sum, value_pred_sum = \
        validation_pass(model, splits["validation"], args.batch)
    tenpai_cal, tenpai_note = fit_calibration(tenpai_logits, tenpai_targets)
    wait_cal, wait_note = fit_calibration(wait_logits, wait_targets)
    del tenpai_logits, tenpai_targets, wait_logits, wait_targets
    value_scale = float(min(10., max(.1, value_target_sum / value_pred_sum))) if value_count >= 30 and value_pred_sum > 0 else 1.
    artifact = {"Schema": 1, "Features": manifest["Features"], "Corpus": manifest["Corpus"], "Rules": manifest["Rules"],
                "Conv1": layer(model.conv1), "Conv2": layer(model.conv2), "Dense": layer(model.dense), "Output": layer(model.output),
                "TenpaiCalibration": tenpai_cal, "WaitCalibration": wait_cal, "ValueScale": value_scale,
                "HasSearchLabels": manifest["SearchRows"] > 0, "Status": "experimental-unvalidated"}
    atomic_json(args.output / "learned_policy.json", artifact)
    report = {"dataset": manifest, "training_identity": identity, "history": history,
              "calibration": {"tenpai": tenpai_note, "conditional_ron": wait_note, "value_scale": value_scale},
              "validation": evaluate(model, splits["validation"], args.batch, tenpai_cal, wait_cal, value_scale),
              "test": evaluate(model, splits["test"], args.batch, tenpai_cal, wait_cal, value_scale),
              "limitations": ["Whole-game split; players and time periods can overlap.",
                              "Imitation accuracy is not playing strength; evaluate complete games before promotion.",
                              "Discard/riichi only; call and kan decisions retain the existing policy.",
                              "Opponent values exclude ura, honba and red winning-tile bonuses."]}
    atomic_json(args.output / "metrics.json", report)
    head = torch.from_numpy(splits["test"].read(0, 8))
    with torch.no_grad():
        model.eval()
        head_output = model(head[:, :FEATURES])
    vectors = [{"Input": head[i, :FEATURES].tolist(), "Output": head_output[i].tolist()} for i in range(len(head))]
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
    parser.add_argument("--buffer-rows", type=int, default=32768, help="shuffle buffer in rows (~10 KB each); bounds training memory")
    parser.add_argument("--memory-log", action="store_true", help="print resident set size after every epoch")
    parser.add_argument("--channels", type=int, default=24, help="conv channels (C# LearnedModel allows up to 256)")
    parser.add_argument("--hidden", type=int, default=64, help="dense width (up to 512)")
    train(parser.parse_args())
