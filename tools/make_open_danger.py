"""Merge tools/open_hand_danger.py outputs into resources/policy/open_danger.json.

Usage: python -I tools/make_open_danger.py <out.json> <SHA256SUMS> <scan1.json> [scan2.json ...]

Rates are conditional on the seat being tenpai (hits / tenpai observations); cost is the
average ron payment of a hit. Cells under MIN_N observations are dropped so the plugin
falls back to the coarser row instead of trusting noise.
"""
import json, sys, collections

MIN_N = 2000


def merge(paths):
    danger = collections.defaultdict(lambda: [0, 0, 0.0])
    tenpai = collections.defaultdict(lambda: [0, 0])
    yakuman = collections.Counter()
    games = 0
    for p in paths:
        d = json.load(open(p, encoding='utf-8'))
        games += d['games']
        for k, v in d['danger'].items():
            for i in range(3):
                danger[k][i] += v[i]
        for k, v in d['tenpai'].items():
            tenpai[k][0] += v[0]
            tenpai[k][1] += v[1]
        for k, v in d['yakuman'].items():
            yakuman[k] += v
    return games, danger, tenpai, yakuman


def main():
    out_path, sums_path, scans = sys.argv[1], sys.argv[2], sys.argv[3:]
    games, danger, tenpai, yakuman = merge(scans)
    table = {'danger': {}, 'dangerByPhase': {}, 'tenpai': {}}
    for key, (n, hits, cost) in sorted(danger.items()):
        k = json.loads(key)
        if n < MIN_N:
            continue
        cell = {'n': n, 'rate': round(hits / n, 5), 'cost': round(cost / hits) if hits else 0}
        if len(k) == 2:
            table['danger'].setdefault(str(k[0]), {})[str(k[1])] = cell
        elif len(k) == 3:
            table['dangerByPhase'].setdefault(k[0], {}).setdefault(k[1], {})[k[2]] = cell
    for key, (n, t) in sorted(tenpai.items()):
        k = json.loads(key)
        if n < MIN_N:
            continue
        node = table['tenpai'].setdefault(k[0], {}).setdefault(k[1], {})
        node[k[2] if len(k) == 3 else 'all'] = {'n': n, 'rate': round(t / n, 4)}
    sums = [line.split() for line in open(sums_path, encoding='utf-8') if line.strip()]
    doc = {
        'source': 'Tenhou Phoenix room archives (https://tenhou.net/0/log/mjlog_pf4-20_n<N>.zip), '
                  'counted by tools/open_hand_danger.py; rates are conditional on the seat being tenpai.',
        'games': games,
        'archives': [{'file': f.lstrip('*'), 'sha256': h} for h, f in sums],
        'minObservations': MIN_N,
        'yakumanWins': {k: v for k, v in sorted(yakuman.items())},
        **table,
    }
    json.dump(doc, open(out_path, 'w', encoding='utf-8'), indent=1)
    print(f'{games} games, {sum(len(v) for v in table["danger"].values())} danger cells -> {out_path}')


if __name__ == '__main__':
    main()
