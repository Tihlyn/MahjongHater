"""Per-player style from the plugin's hand_rivers.jsonl (Core/Policy/HandRivers.cs).

Usage: python -I tools/hand_rivers.py <hand_rivers.jsonl> [--population human|npc]

Seats are relative to us and fixed within a match, so (match, seat) is one player. A match
ends when a hand's scores are back to 25 000 each on East 1. Per player it prints the hands
seen, the early-middle share, call and riichi rates, the genbutsu share of its discards after
another seat's riichi, and P(novice) from the same two-type classifier as OpponentProfiles
(the constants below must match it).
"""
import json, math, sys, collections

STEADY_EARLY = [0.421, 0.3099, 0.1736, 0.0691, 0.0209, 0.0045, 0.001]
STEADY_FOLD = [0.0455, 0.1316, 0.1719, 0.3111, 0.3399]
NOVICE_FOLD = [0.25, 0.30, 0.22, 0.13, 0.10]
PRIOR, TEMPER, MEAN, CONCENTRATION = 0.25, 0.7, 0.35, 2.0


def beta_binomial(n, mean, c):
    a, b = mean * c, (1 - mean) * c
    lb = lambda x, y: math.lgamma(x) + math.lgamma(y) - math.lgamma(x + y)
    return [math.comb(n, k) * math.exp(lb(k + a, n - k + b) - lb(a, b)) for k in range(n + 1)]


NOVICE_EARLY = beta_binomial(6, MEAN, CONCENTRATION)


def kind(t):
    # "5m", "0p" (red five) or "7z" -> 0..33
    n, s = int(t[0]) or 5, t[1]
    return 'mpsz'.index(s) * 9 + n - 1


def middle(t):
    return t[1] != 'z' and 3 <= (int(t[0]) or 5) <= 7


def fold_share(seat, seats):
    """Genbutsu share of `seat`'s discards after the first other riichi, None if < 3."""
    if not seat['order']:
        return None
    riichi = [s for s in seats if s['seat'] != seat['seat'] and s['riichi'] >= 0 and s['order']]
    if not riichi:
        return None
    r = min(riichi, key=lambda s: s['order'][s['riichi']])
    at = r['order'][r['riichi']]
    if seat['riichi'] >= 0 and seat['order'][seat['riichi']] < at:
        return None
    every = [(kind(t), o) for s in seats if s['order'] for t, o in zip(s['tiles'], s['order'])]
    own = {kind(t) for t in r['tiles']}
    n = safe = 0
    for t, o in zip(seat['tiles'], seat['order']):
        if o <= at:
            continue
        k = kind(t)
        n += 1
        safe += k in own or any(kk == k and at < oo < o for kk, oo in every)
    return safe / n if n >= 3 else None


def main():
    path = sys.argv[1]
    population = sys.argv[sys.argv.index('--population') + 1] if '--population' in sys.argv else None
    hands = [json.loads(line) for line in open(path, encoding='utf-8') if line.strip()]
    if population:
        hands = [h for h in hands if h.get('population') == population]
    match = -1
    players = collections.defaultdict(lambda: {'hands': 0, 'em': 0, 'en': 0, 'called': 0, 'riichi': 0, 'safe': [], 'llr': math.log(PRIOR / (1 - PRIOR))})
    for h in hands:
        seats = h['seats']
        for s in seats:
            s['tiles'] = s['discards'].split() if s['discards'] else []
            s['order'] = s['order'] or []
        if match < 0 or (h['round'] == 'East' and all(s['score'] == 25000 for s in seats)):
            match += 1
        for s in seats:
            if s['seat'] == 0 or not s['verified']:
                continue
            p = players[(match, s['seat'])]
            p['hands'] += 1
            early = s['tiles'][:6]
            m = sum(map(middle, early))
            p['em'] += m; p['en'] += len(early)
            p['called'] += any(not x.endswith(':closed') for x in s['melds'])
            p['riichi'] += s['riichi'] >= 0
            evidence = 0.0
            if len(early) == 6:
                evidence += math.log(NOVICE_EARLY[m] / STEADY_EARLY[m])
            share = fold_share(s, seats)
            if share is not None:
                p['safe'].append(share)
                fifth = min(4, int(5 * share))
                evidence += math.log(NOVICE_FOLD[fifth] / STEADY_FOLD[fifth])
            p['llr'] += TEMPER * evidence
    print(f"{len(hands)} hands, {match + 1} matches, {len(players)} players")
    print("match seat hands earlyMid called riichi foldShare(hands) P(novice)")
    for (m, seat), p in sorted(players.items()):
        fold = f"{sum(p['safe']) / len(p['safe']):5.0%}({len(p['safe'])})" if p['safe'] else "    -   "
        print(f"{m + 1:5} {seat:4} {p['hands']:5} {p['em'] / max(1, p['en']):7.0%} {p['called']:3}/{p['hands']:<3}"
              f"{p['riichi']:3}/{p['hands']:<3} {fold:>10} {1 / (1 + math.exp(-p['llr'])):8.2f}")


if __name__ == '__main__':
    main()
