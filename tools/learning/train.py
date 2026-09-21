"""Train expert imitation (discard / riichi / reaction) + opponent heads, optionally
assisted by offline Q labels.

Inputs are exported by `Precompute learn-data`; the C# encoder is the sole feature
implementation and the row layout comes from the dataset manifest (schema 1 = public-tiles-v1,
74 actions; schema 2 = public-tiles-v2 with look-ahead planes and 82 actions including
pass / pon / open kan / chi shapes / own-turn kans; schema 3 adds the acting seat's final
placement, trained as a fourth head). Hidden hands are targets only.

Network: 3×1 conv stem → N residual blocks (conv-relu-conv + skip) → dense → heads, the
Suphx/NAGA shape at a size this project can train. --blocks 0 reproduces the earlier
two-layer net (exported as schema 1 when the dataset is schema 1).

Memory: nothing is memory-mapped or copied whole. Training reads shuffled chunks of rows
into a bounded shuffle buffer (--buffer-rows); validation/test are streamed with metrics
accumulated per batch. --memory-log prints the resident set after every epoch.

GPU: --device cuda (or auto) with automatic mixed precision. Training data streams through
device memory: a reader thread fills a pinned window of randomly ordered contiguous chunks
(--window-rows, default ~a third of free GPU memory) while the GPU trains on the previous
window; shuffling, batching and unpacking happen on the device, and the objective has no
host/device synchronisation points. With that the GPU, not the loader, sets the epoch time,
so use big batches (--batch 4096) and a deeper network. On the CPU the loader runs on a
background thread instead (--prefetch batches ahead).
"""
import argparse
import ctypes
import hashlib
import json
import math
import os
from pathlib import Path
import queue
import random
import sys
import threading
import time

import numpy as np
import torch
from torch import nn
from torch.nn import functional as F

OPPONENTS = 207
WIDTH = 34


class Layout:
    """Row/output layout for one feature version (from the dataset manifest)."""

    def __init__(self, manifest):
        if manifest["Schema"] not in (1, 2, 3) or manifest["Features"] not in ("public-tiles-v1", "public-tiles-v2"):
            raise ValueError("Incompatible learning dataset schema")
        self.schema = manifest["Schema"]
        self.features_version = manifest["Features"]
        self.channels = manifest["Channels"]
        self.actions = manifest["Actions"]
        self.features = self.channels * WIDTH
        self.placement = self.schema >= 3
        self.row = self.features + self.actions + 1 + OPPONENTS + self.actions + (1 if self.placement else 0)
        if manifest["RowFloats"] != self.row:
            raise ValueError("Dataset row size does not match its declared layout")
        self.dtype = "<f2" if manifest.get("Dtype", "float32") == "float16" else "<f4"
        self.item = 2 if self.dtype == "<f2" else 4
        self.extension = manifest.get("Extension", ".f32")
        self.outputs = 2 * self.actions + OPPONENTS + (4 if self.placement else 0)
        self.placement_offset = 2 * self.actions + OPPONENTS
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
    offset = layout.features + layout.actions + 1
    targets = rows[:, offset:offset + OPPONENTS]
    q = rows[:, offset + OPPONENTS:offset + OPPONENTS + layout.actions]
    # Placement 1..4 -> class 0..3, -1 = unknown (search rows).
    placement = rows[:, -1].long() - 1 if layout.placement else torch.full((len(rows),), -1, dtype=torch.long, device=rows.device)
    return x, legal, human, targets, q, placement


def masked_loss(prediction, target, kind):
    """Mean over the labelled elements (-1 / NaN = unlabelled), 0 when nothing is labelled.
    Weights instead of boolean indexing: no host/device synchronisation."""
    mask = (torch.isfinite(target) if kind == "q" else target >= 0).float()
    target = torch.nan_to_num(target.float(), nan=0.).clamp(min=0)
    prediction = prediction.float()
    if kind == "binary":
        total = F.binary_cross_entropy_with_logits(prediction, target, weight=mask, reduction="sum")
    else:
        if kind == "value":
            prediction = F.softplus(prediction)
        total = ((prediction - target) ** 2 * mask).sum()
    return total / mask.sum().clamp(min=1)


