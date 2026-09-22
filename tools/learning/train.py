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

GPU: --device cuda (or auto) with automatic mixed precision. Every split streams through
device memory: a reader thread reads the file in randomly ordered contiguous chunks through a
small ring of pinned pieces straight into one of two device-resident windows (--window-rows,
default ~a quarter of free GPU memory each) while the GPU works on the other; shuffling,
batching, unpacking, the objective and every validation/test metric run on the device, and
the training loop has no host/device synchronisation points. Host memory stays under a
gigabyte whatever the window size. On the CPU the loader runs on a background thread instead
(--prefetch batches ahead).
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
    def __init__(self, layout, channels=24, hidden=64, blocks=0, dropout=0.):
        super().__init__()
        self.layout = layout
        self.dropout = nn.Dropout(dropout)   # on the flattened planes before the dense layer; identity at inference
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
        return self.output(F.relu(self.dense(self.dropout(h.flatten(1)))))


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


class DeviceStream:
    """Rows of a split resident on the device, filled by a reader thread.

    The file is visited in contiguous chunks of `chunk` rows (random order when a generator is
    given, file order otherwise). Chunks go through a ring of small pinned pieces into one of
    two device windows on a copy stream while the consumer works on the other window, so the
    host holds a few hundred MB however large the windows are. The consumer shuffles, slices
    and unpacks on the device. One object serves every split of a dataset."""

    def __init__(self, layout, device, window_rows, chunk=8192, pieces=4):
        self.layout, self.device = layout, device
        self.chunk = chunk
        self.window_rows = max(chunk, window_rows // chunk * chunk)
        dtype = torch.float16 if layout.item == 2 else torch.float32
        cuda = device.type == "cuda"
        self.pieces = [torch.empty((chunk, layout.row), dtype=dtype, pin_memory=cuda) for _ in range(pieces)]
        self.piece_views = [piece.numpy() for piece in self.pieces]
        self.piece_events = [None] * pieces
        self.windows = [torch.empty((self.window_rows, layout.row), dtype=dtype, device=device) for _ in range(2)]
        self.copy_stream = torch.cuda.Stream(device) if cuda else None

    def _event(self):
        return torch.cuda.Event() if self.device.type == "cuda" else None

    def run(self, split, batch, rng=None, seed=0):
        """Iterate (count, unpacked parts) over the whole split once."""
        starts = list(range(0, split.rows, self.chunk))
        if rng is not None:
            rng.shuffle(starts)
        per_window = self.window_rows // self.chunk
        plan = [starts[i:i + per_window] for i in range(0, len(starts), per_window)]
        ready = queue.Queue(maxsize=1)
        released = [threading.Semaphore(1), threading.Semaphore(1)]   # window may be overwritten
        done_events = [None, None]                                     # consumer's last use of the window

        def read():
            try:
                with split.file.open("rb") as file:
                    for w, window in enumerate(plan):
                        slot = w % 2
                        released[slot].acquire()
                        target = self.windows[slot]
                        if self.copy_stream is not None and done_events[slot] is not None:
                            self.copy_stream.wait_event(done_events[slot])
                        filled = 0
                        for n, start in enumerate(window):
                            k = n % len(self.pieces)
                            if self.piece_events[k] is not None:
                                self.piece_events[k].synchronize()      # the piece's previous copy has landed
                            count = min(self.chunk, split.rows - start)
                            file.seek(start * self.layout.row * self.layout.item)
                            file.readinto(memoryview(self.piece_views[k][:count]).cast("B"))
                            if self.copy_stream is not None:
                                with torch.cuda.stream(self.copy_stream):
                                    target[filled:filled + count].copy_(self.pieces[k][:count], non_blocking=True)
                                    self.piece_events[k] = self._event()
                                    self.piece_events[k].record(self.copy_stream)
                            else:
                                target[filled:filled + count].copy_(self.pieces[k][:count])
                            filled += count
                        landed = self._event()
                        if landed is not None:
                            landed.record(self.copy_stream)
                        ready.put((slot, filled, landed))
            except BaseException as error:  # noqa: BLE001 - forwarded to the consumer
                ready.put(error)

        threading.Thread(target=read, daemon=True).start()
        generator = torch.Generator().manual_seed(seed)
        for _ in plan:
            item = ready.get()
            if isinstance(item, BaseException):
                raise item
            slot, filled, landed = item
            if landed is not None:
                torch.cuda.current_stream().wait_event(landed)
            rows = self.windows[slot][:filled]
            if rng is not None:
                order = torch.randperm(filled, generator=generator).to(self.device)
                for j in range(0, filled, batch):
                    index = order[j:j + batch]
                    yield len(index), unpack(rows.index_select(0, index).float(), self.layout)
            else:
                for j in range(0, filled, batch):
                    piece = rows[j:j + batch]
                    yield len(piece), unpack(piece.float(), self.layout)
            if self.device.type == "cuda":
                done_events[slot] = self._event()
                done_events[slot].record()
            released[slot].release()


def device_window_rows(layout, requested, device):
    """Rows per device window (two windows): the request, or about a quarter of free memory."""
    if requested > 0:
        return requested
    if device.type != "cuda":
        return 131072
    free, _ = torch.cuda.mem_get_info()
    return max(8192, int(free * .25 // (layout.row * layout.item)))


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
def stream(model, split, batch, device, loader=None):
    """Yield (unpacked parts, output) per batch with the model in eval mode, everything on the
    device when a DeviceStream is given (metrics then run there too)."""
    model.eval()
    if loader is not None:
        for _, parts in loader.run(split, batch):
            with torch.autocast(device_type=device.type, enabled=device.type == "cuda"):
                output = model(parts[0])
            yield parts, output.float()
        return
    for _, rows in split.batches(batch):
        parts = unpack(rows, split.layout)
        x = parts[0].to(device, non_blocking=True)
        with torch.autocast(device_type=device.type, enabled=device.type == "cuda"):
            output = model(x)
        yield parts, output.float().cpu()


def fit_calibration(logits, targets):
    mask = targets >= 0
    x, y = logits[mask].detach().float(), targets[mask].detach().float()
    if len(y) < 30 or y.sum() < 5 or (1 - y).sum() < 5:
        return {"Slope": 1., "Bias": 0.}, "insufficient validation positives/negatives; identity calibration"
    raw_slope = nn.Parameter(torch.tensor(math.log(math.expm1(1.)), device=x.device))
    bias = nn.Parameter(torch.tensor(0., device=x.device))
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


def evaluate(model, split, batch, device, tenpai_cal, wait_cal, value_scale=1., loader=None):
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
    for (x, legal, human, targets, q, placement), output in stream(model, split, batch, device, loader):
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


def validation_pass(model, split, batch, device, loader=None, collect=False):
    """Validation loss; with `collect`, also the per-row pieces calibration needs (logits and
    masked targets of the tenpai/ron heads, value ratio terms), never the feature planes.
    Everything stays on the device until the end."""
    layout = split.layout
    total = torch.zeros((), device=device if loader is not None else "cpu", dtype=torch.float64)
    tenpai_logits, tenpai_targets, wait_logits, wait_targets = [], [], [], []
    value_target_sum = value_pred_sum = 0.
    value_count = 0
    for (x, legal, human, targets, q, placement), output in stream(model, split, batch, device, loader):
        total += objective(output, legal, human, targets, q, placement, layout).double() * len(x)
        if not collect:
            continue
        riichi, safe = rule_masks(x, layout)
        tenpai_logits.append(output[:, layout.tenpai:layout.tenpai + 3].clone())
        tenpai_targets.append(targets[:, :3].masked_fill(riichi, -1))
        wait_logits.append(output[:, layout.ron:layout.ron + 102].clone())
        wait_targets.append(targets[:, 3:105].masked_fill(safe, -1))
        value_mask = targets[:, 105:] >= 0
        value_count += int(value_mask.sum())
        value_target_sum += float(targets[:, 105:][value_mask].sum())
        value_pred_sum += float(F.softplus(output[:, layout.points:layout.points + 102])[value_mask].sum())
    loss = float(total) / len(split)
    if not collect:
        return (loss,)
    return (loss, torch.cat(tenpai_logits), torch.cat(tenpai_targets), torch.cat(wait_logits), torch.cat(wait_targets),
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
    if not 0 <= args.dropout < 1:
        raise ValueError("dropout must be in [0, 1)")
    device = pick_device(args.device)
    # With everything on the device the CPU only orchestrates; spare threads would just spin.
    torch.set_num_threads(min(args.threads, 4) if device.type == "cuda" else args.threads)
    torch.backends.cudnn.benchmark = True   # fixed 34-wide shapes: let cuDNN pick the conv kernels once
    # No TF32: training runs under fp16 autocast anyway, and the remaining fp32 maths must match
    # the plugin's exact fp32 inference (learn-check tolerance 2e-4).
    torch.backends.cuda.matmul.allow_tf32 = False
    torch.backends.cudnn.allow_tf32 = False
    torch.manual_seed(args.seed)
    np.random.seed(args.seed)
    random.seed(args.seed)
    manifest, layout, splits = load_dataset(args.dataset)
    architecture = f"cnn{args.channels}-res{args.blocks}-dense{args.hidden}-{layout.features_version}"
    identity_fields = {"dataset": manifest, "seed": args.seed, "batch": args.batch, "lr": args.lr, "architecture": architecture}
    if args.schedule != "constant":
        identity_fields["schedule"] = f"{args.schedule}-{args.epochs}"   # the decay horizon is part of the run
    if args.dropout > 0:
        identity_fields["dropout"] = args.dropout
    identity = hashlib.sha256(json.dumps(identity_fields, sort_keys=True).encode()).hexdigest()
    if args.output.exists() and not args.resume:
        raise ValueError("Output exists; use a new directory or --resume")
    args.output.mkdir(parents=True, exist_ok=True)
    model = Network(layout, args.channels, args.hidden, args.blocks, args.dropout).to(device)
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
    window_rows = device_window_rows(layout, args.window_rows, device) if use_windows else 0
    loader = DeviceStream(layout, device, window_rows) if use_windows else None
    print(json.dumps({"device": str(device), "window_rows": window_rows, "architecture": architecture, "parameters": sum(p.numel() for p in model.parameters()),
                      "train_rows": len(splits["train"]), "schema": layout.schema}), flush=True)
    # Test data is not evaluated until model selection and calibration are frozen.
    for epoch in range(start, args.epochs):
        model.train()
        rng = np.random.default_rng(args.seed + epoch)
        total_loss, seen, steps = torch.zeros((), device=device), 0, 0
        epoch_started = time.time()
        if loader is not None:
            batches = loader.run(splits["train"], args.batch, rng, args.seed + epoch)
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
        validation_loss = validation_pass(model, splits["validation"], args.batch, device, loader)[0]
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
        validation_pass(model, splits["validation"], args.batch, device, loader, collect=True)
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
              "validation": evaluate(model, splits["validation"], args.batch, device, tenpai_cal, wait_cal, value_scale, loader),
              "test": evaluate(model, splits["test"], args.batch, device, tenpai_cal, wait_cal, value_scale, loader),
              "limitations": ["Whole-game split; players and time periods can overlap.",
                              "Imitation accuracy is not playing strength; evaluate complete games before promotion.",
                              "Own-turn kan and win decisions retain the existing policy.",
                              "Opponent values exclude ura, honba and red winning-tile bonuses."]}
    atomic_json(args.output / "metrics.json", report)
    head = torch.from_numpy(splits["test"].read(0, 8).astype(np.float32))   # first eight test rows, straight from the file
    with torch.no_grad():
        # Parity vectors in plain fp32 on the CPU: the reference the C# inference is checked against.
        reference = model.eval().to("cpu")
        head_output = reference(head[:, :layout.features]).float()
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
    parser.add_argument("--window-rows", type=int, default=0, help="CUDA: rows per device-resident window, two windows (0 = ~a quarter of free GPU memory each)")
    parser.add_argument("--loader", default="auto", choices=["auto", "windows", "prefetch"], help="auto = device windows on CUDA, prefetch thread on CPU")
    parser.add_argument("--schedule", default="cosine", choices=["cosine", "constant"], help="learning-rate schedule over --epochs")
    parser.add_argument("--dropout", type=float, default=0.1, help="dropout before the dense layer (the first 11 M-row run overfit from epoch 6)")
    train(parser.parse_args())
