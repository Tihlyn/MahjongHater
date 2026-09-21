"""Train expert imitation (discard / riichi / reaction) + opponent heads, optionally
assisted by offline Q labels.

Inputs are exported by `Precompute learn-data`; the C# encoder is the sole feature
implementation and the row layout comes from the dataset manifest (schema 1 = public-tiles-v1,
74 actions; schema 2 = public-tiles-v2 with look-ahead planes and 82 actions including
pass / pon / open kan / chi shapes / own-turn kans). Hidden hands are targets only.

Network: 3×1 conv stem → N residual blocks (conv-relu-conv + skip) → dense → heads, the
Suphx/NAGA shape at a size this project can train. --blocks 0 reproduces the earlier
two-layer net (exported as schema 1 when the dataset is schema 1).

Memory: nothing is memory-mapped or copied whole. Training reads shuffled chunks of rows
into a bounded shuffle buffer (--buffer-rows); validation/test are streamed with metrics
accumulated per batch. --memory-log prints the resident set after every epoch.

GPU: --device cuda (or auto) with automatic mixed precision; an 8 GB card takes
--blocks 6 --channels 128 --batch 1024 comfortably.
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

OPPONENTS = 207
WIDTH = 34


class Layout:
    """Row/output layout for one feature version (from the dataset manifest)."""

    def __init__(self, manifest):
        if manifest["Schema"] not in (1, 2) or manifest["Features"] not in ("public-tiles-v1", "public-tiles-v2"):
            raise ValueError("Incompatible learning dataset schema")
        self.schema = manifest["Schema"]
        self.features_version = manifest["Features"]
        self.channels = manifest["Channels"]
        self.actions = manifest["Actions"]
        self.features = self.channels * WIDTH
        self.row = self.features + self.actions + 1 + OPPONENTS + self.actions
        if manifest["RowFloats"] != self.row:
            raise ValueError("Dataset row size does not match its declared layout")
        self.dtype = "<f2" if manifest.get("Dtype", "float32") == "float16" else "<f4"
        self.item = 2 if self.dtype == "<f2" else 4
        self.extension = manifest.get("Extension", ".f32")
        self.outputs = 2 * self.actions + OPPONENTS
        self.tenpai = self.actions
        self.ron = self.actions + 3
        self.points = self.actions + 105
        self.q = self.actions + 207


class Network(nn.Module):
    def __init__(self, layout, channels=24, hidden=64, blocks=0):
        super().__init__()
        self.layout = layout
        self.stem = nn.Conv1d(layout.channels, channels, 3, padding=1)
        self.blocks = nn.ModuleList(nn.ModuleList([nn.Conv1d(channels, channels, 3, padding=1), nn.Conv1d(channels, channels, 3, padding=1)])
                                    for _ in range(blocks))
        self.dense = nn.Linear(channels * WIDTH, hidden)
        self.output = nn.Linear(hidden, layout.outputs)
        with torch.no_grad():
            self.output.bias[layout.tenpai:layout.tenpai + 3] = -1.5
            self.output.bias[layout.ron:layout.ron + 102] = -3
            self.output.bias[layout.points:layout.points + 102] = -2.5

    def forward(self, x):
        h = F.relu(self.stem(x.reshape(-1, self.layout.channels, WIDTH)))
        for conv1, conv2 in self.blocks:
            h = F.relu(conv2(F.relu(conv1(h))) + h)
        return self.output(F.relu(self.dense(h.flatten(1))))


def unpack(rows, layout):
    x = rows[:, :layout.features]
    legal = rows[:, layout.features:layout.features + layout.actions] > 0
    human = rows[:, layout.features + layout.actions].long()
    targets = rows[:, layout.features + layout.actions + 1:layout.features + layout.actions + 1 + OPPONENTS]
    return x, legal, human, targets, rows[:, -layout.actions:]


def masked_loss(prediction, target, kind):
    mask = torch.isfinite(target) if kind == "q" else target >= 0
    if not mask.any():
        return prediction.sum() * 0
    if kind == "binary":
        return F.binary_cross_entropy_with_logits(prediction[mask].float(), target[mask].float())
    if kind == "value":
        prediction = F.softplus(prediction.float())
    return F.mse_loss(prediction[mask].float(), target[mask].float())


def objective(output, legal, human, targets, q, layout):
    output = output.float()
    labeled = human >= 0
    a = layout.actions
    policy = (F.cross_entropy(output[labeled, :a].masked_fill(~legal[labeled], -1e9), human[labeled])
              if labeled.any() else output.sum() * 0)
    return (policy + .5 * masked_loss(output[:, layout.tenpai:layout.tenpai + 3], targets[:, :3], "binary")
            + .5 * masked_loss(output[:, layout.ron:layout.ron + 102], targets[:, 3:105], "binary")
            + masked_loss(output[:, layout.points:layout.points + 102], targets[:, 105:], "value")
            + .2 * masked_loss(output[:, layout.q:], q, "q"))


class Split:
    """Rows of one split, read on demand from the file (never mapped, never copied whole)."""

    def __init__(self, file, rows, layout):
        self.file, self.rows, self.layout = file, rows, layout

    def __len__(self):
        return self.rows

    def read(self, start, count):
        count = max(0, min(count, self.rows - start))
        with self.file.open("rb") as source:
            data = np.fromfile(source, dtype=self.layout.dtype, count=count * self.layout.row, offset=start * self.layout.row * self.layout.item)
        return data.reshape(count, self.layout.row)

    def batches(self, batch):
        for start in range(0, self.rows, batch):
            yield start, torch.from_numpy(self.read(start, batch).astype(np.float32, copy=False))

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
                yield torch.from_numpy(np.ascontiguousarray(rows[j:j + batch], dtype=np.float32))


def load_dataset(path):
    manifest = json.loads((path / "manifest.json").read_text(encoding="utf-8-sig"))
    layout = Layout(manifest)
    splits = {}
    for split in ("train", "validation", "test"):
        file = path / f"{split}{layout.extension}"
        with file.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest().upper()
        if digest != manifest["Sha256"][split] or file.stat().st_size != manifest["Rows"][split] * layout.row * layout.item:
            raise ValueError(f"Dataset checksum or length mismatch: {split}")
        if manifest["HumanRows"][split] < 1:
            raise ValueError(f"No human examples in {split}; import more games before training")
        splits[split] = Split(file, manifest["Rows"][split], layout)
    return manifest, layout, splits


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
def stream(model, split, batch, device):
    """Yield (rows on CPU, output on CPU) per batch with the model in eval mode."""
    model.eval()
    for _, rows in split.batches(batch):
        x = rows[:, :split.layout.features].to(device, non_blocking=True)
        with torch.autocast(device_type=device.type, enabled=device.type == "cuda"):
            output = model(x)
        yield rows, output.float().cpu()


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


def rule_masks(x, layout):
    """Riichi seats (tenpai = 1 by rule) and known-safe tiles (ron = 0 by rule) per row.
    Planes 0-63 are identical in both feature versions."""
    features = x.reshape(-1, layout.channels, WIDTH)
    riichi = torch.stack([features[:, 36 + seat, 0] > 0 for seat in range(1, 4)], 1)
    safe = torch.cat([(features[:, 8 + 6 * seat, :] > 0) | (features[:, 8 + 6 * seat + 5, :] > 0) for seat in range(1, 4)], 1)
    return riichi, safe


def evaluate(model, split, batch, device, tenpai_cal, wait_cal, value_scale=1.):
    """Streaming report: one batch of rows in memory at a time. Reaction rows (legal set
    includes pass) are reported separately from turn rows."""
    layout = split.layout
    decisions = top1 = reaction_decisions = reaction_top1 = 0
    nll = uniform = 0.
    tenpai_raw, tenpai_rules, ron_raw, ron_safety = BinaryMetrics(), BinaryMetrics(), BinaryMetrics(), BinaryMetrics()
    value_count = q_count = 0
    value_abs = q_square = 0.
    pass_index = 74 if layout.actions > 74 else -1
    for rows, output in stream(model, split, batch, device):
        x, legal, human, targets, q = unpack(rows, layout)
        labeled = human >= 0
        if labeled.any():
            logits = output[labeled, :layout.actions].masked_fill(~legal[labeled], -1e9)
            hits = logits.argmax(1) == human[labeled]
            reaction = legal[labeled, pass_index] if pass_index >= 0 else torch.zeros_like(hits)
            decisions += int((~reaction).sum())
            top1 += int(hits[~reaction].sum())
            reaction_decisions += int(reaction.sum())
            reaction_top1 += int(hits[reaction].sum())
            nll += float(F.cross_entropy(logits, human[labeled], reduction="sum"))
            uniform += float(legal[labeled].sum(1).float().log().sum())
        riichi, safe = rule_masks(x, layout)
        tenpai = probability(output[:, layout.tenpai:layout.tenpai + 3], tenpai_cal).masked_fill(riichi, 1.)
        wait = probability(output[:, layout.ron:layout.ron + 102], wait_cal).masked_fill(safe, 0.)
        tenpai_raw.add(torch.sigmoid(output[:, layout.tenpai:layout.tenpai + 3]), targets[:, :3])
        tenpai_rules.add(tenpai, targets[:, :3])
        ron_raw.add(torch.sigmoid(output[:, layout.ron:layout.ron + 102]), targets[:, 3:105])
        ron_safety.add(wait, targets[:, 3:105])
        points = (F.softplus(output[:, layout.points:layout.points + 102]) * value_scale).clamp(0, 4)
        value_mask = targets[:, 105:] >= 0
        value_count += int(value_mask.sum())
        value_abs += float((points[value_mask] - targets[:, 105:][value_mask]).abs().sum())
        valid_q = torch.isfinite(q)
        q_count += int(valid_q.sum())
        q_square += float(((output[:, layout.q:][valid_q] - q[valid_q]) ** 2).sum())
    total = decisions + reaction_decisions
    return {"human_decisions": total, "turn_decisions": decisions, "reaction_decisions": reaction_decisions,
            "policy_top1": top1 / decisions if decisions else None,
            "reaction_top1": reaction_top1 / reaction_decisions if reaction_decisions else None,
            "policy_nll": nll / total if total else None,
            "uniform_legal_nll": uniform / total if total else None,
            "tenpai_raw": tenpai_raw.report(), "tenpai_calibrated_with_rules": tenpai_rules.report(),
            "conditional_ron_raw": ron_raw.report(), "conditional_ron_calibrated_with_safety": ron_safety.report(),
            "value_positive_count": value_count,
            "value_mae_points": value_abs / value_count * 32000 if value_count else None,
            "search_q_rmse_points": math.sqrt(q_square / q_count) * 32000 if q_count else None}


def validation_pass(model, split, batch, device):
    """Validation loss plus the small per-row pieces calibration needs (logits and masked
    targets of the tenpai/ron heads, value ratio terms); never the feature planes."""
    layout = split.layout
    total = 0.
    tenpai_logits, tenpai_targets, wait_logits, wait_targets = [], [], [], []
    value_target_sum = value_pred_sum = 0.
    value_count = 0
    for rows, output in stream(model, split, batch, device):
        x, legal, human, targets, q = unpack(rows, layout)
        total += float(objective(output, legal, human, targets, q, layout)) * len(rows)
        riichi, safe = rule_masks(x, layout)
        tenpai_logits.append(output[:, layout.tenpai:layout.tenpai + 3].clone())
        tenpai_targets.append(targets[:, :3].masked_fill(riichi, -1))
        wait_logits.append(output[:, layout.ron:layout.ron + 102].clone())
        wait_targets.append(targets[:, 3:105].masked_fill(safe, -1))
        value_mask = targets[:, 105:] >= 0
        value_count += int(value_mask.sum())
        value_target_sum += float(targets[:, 105:][value_mask].sum())
        value_pred_sum += float(F.softplus(output[:, layout.points:layout.points + 102])[value_mask].sum())
    return (total / len(split), torch.cat(tenpai_logits), torch.cat(tenpai_targets), torch.cat(wait_logits), torch.cat(wait_targets),
            value_count, value_target_sum, value_pred_sum)


def atomic_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, allow_nan=False, separators=(",", ":")), encoding="utf-8")
    os.replace(temporary, path)


def layer(module):
    weight = module.weight.detach().float().cpu()
    return {"Inputs": weight.shape[1], "Outputs": weight.shape[0], "Kernel": weight.shape[2] if weight.ndim == 3 else 1,
            "Weight": weight.flatten().tolist(), "Bias": module.bias.detach().float().cpu().tolist()}


def pick_device(name):
    if name == "auto":
        name = "cuda" if torch.cuda.is_available() else "cpu"
    if name == "cuda" and not torch.cuda.is_available():
        raise ValueError("CUDA requested but torch.cuda.is_available() is False; install a CUDA build of torch")
    return torch.device(name)


def train(args):
    if args.epochs < 1 or args.batch < 1 or args.threads < 1 or not math.isfinite(args.lr) or args.lr <= 0 or args.buffer_rows < args.batch:
        raise ValueError("Epochs, batch, threads and learning rate must be positive; buffer-rows must cover a batch")
    if args.channels < 1 or args.channels > 256 or args.hidden < 1 or args.hidden > 512 or args.blocks < 0 or args.blocks > 64:
        raise ValueError("channels 1-256, hidden 1-512, blocks 0-64 (LearnedModel limits)")
    device = pick_device(args.device)
    torch.set_num_threads(args.threads)
    torch.manual_seed(args.seed)
    np.random.seed(args.seed)
    random.seed(args.seed)
    manifest, layout, splits = load_dataset(args.dataset)
    architecture = f"cnn{args.channels}-res{args.blocks}-dense{args.hidden}-{layout.features_version}"
    identity = hashlib.sha256(json.dumps({"dataset": manifest, "seed": args.seed, "batch": args.batch, "lr": args.lr,
                                         "architecture": architecture}, sort_keys=True).encode()).hexdigest()
    if args.output.exists() and not args.resume:
        raise ValueError("Output exists; use a new directory or --resume")
    args.output.mkdir(parents=True, exist_ok=True)
    model = Network(layout, args.channels, args.hidden, args.blocks).to(device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    scaler = torch.amp.GradScaler("cuda", enabled=device.type == "cuda")
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
    print(json.dumps({"device": str(device), "architecture": architecture, "parameters": sum(p.numel() for p in model.parameters()),
                      "train_rows": len(splits["train"]), "schema": layout.schema}), flush=True)
    # Test data is not evaluated until model selection and calibration are frozen.
    for epoch in range(start, args.epochs):
        model.train()
        rng = np.random.default_rng(args.seed + epoch)
        total_loss, seen = 0., 0
        for rows in splits["train"].shuffled_batches(args.batch, args.buffer_rows, rng):
            x, legal, human, targets, q = unpack(rows, layout)
            x, legal, human, targets, q = (t.to(device, non_blocking=True) for t in (x, legal, human, targets, q))
            optimizer.zero_grad(set_to_none=True)
            with torch.autocast(device_type=device.type, enabled=device.type == "cuda"):
                output = model(x)
            loss = objective(output, legal, human, targets, q, layout)
            if not torch.isfinite(loss):
                raise FloatingPointError("Nonfinite training loss")
            scaler.scale(loss).backward()
            scaler.unscale_(optimizer)
            nn.utils.clip_grad_norm_(model.parameters(), 5.)
            scaler.step(optimizer)
            scaler.update()
            total_loss += float(loss.detach()) * len(rows)
            seen += len(rows)
        validation_loss = validation_pass(model, splits["validation"], args.batch, device)[0]
        entry = {"epoch": epoch + 1, "train_loss": total_loss / max(1, seen), "validation_loss": validation_loss}
        if args.memory_log:
            current, peak = resident_mb()
            entry["rss_mb"], entry["peak_rss_mb"] = round(current), round(peak)
            if device.type == "cuda":
                entry["gpu_peak_mb"] = round(torch.cuda.max_memory_allocated() / 2 ** 20)
        history.append(entry)
        print(json.dumps(entry), flush=True)
        if validation_loss < best_loss:
            best_loss = validation_loss
            torch.save(model.state_dict(), args.output / "best.pt.tmp")
            os.replace(args.output / "best.pt.tmp", args.output / "best.pt")
        torch.save({"identity": identity, "model": model.state_dict(), "optimizer": optimizer.state_dict(),
                    "epoch": epoch + 1, "best_loss": best_loss, "history": history, "rng": torch.get_rng_state()}, args.output / "checkpoint.pt.tmp")
        os.replace(args.output / "checkpoint.pt.tmp", checkpoint)
    model.load_state_dict(torch.load(args.output / "best.pt", map_location=device, weights_only=True))
    # Riichi is a deterministic tenpai fact, and known-safe tiles are hard rules.
    # Fit probabilistic calibration only where those rules do not decide the result.
    _, tenpai_logits, tenpai_targets, wait_logits, wait_targets, value_count, value_target_sum, value_pred_sum = \
        validation_pass(model, splits["validation"], args.batch, device)
    tenpai_cal, tenpai_note = fit_calibration(tenpai_logits, tenpai_targets)
    wait_cal, wait_note = fit_calibration(wait_logits, wait_targets)
    del tenpai_logits, tenpai_targets, wait_logits, wait_targets
    value_scale = float(min(10., max(.1, value_target_sum / value_pred_sum))) if value_count >= 30 and value_pred_sum > 0 else 1.
    common = {"Features": manifest["Features"], "Corpus": manifest["Corpus"], "Rules": manifest["Rules"],
              "Dense": layer(model.dense), "Output": layer(model.output),
              "TenpaiCalibration": tenpai_cal, "WaitCalibration": wait_cal, "ValueScale": value_scale,
              "HasSearchLabels": manifest["SearchRows"] > 0, "Status": "experimental-unvalidated"}
    # Always the schema-2 artifact (stem + blocks); LearnedModel accepts it for either
    # feature version, so a v1 dataset still trains and loads.
    artifact = {"Schema": 2, "Stem": layer(model.stem),
                "Blocks": [{"Conv1": layer(c1), "Conv2": layer(c2)} for c1, c2 in model.blocks], **common}
    atomic_json(args.output / "learned_policy.json", artifact)
    report = {"dataset": manifest, "training_identity": identity, "history": history, "architecture": architecture,
              "calibration": {"tenpai": tenpai_note, "conditional_ron": wait_note, "value_scale": value_scale},
              "validation": evaluate(model, splits["validation"], args.batch, device, tenpai_cal, wait_cal, value_scale),
              "test": evaluate(model, splits["test"], args.batch, device, tenpai_cal, wait_cal, value_scale),
              "limitations": ["Whole-game split; players and time periods can overlap.",
                              "Imitation accuracy is not playing strength; evaluate complete games before promotion.",
                              "Own-turn kan and win decisions retain the existing policy.",
                              "Opponent values exclude ura, honba and red winning-tile bonuses."]}
    atomic_json(args.output / "metrics.json", report)
    head = torch.from_numpy(splits["test"].read(0, 8).astype(np.float32))
    with torch.no_grad():
        model.eval()
        head_output = model(head[:, :layout.features].to(device)).float().cpu()
    vectors = [{"Input": head[i, :layout.features].tolist(), "Output": head_output[i].tolist()} for i in range(len(head))]
    atomic_json(args.output / "parity.json", vectors)
    print(json.dumps({"model": str(args.output / "learned_policy.json"), "test_policy_top1": report["test"]["policy_top1"],
                      "test_reaction_top1": report["test"]["reaction_top1"],
                      "test_tenpai_brier": report["test"]["tenpai_calibrated_with_rules"]["brier"]}), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("dataset", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--epochs", type=int, default=20)
    parser.add_argument("--batch", type=int, default=128)
    parser.add_argument("--threads", type=int, default=8)
    parser.add_argument("--lr", type=float, default=.001)
    parser.add_argument("--seed", type=int, default=20260921)
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--buffer-rows", type=int, default=32768, help="shuffle buffer in rows (11 KB float32 / 5.6 KB float16 each); bounds training memory")
    parser.add_argument("--memory-log", action="store_true", help="print resident set (and GPU peak) after every epoch")
    parser.add_argument("--channels", type=int, default=24, help="conv channels (C# LearnedModel allows up to 256)")
    parser.add_argument("--hidden", type=int, default=64, help="dense width (up to 512)")
    parser.add_argument("--blocks", type=int, default=0, help="residual blocks after the stem (0-64)")
    parser.add_argument("--device", default="auto", help="cpu, cuda or auto")
    train(parser.parse_args())