def classes_loss(logits, labels):
    """Cross-entropy averaged over the rows whose label is not -1, 0 when there are none."""
    total = F.cross_entropy(logits.float(), labels, ignore_index=-1, reduction="sum")
    return total / (labels >= 0).sum().clamp(min=1)


def objective(output, legal, human, targets, q, placement, layout):
    output = output.float()
    a = layout.actions
    policy = classes_loss(output[:, :a].masked_fill(~legal, -1e9), human)
    placed = classes_loss(output[:, layout.placement_offset:layout.placement_offset + 4], placement) if layout.placement else 0.
    return (policy + .5 * masked_loss(output[:, layout.tenpai:layout.tenpai + 3], targets[:, :3], "binary")
            + .5 * masked_loss(output[:, layout.ron:layout.ron + 102], targets[:, 3:105], "binary")
            + masked_loss(output[:, layout.points:layout.points + 102], targets[:, 105:], "value")
            + .2 * masked_loss(output[:, layout.q:layout.q + a], q, "q")
            + .3 * placed)


class DeviceWindows:
    """Training batches from windows of rows resident on the device. The file is visited in
    random chunk order (contiguous chunks of `chunk` rows, like Split.shuffled_batches); a
    reader thread fills a pinned staging buffer with the next window while the GPU trains on
    the current one; the window is copied over in its stored dtype and shuffled, sliced and
    unpacked on the device. Host work per epoch is one sequential-ish read of the file."""

    def __init__(self, split, batch, window_rows, rng, device, seed):
        self.split, self.batch, self.device = split, batch, device
        layout = split.layout
        self.chunk = max(batch, min(8192, window_rows))
        starts = list(range(0, split.rows, self.chunk))
        rng.shuffle(starts)
        per_window = max(1, window_rows // self.chunk)
        self.windows = [starts[i:i + per_window] for i in range(0, len(starts), per_window)]
        dtype = torch.float16 if layout.item == 2 else torch.float32
        self.staging = torch.empty((per_window * self.chunk, layout.row), dtype=dtype, pin_memory=device.type == "cuda")
        self.view = self.staging.numpy()
        self.ready = queue.Queue(maxsize=1)     # rows filled in staging, or an exception
        self.free = threading.Semaphore(1)      # staging may be overwritten
        self.generator = torch.Generator().manual_seed(seed)
        self.thread = threading.Thread(target=self._read, daemon=True)
        self.thread.start()

    def _read(self):
        try:
            with self.split.file.open("rb") as file:
                for window in self.windows:
                    self.free.acquire()
                    filled = 0
                    for start in window:
                        count = min(self.chunk, self.split.rows - start)
                        file.seek(start * self.split.layout.row * self.split.layout.item)
                        file.readinto(memoryview(self.view[filled:filled + count]).cast("B"))
                        filled += count
                    self.ready.put(filled)
        except BaseException as error:  # noqa: BLE001 - forwarded to the consumer
            self.ready.put(error)

    def __iter__(self):
        layout = self.split.layout
        for _ in self.windows:
            item = self.ready.get()
            if isinstance(item, BaseException):
                raise item
            rows = self.staging[:item].to(self.device, copy=True)   # synchronous: staging is reused right after
            self.free.release()
            order = torch.randperm(item, generator=self.generator).to(self.device)
            for j in range(0, item, self.batch):
                index = order[j:j + self.batch]
                yield len(index), unpack(rows.index_select(0, index).float(), layout)
            del rows


def device_window_rows(split, requested, device):
    """Rows per device window: the request, or about a third of the free device memory."""
    if requested > 0 or device.type != "cuda":
        return min(requested if requested > 0 else 262144, split.rows)
    free, _ = torch.cuda.mem_get_info()
    return max(8192, min(split.rows, int(free * .35 // (split.layout.row * split.layout.item))))


class Prefetcher:
    """CPU training: runs the batch iterator on a background thread so reading, shuffling and
    unpacking overlap the optimiser step. Exceptions on the thread are re-raised in the consumer."""

    _done = object()

    def __init__(self, batches, layout, device, depth):
        self.queue = queue.Queue(maxsize=max(1, depth))
        self.layout, self.device = layout, device
        self.thread = threading.Thread(target=self._run, args=(batches,), daemon=True)
        self.thread.start()

    def _run(self, batches):
        try:
            for rows in batches:
                parts = unpack(rows, self.layout)
                if self.device.type == "cuda":
                    parts = tuple(t.contiguous().pin_memory().to(self.device, non_blocking=True) for t in parts)
                self.queue.put((len(rows), parts))
        except BaseException as error:  # noqa: BLE001 - forwarded to the consumer
            self.queue.put(error)
        finally:
            self.queue.put(self._done)

    def __iter__(self):
        while True:
            item = self.queue.get()
            if item is self._done:
                return
            if isinstance(item, BaseException):
                raise item
            yield item


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
    placement_count = placement_top1 = rank_top1 = 0
    placement_nll = 0.
    pass_index = 74 if layout.actions > 74 else -1
    for rows, output in stream(model, split, batch, device):
        x, legal, human, targets, q, placement = unpack(rows, layout)
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
        q_square += float(((output[:, layout.q:layout.q + layout.actions][valid_q] - q[valid_q]) ** 2).sum())
        known = placement >= 0
        if layout.placement and known.any():
            logits = output[known, layout.placement_offset:layout.placement_offset + 4]
            placement_count += int(known.sum())
            placement_top1 += int((logits.argmax(1) == placement[known]).sum())
            placement_nll += float(F.cross_entropy(logits, placement[known], reduction="sum"))
            # Baseline: current rank by score (planes 32-35 hold score / 50000 per relative seat).
            scores = x.reshape(-1, layout.channels, WIDTH)[known, 32:36, 0]
            rank = (scores[:, 1:] > scores[:, :1]).sum(1)
            rank_top1 += int((rank == placement[known]).sum())
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
            "search_q_rmse_points": math.sqrt(q_square / q_count) * 32000 if q_count else None,
            "placement_count": placement_count,
            "placement_top1": placement_top1 / placement_count if placement_count else None,
            "placement_nll": placement_nll / placement_count if placement_count else None,
            "placement_top1_by_current_rank": rank_top1 / placement_count if placement_count else None}


def validation_pass(model, split, batch, device):
    """Validation loss plus the small per-row pieces calibration needs (logits and masked
    targets of the tenpai/ron heads, value ratio terms); never the feature planes."""
    layout = split.layout
    total = 0.
    tenpai_logits, tenpai_targets, wait_logits, wait_targets = [], [], [], []
    value_target_sum = value_pred_sum = 0.
    value_count = 0
    for rows, output in stream(model, split, batch, device):
        x, legal, human, targets, q, placement = unpack(rows, layout)
        total += float(objective(output, legal, human, targets, q, placement, layout)) * len(rows)
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
    torch.backends.cudnn.benchmark = True   # fixed 34-wide shapes: let cuDNN pick the conv kernels once
    torch.manual_seed(args.seed)
    np.random.seed(args.seed)
    random.seed(args.seed)
    manifest, layout, splits = load_dataset(args.dataset)
    architecture = f"cnn{args.channels}-res{args.blocks}-dense{args.hidden}-{layout.features_version}"
    identity_fields = {"dataset": manifest, "seed": args.seed, "batch": args.batch, "lr": args.lr, "architecture": architecture}
    if args.schedule != "constant":
        identity_fields["schedule"] = f"{args.schedule}-{args.epochs}"   # the decay horizon is part of the run
    identity = hashlib.sha256(json.dumps(identity_fields, sort_keys=True).encode()).hexdigest()
    if args.output.exists() and not args.resume:
        raise ValueError("Output exists; use a new directory or --resume")
    args.output.mkdir(parents=True, exist_ok=True)
    model = Network(layout, args.channels, args.hidden, args.blocks).to(device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    # Cosine decay to 5 % of the base rate over the planned epochs: the usual +0.5-1 pt of
    # imitation accuracy at the end of a run over a constant rate.
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=args.epochs, eta_min=args.lr * .05) if args.schedule == "cosine" else None
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
        if scheduler is not None and saved.get("scheduler") is not None:
            scheduler.load_state_dict(saved["scheduler"])
        torch.set_rng_state(saved["rng"])
        start, best_loss, history = saved["epoch"], saved["best_loss"], saved["history"]
    use_windows = args.loader == "windows" or args.loader == "auto" and device.type == "cuda"
    window_rows = device_window_rows(splits["train"], args.window_rows, device) if use_windows else 0
    print(json.dumps({"device": str(device), "window_rows": window_rows, "architecture": architecture, "parameters": sum(p.numel() for p in model.parameters()),
                      "train_rows": len(splits["train"]), "schema": layout.schema}), flush=True)
    # Test data is not evaluated until model selection and calibration are frozen.
    for epoch in range(start, args.epochs):
        model.train()
        rng = np.random.default_rng(args.seed + epoch)
        total_loss, seen, steps = torch.zeros((), device=device), 0, 0
        epoch_started = time.time()
        if use_windows:
            batches = DeviceWindows(splits["train"], args.batch, window_rows, rng, device, args.seed + epoch)
        else:
            batches = Prefetcher(splits["train"].shuffled_batches(args.batch, args.buffer_rows, rng), layout, device, args.prefetch)
        for count, (x, legal, human, targets, q, placement) in batches:
            optimizer.zero_grad(set_to_none=True)
            with torch.autocast(device_type=device.type, enabled=device.type == "cuda"):
                output = model(x)
            loss = objective(output, legal, human, targets, q, placement, layout)
            scaler.scale(loss).backward()
            scaler.unscale_(optimizer)
            nn.utils.clip_grad_norm_(model.parameters(), 5.)
            scaler.step(optimizer)
            scaler.update()
            total_loss += loss.detach() * count
            seen += count
            steps += 1
            # The only synchronisation in the loop: a periodic finiteness check.
            if steps % 256 == 0 and not torch.isfinite(total_loss):
                raise FloatingPointError("Nonfinite training loss")
        total_loss = float(total_loss)
        if not math.isfinite(total_loss):
            raise FloatingPointError("Nonfinite training loss")
        train_seconds = time.time() - epoch_started
        validation_loss = validation_pass(model, splits["validation"], args.batch, device)[0]
        entry = {"epoch": epoch + 1, "train_loss": total_loss / max(1, seen), "validation_loss": validation_loss,
                 "train_seconds": round(train_seconds), "validation_seconds": round(time.time() - epoch_started - train_seconds)}
        if args.memory_log:
            current, peak = resident_mb()
            entry["rss_mb"], entry["peak_rss_mb"] = round(current), round(peak)
            if device.type == "cuda":
                entry["gpu_peak_mb"] = round(torch.cuda.max_memory_allocated() / 2 ** 20)
        entry["lr"] = optimizer.param_groups[0]["lr"]
        if scheduler is not None:
            scheduler.step()
        history.append(entry)
        print(json.dumps(entry), flush=True)
        if validation_loss < best_loss:
            best_loss = validation_loss
            torch.save(model.state_dict(), args.output / "best.pt.tmp")
            os.replace(args.output / "best.pt.tmp", args.output / "best.pt")
        torch.save({"identity": identity, "model": model.state_dict(), "optimizer": optimizer.state_dict(),
                    "scheduler": scheduler.state_dict() if scheduler is not None else None,
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
              "HasSearchLabels": manifest["SearchRows"] > 0, "HasPlacementHead": layout.placement, "Status": "experimental-unvalidated"}
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
                      "test_placement_top1": report["test"]["placement_top1"],
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
    parser.add_argument("--prefetch", type=int, default=6, help="CPU training: batches prepared ahead on the loader thread")
    parser.add_argument("--window-rows", type=int, default=0, help="CUDA training: rows per device-resident window (0 = ~a third of free GPU memory)")
    parser.add_argument("--loader", default="auto", choices=["auto", "windows", "prefetch"], help="auto = device windows on CUDA, prefetch thread on CPU")
    parser.add_argument("--schedule", default="cosine", choices=["cosine", "constant"], help="learning-rate schedule over --epochs")
    train(parser.parse_args())
