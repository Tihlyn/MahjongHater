"""Per-player style baselines from Tenhou Phoenix replays, for OpponentProfiles' priors.

Usage: python -I tools/seat_style_baseline.py <mjlog_archive.zip> <out.json> [max_games] [workers]

Per player-game (one seat of one hanchan) it counts, per hand: whether the seat called
(chi/pon/open kan), whether it declared riichi, the middle tiles (3-7) among its first six
discards, and, after somebody else's riichi, how many of its discards were genbutsu to that
riichi (the riichi seat's own discards or any tile discarded after the declaration) — the
same definitions the plugin's profiles and tools/hand_rivers.py use on live logs.

The output gives the pooled rate of each statistic and its split-half reliability: how well
a player's first half of the game predicts the second (Pearson r of the two half rates).
Discards within a hand are correlated (a tenpai seat pushes all hand long), so binomial noise
understates the noise; split halves measure it. Spearman-Brown turns r into the reliability
of one hand, rho = r_h / (k - (k - 1) r_h) for halves of k hands, and the matching prior
strength in hands, (1 - rho) / rho: after n hands the estimate moves n / (n + strength) of
the way from the prior to the observed rate.
"""
import gzip, json, math, re, sys, zipfile, collections, multiprocessing as mp
import xml.etree.ElementTree as ET

STATS = ('called', 'riichi', 'earlyMiddle', 'foldGenbutsu')


def kind(t):
    return t // 4


def middle(t):
    k = kind(t)
    return k < 27 and 2 <= k % 9 <= 6


def analyse_game(raw):
    try:
        root = ET.fromstring(gzip.decompress(raw))
    except Exception:
        try:
            root = ET.fromstring(raw)
        except Exception:
            return None
    # Per seat: one {stat: [successes, trials]} per hand.
    games = [[] for _ in range(4)]
    hand = None
    for el in root:
        tag = el.tag
        if tag == 'INIT':
            hand = {'disc': [[] for _ in range(4)], 'called': [False] * 4, 'riichi': [None] * 4, 'pending': [False] * 4, 'order': 0}
            continue
        if hand is None:
            continue
        if tag in ('AGARI', 'RYUUKYOKU'):
            for s in range(4):
                g = {st: [0, 0] for st in STATS}
                games[s].append(g)
                g['called'][0] += hand['called'][s]; g['called'][1] += 1
                g['riichi'][0] += hand['riichi'][s] is not None; g['riichi'][1] += 1
                early = [t for t, _ in hand['disc'][s][:6]]
                g['earlyMiddle'][0] += sum(map(middle, early)); g['earlyMiddle'][1] += len(early)
                for r in range(4):
                    at = hand['riichi'][r]
                    if r == s or at is None or (hand['riichi'][s] is not None and hand['riichi'][s] < at):
                        continue
                    for t, o in hand['disc'][s]:
                        if o <= at:
                            continue
                        safe = {kind(x) for x, _ in hand['disc'][r]} | {kind(x) for q in range(4) for x, oo in hand['disc'][q] if at < oo < o}
                        g['foldGenbutsu'][0] += kind(t) in safe; g['foldGenbutsu'][1] += 1
            hand = None
            continue
        if tag == 'REACH':
            if el.get('step') == '1':
                hand['pending'][int(el.get('who'))] = True
            continue
        if tag == 'N':
            m = int(el.get('m'))
            # Closed kan: bits 2-5 zero and not a chi/pon/added kan.
            closed_kan = not (m & 0x4) and not (m & 0x18) and not (m & 0x20) and (m & 0x3) == 0
            if not closed_kan:
                hand['called'][int(el.get('who'))] = True
            continue
        d = re.fullmatch(r'([DEFG])(\d+)', tag)
        if d:
            s = 'DEFG'.index(d.group(1))
            hand['order'] += 1
            hand['disc'][s].append((int(d.group(2)), hand['order']))
            if hand['pending'][s]:
                hand['riichi'][s] = hand['order']
                hand['pending'][s] = False
    return games


def pearson(xs, ys):
    n = len(xs)
    mx, my = sum(xs) / n, sum(ys) / n
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    sxx = sum((x - mx) ** 2 for x in xs); syy = sum((y - my) ** 2 for y in ys)
    return sxy / math.sqrt(sxx * syy) if sxx > 0 and syy > 0 else 0


def summarise(players):
    out = {}
    for stat in STATS:
        k = sum(h[stat][0] for p in players for h in p); n = sum(h[stat][1] for p in players for h in p)
        firsts, seconds, hands = [], [], []
        for p in players:
            # Alternate hands (odd/even) so both halves see the same mix of early and late rounds.
            a = [h[stat] for h in p[0::2]]; b = [h[stat] for h in p[1::2]]
            ta, tb = sum(t for _, t in a), sum(t for _, t in b)
            if ta and tb:
                firsts.append(sum(x for x, _ in a) / ta); seconds.append(sum(x for x, _ in b) / tb)
                hands.append(len(p) / 2)
        r = pearson(firsts, seconds) if len(firsts) > 2 else 0
        half = sum(hands) / len(hands) if hands else 0
        rho = r / (half - (half - 1) * r) if r > 0 and half > 0 else 0
        out[stat] = {'rate': round(k / n, 4) if n else 0, 'playerGames': len(firsts), 'handsPerHalf': round(half, 1),
                     'splitHalfR': round(r, 3), 'perHandReliability': round(rho, 4),
                     'priorStrengthHands': round((1 - rho) / rho, 1) if rho > 0 else None}
    return out


def main():
    path, out_path = sys.argv[1], sys.argv[2]
    max_games = int(sys.argv[3]) if len(sys.argv) > 3 else 10 ** 9
    workers = int(sys.argv[4]) if len(sys.argv) > 4 else max(1, mp.cpu_count() - 2)
    z = zipfile.ZipFile(path)
    blobs = [z.read(n) for n in z.namelist() if n.endswith('.mjlog')][:max_games]
    players = []
    with mp.Pool(workers) as pool:
        for r in pool.imap_unordered(analyse_game, blobs, chunksize=16):
            if r:
                players.extend(r)
    # Per-hand distributions for a type classifier: middle tiles among the first six discards
    # (hands with six), and the genbutsu share of a hand's discards after another's riichi.
    early = collections.Counter(h['earlyMiddle'][0] for p in players for h in p if h['earlyMiddle'][1] == 6)
    fold = collections.Counter(min(4, int(5 * h['foldGenbutsu'][0] / h['foldGenbutsu'][1])) for p in players for h in p if h['foldGenbutsu'][1] >= 3)
    hist = {'earlyMiddleOfSix': [round(early[i] / sum(early.values()), 4) for i in range(7)],
            'foldShareQuintile': [round(fold[i] / sum(fold.values()), 4) for i in range(5)]}
    doc = {'source': path, 'playerGames': len(players), 'stats': summarise(players), 'perHand': hist}
    json.dump(doc, open(out_path, 'w', encoding='utf-8'), indent=1)
    print(json.dumps(doc, indent=1))


if __name__ == '__main__':
    main()
