"""Pair Cartographer callbacks with the AtkEvents that accompanied them.

Pair by FRAME, never by timestamp. A callback and the event that produced it are 20-50 us
apart, which straddles a millisecond boundary often enough to matter: bucketing the
2026-09-23 capture by millisecond split 4 of 100 discard pairs and produced a "the game
fires this command with no event at all" finding that does not exist. The capture carries a
frame number for exactly this reason.

    python tools/pair_callbacks.py artifacts/capture/timeline.json
    python tools/pair_callbacks.py artifacts/capture/timeline.json --addon Emj --head 7 -v

Output columns: fires / paired / unpaired, and the event signatures seen per head. A head
with unpaired fires is one the addon may emit on its own; a head that is always paired tells
you the payload and nothing more. Sufficiency of a bare callback is an outcome test, not
something this script can answer.
"""

import argparse
import collections
import json
import sys


def load(path):
    with open(path, encoding="utf-8-sig") as fh:
        data = json.load(fh)
    return data if isinstance(data, list) else data.get("entries", [])


def frame_of(entry):
    return entry.get("Frame", entry.get("Values", {}).get("Frame"))


def head_of(entry):
    if "Head" in entry:
        return entry["Head"]
    return entry.get("Data", entry.get("Values", {})).get("Head")


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("timeline")
    ap.add_argument("--addon", default="Emj")
    ap.add_argument("--head", type=int, default=None, help="only this callback head")
    ap.add_argument("-v", "--verbose", action="store_true", help="list every pair")
    args = ap.parse_args()

    entries = load(args.timeline)

    events = collections.defaultdict(list)
    for e in entries:
        if e.get("Kind") == "AddonReceiveEvent" and e.get("Subject") == args.addon:
            events[frame_of(e)].append(e)

    callbacks = [e for e in entries
                 if e.get("Kind") == "Callback"
                 and e.get("Data", {}).get("Addon") == args.addon
                 and (args.head is None or head_of(e) == args.head)]

    if not callbacks:
        print(f"no callbacks for addon {args.addon!r} in {args.timeline}", file=sys.stderr)
        return 1

    groups = collections.defaultdict(list)
    for c in callbacks:
        groups[head_of(c)].append(c)

    print(f"{args.addon}: {len(callbacks)} callbacks, frame-paired against "
          f"{sum(len(v) for v in events.values())} events\n")
    print(f"{'head':>5} {'fires':>6} {'paired':>7} {'unpaired':>9}  event signatures")

    for head in sorted(groups, key=lambda h: (h is None, h)):
        group = groups[head]
        paired = 0
        sigs = collections.Counter()
        for c in group:
            same = [e for e in events.get(frame_of(c), [])
                    if e.get("Data", {}).get("listenerKind") == "addon"]
            if same:
                paired += 1
                for e in same:
                    d = e["Data"]
                    sigs[f"{d['typeName']} node={d['nodeId']}"] += 1
            if args.verbose:
                mark = "  " if same else "!!"
                what = (f"{same[0]['Data']['typeName']} "
                        f"param={same[0]['Data']['param']} "
                        f"node={same[0]['Data']['nodeId']}") if same else "NO EVENT IN FRAME"
                print(f"  {mark} {c['Utc']} frame {frame_of(c)} {c['Summary']:<22} {what}")
        top = ", ".join(f"{s} x{n}" for s, n in sigs.most_common(3)) or "-"
        print(f"{head:>5} {len(group):>6} {paired:>7} {len(group) - paired:>9}  {top}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
