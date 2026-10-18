"""Conditional deal-in rates vs riichi / dama / open hands from Tenhou mjlogs.

Every hand is visible in a Tenhou log, so for each discard we know which other seats were
tenpai, their waits, furiten, whether a ron on that tile has a yaku, and what it would pay.
Counts are keyed by threat kind (riichi, dama, open read: flush / toitoi / yakuhai / tanyao /
other) x tile class, plus tenpai rates by calls x phase x tsumogiri streak / late tedashi.
tools/make_open_danger.py merges per-archive outputs into resources/policy/open_danger.json.

Needs the MIT `mahjong` package for shanten and scoring (pip install mahjong==1.2.1); run it
from its own venv:  python -I tools/open_hand_danger.py <mjlog zip> <out.json> [max_games] [workers]
"""
import gzip, json, re, sys, zipfile, collections, multiprocessing as mp
import xml.etree.ElementTree as ET

from mahjong.shanten import Shanten
from mahjong.hand_calculating.hand import HandCalculator
from mahjong.hand_calculating.hand_config import HandConfig, OptionalRules
from mahjong.meld import Meld as LMeld
from mahjong.constants import EAST, SOUTH, WEST, NORTH

SH = Shanten()
CALC = HandCalculator()
WINDS = [EAST, SOUTH, WEST, NORTH]
RULES = OptionalRules(has_open_tanyao=True, has_aka_dora=True)


def kind(t):
    return t // 4


def decode_meld(m):
    """Return (type, tiles136, called_tile136 or None)."""
    if m & 0x4:
        t = (m & 0xFC00) >> 10
        r = t % 3
        t //= 3
        t = t // 7 * 9 + t % 7
        t *= 4
        h = [t + ((m & 0x0018) >> 3), t + 4 + ((m & 0x0060) >> 5), t + 8 + ((m & 0x0180) >> 7)]
        return 'chi', h, h[r]
    if m & 0x8:
        unused = (m & 0x0060) >> 5
        t = (m & 0xFE00) >> 9
        r = t % 3
        t = t // 3 * 4
        h = [t, t + 1, t + 2, t + 3]
        h.remove(t + unused)
        return 'pon', h, h[r]
    if m & 0x10:
        added = (m & 0x0060) >> 5
        t = (m & 0xFE00) >> 9
        t = t // 3 * 4
        return 'chakan', [t, t + 1, t + 2, t + 3], t + added
    if m & 0x20:
        return 'nuki', [], None
    hai0 = (m & 0xFF00) >> 8
    t = hai0 // 4 * 4
    return ('ankan' if (m & 3) == 0 else 'minkan'), [t, t + 1, t + 2, t + 3], (None if (m & 3) == 0 else hai0)


def to34(tiles):
    a = [0] * 34
    for t in tiles:
        a[kind(t)] += 1
    return a


class Seat:
    def __init__(self, tiles):
        self.hand = list(tiles)
        self.melds = []           # (type, tiles136)
        self.discards = []        # (tile136, tedashi bool, global order)
        self.riichi_order = -1    # global discard order of riichi tile
        self.riichi_pending = False
        self.last_draw = None
        self.version = 0
        self.furiten_riichi = False
        self.wait_cache = (-1, None)

    @property
    def open(self):
        return any(t != 'ankan' for t, _ in self.melds)


def waits_of(seat):
    c = to34(seat.hand)
    if sum(c) % 3 != 1:
        return None
    try:
        if SH.calculate_shanten(c, use_chiitoitsu=not seat.melds, use_kokushi=not seat.melds) != 0:
            return []
    except Exception:
        return []
    allc = to34(seat.hand + [t for _, ts in seat.melds for t in ts])
    w = []
    for k in range(34):
        if allc[k] >= 4:
            continue
        c[k] += 1
        if SH.calculate_shanten(c, use_chiitoitsu=not seat.melds, use_kokushi=not seat.melds) == -1:
            w.append(k)
        c[k] -= 1
    return w


def ron_value(seat, tile136, seat_wind, round_wind, doras, riichi):
    tiles = seat.hand + [tile136] + [t for _, ts in seat.melds for t in ts]
    melds = []
    for typ, ts in seat.melds:
        mt = {'chi': LMeld.CHI, 'pon': LMeld.PON, 'chakan': LMeld.SHOUMINKAN, 'minkan': LMeld.KAN, 'ankan': LMeld.KAN}[typ]
        melds.append(LMeld(meld_type=mt, tiles=ts, opened=typ != 'ankan'))
    cfg = HandConfig(is_riichi=riichi, is_tsumo=False, player_wind=WINDS[seat_wind], round_wind=WINDS[round_wind], options=RULES)
    try:
        r = CALC.estimate_hand_value(tiles, tile136, melds=melds, dora_indicators=doras, config=cfg)
    except Exception:
        return None
    if r.error is not None:
        return 0
    return r.cost['main']  # ron: the main payment is the whole bill


DRAW = {'T': 0, 'U': 1, 'V': 2, 'W': 3}
DISC = {'D': 0, 'E': 1, 'F': 2, 'G': 3}


def yakuhai_kinds(seat_wind, round_wind):
    return {31, 32, 33, 27 + seat_wind, 27 + round_wind}


def read_type(seat, seat_wind, round_wind):
    melds = [(t, ts) for t, ts in seat.melds if t != 'ankan']
    if not melds:
        return 'closed'
    kinds = [kind(ts[0]) for _, ts in melds]
    num_suits = {k // 9 for k in kinds if k < 27}
    yh = yakuhai_kinds(seat_wind, round_wind)
    has_yakuhai = any(t != 'chi' and kind(ts[0]) in yh for t, ts in melds)
    # discard evidence for a flush: number discards mostly outside one suit
    nd = [kind(t) for t, _, _ in seat.discards if kind(t) < 27]
    tags = []
    if len(num_suits) <= 1:
        suit = next(iter(num_suits)) if num_suits else None
        if suit is None:
            # honor melds only: a flush if the number discards avoid one suit
            if len(nd) >= 4:
                counts = [sum(1 for k in nd if k // 9 == s) for s in range(3)]
                s = min(range(3), key=lambda i: counts[i])
                if counts[s] <= len(nd) * 0.15:
                    suit = s
        if suit is not None:
            in_suit = sum(1 for k in nd if k // 9 == suit)
            if len(melds) >= 2 or (len(nd) >= 4 and in_suit <= len(nd) * 0.2):
                tags.append(('flush', suit))
    if len(melds) >= 2 and all(t != 'chi' for t, _ in melds):
        tags.append(('toitoi', None))
    if has_yakuhai:
        tags.append(('yakuhai', None))
    if all(all(not (kind(x) >= 27 or kind(x) % 9 in (0, 8)) for x in ts) for _, ts in melds):
        tags.append(('tanyao', None))
    return tags or [('other', None)]


def tile_class(k, seat, visible, read):
    """Class of tile kind k against seat for the given primary read."""
    if read and read[0][0] == 'flush':
        suit = read[0][1]
        if k >= 27:
            return 'flush:honor'
        return 'flush:in-suit' if k // 9 == suit else 'flush:off-suit'
    if k >= 27:
        return f'honor:vis{min(visible[k], 3)}'
    n = k % 9 + 1
    disc = {kind(t) for t, _, _ in seat.discards}
    base = k - (n - 1)
    if n <= 3:
        suji = (base + n + 2) in disc
    elif n >= 7:
        suji = (base + n - 4) in disc
    else:
        suji = (base + n - 4) in disc and (base + n + 2) in disc
    rank = '19' if n in (1, 9) else '28' if n in (2, 8) else '37' if n in (3, 7) else '456'
    return f"{'suji' if suji else 'nonsuji'}:{rank}"


def bucket_turn(n):
    return 'early' if n <= 6 else 'mid' if n <= 11 else 'late'


def analyse_game(raw):
    out = collections.defaultdict(lambda: [0, 0, 0.0])   # key -> [n, hits, sum value of hits]
    tenpai = collections.defaultdict(lambda: [0, 0])      # key -> [n, tenpai]
    yakuman = collections.Counter()
    try:
        root = ET.fromstring(gzip.decompress(raw))
    except Exception:
        try:
            root = ET.fromstring(raw)
        except Exception:
            return None
    seats = None
    order = 0
    oya = 0
    round_wind = 0
    doras = []
    for el in root:
        tag = el.tag
        if tag == 'INIT':
            seed = [int(x) for x in el.get('seed').split(',')]
            round_wind = seed[0] // 4
            doras = [seed[5]]
            oya = int(el.get('oya'))
            seats = [Seat([int(x) for x in el.get(f'hai{i}').split(',')]) for i in range(4)]
            order = 0
            continue
        if seats is None:
            continue
        if tag == 'DORA':
            doras.append(int(el.get('hai')))
            continue
        if tag == 'REACH':
            who = int(el.get('who'))
            if el.get('step') == '1':
                seats[who].riichi_pending = True
            continue
        if tag == 'N':
            who = int(el.get('who'))
            typ, ts, called = decode_meld(int(el.get('m')))
            s = seats[who]
            if typ == 'chakan':
                for i, (t, mts) in enumerate(s.melds):
                    if t == 'pon' and kind(mts[0]) == kind(ts[0]):
                        s.melds[i] = ('chakan', ts)
                for t in ts:
                    if t in s.hand:
                        s.hand.remove(t)
            elif typ in ('chi', 'pon', 'minkan', 'ankan'):
                for t in ts:
                    if t in s.hand:
                        s.hand.remove(t)
                s.melds.append((typ, ts))
            s.version += 1
            continue
        if tag == 'AGARI':
            who, frm = int(el.get('who')), int(el.get('fromWho'))
            yk = el.get('yakuman')
            if yk:
                for y in yk.split(','):
                    yakuman[(int(y), seats[who].open)] += 1
            continue
        m = re.fullmatch(r'([TUVWDEFG])(\d+)', tag)
        if not m:
            continue
        letter, tile = m.group(1), int(m.group(2))
        if letter in DRAW:
            s = seats[DRAW[letter]]
            s.hand.append(tile)
            s.last_draw = tile
            continue
        d = DISC[letter]
        s = seats[d]
        if tile in s.hand:
            s.hand.remove(tile)
        tedashi = tile != s.last_draw
        s.last_draw = None
        order += 1
        s.discards.append((tile, tedashi, order))
        if s.riichi_pending:
            s.riichi_order = order
            s.riichi_pending = False
        s.version += 1
        k = kind(tile)
        # visible copies (public): all discards + all meld tiles + dora indicators
        visible = [0] * 34
        for x in seats:
            for t, _, _ in x.discards:
                visible[kind(t)] += 1
            for _, ts in x.melds:
                for t in ts:
                    visible[kind(t)] += 1
        for t in doras:
            visible[kind(t)] += 1
        for o in range(4):
            if o == d:
                continue
            os_ = seats[o]
            seat_wind = (o - oya) % 4
            riichi = os_.riichi_order >= 0
            closed = not os_.open
            ndisc = len(os_.discards)
            if os_.wait_cache[0] != os_.version:
                os_.wait_cache = (os_.version, waits_of(os_))
            w = os_.wait_cache[1]
            if w is None:
                continue
            is_tenpai = len(w) > 0
            # ---- tenpai-rate features
            if riichi:
                kind_s = 'riichi'
            elif closed:
                kind_s = 'dama'
            else:
                kind_s = f'open{min(len([1 for t, _ in os_.melds if t != "ankan"]), 4)}'
            tb = bucket_turn(ndisc)
            if not riichi:
                tsumogiri_streak = 0
                for t, td, _ in reversed(os_.discards):
                    if td:
                        break
                    tsumogiri_streak += 1
                late_mid_tedashi = sum(1 for t, td, _ in os_.discards[-2:] if td and kind(t) < 27 and 2 <= kind(t) % 9 <= 6)
                tenpai[(kind_s, tb)][0] += 1
                tenpai[(kind_s, tb)][1] += is_tenpai
                tenpai[(kind_s, tb, f'tsg{min(tsumogiri_streak, 3)}')][0] += 1
                tenpai[(kind_s, tb, f'tsg{min(tsumogiri_streak, 3)}')][1] += is_tenpai
                tenpai[(kind_s, tb, f'midtd{late_mid_tedashi}')][0] += 1
                tenpai[(kind_s, tb, f'midtd{late_mid_tedashi}')][1] += is_tenpai
                if not closed:
                    for rt, _ in read_type(os_, seat_wind, round_wind):
                        tenpai[(kind_s, tb, 'read:' + rt)][0] += 1
                        tenpai[(kind_s, tb, 'read:' + rt)][1] += is_tenpai
                    dragons = sum(1 for t, ts in os_.melds if t != 'chi' and kind(ts[0]) >= 31)
                    winds = sum(1 for t, ts in os_.melds if t != 'chi' and 27 <= kind(ts[0]) <= 30)
                    if dragons >= 2:
                        tenpai[('two-dragon-melds', tb)][0] += 1
                        tenpai[('two-dragon-melds', tb)][1] += is_tenpai
                    if winds >= 3:
                        tenpai[('three-wind-melds', tb)][0] += 1
                        tenpai[('three-wind-melds', tb)][1] += is_tenpai
            if not is_tenpai:
                continue
            # ---- conditional danger given tenpai (only for this discard tile)
            own = {kind(t) for t, _, _ in os_.discards}
            furiten = any(x in own for x in w) or os_.furiten_riichi
            hit = 0
            value = 0
            if k in w and not furiten:
                v = ron_value(os_, tile, seat_wind, round_wind, doras, riichi)
                if v:
                    hit, value = 1, v
            if riichi and k in w and not hit:
                os_.furiten_riichi = True
            if riichi:
                ctx = 'riichi'
                read = None
            elif closed:
                ctx = 'dama'
                read = None
            else:
                read = read_type(os_, seat_wind, round_wind)
                ctx = 'open:' + read[0][0]
            safe_after = os_.riichi_order >= 0 and any(kind(t) == k for x in seats for t, _, od in x.discards if od > os_.riichi_order and not (x is s and od == order))
            if k in own or safe_after:
                cls = 'genbutsu'
            else:
                cls = tile_class(k, os_, visible, read if ctx.startswith('open:flush') else None)
            for key in [(ctx, cls), (ctx, 'ALL'), (ctx, bucket_turn(ndisc), cls)]:
                out[key][0] += 1
                out[key][1] += hit
                out[key][2] += value
            if not riichi and not closed:
                dragons = sum(1 for t, ts in os_.melds if t != 'chi' and kind(ts[0]) >= 31)
                if dragons >= 2 and k >= 31:
                    key = ('two-dragon-melds', 'dragon' if not any(kind(ts[0]) == k for _, ts in os_.melds) else 'melded')
                    out[key][0] += 1
                    out[key][1] += hit
                    out[key][2] += value
                # value of open hands when they hit, by number of visible dora in melds
                dkinds = set()
                for t in doras:
                    q = kind(t)
                    dkinds.add((q // 9 * 9 + (q % 9 + 1) % 9) if q < 27 else (27 + (q - 27 + 1) % 4 if q < 31 else 31 + (q - 31 + 1) % 3))
                vis_dora = sum(1 for _, ts in os_.melds for t in ts if kind(t) in dkinds or t in (16, 52, 88))
                key = ('open-dora-visible', min(vis_dora, 3))
                out[key][0] += 1
                out[key][1] += hit
                out[key][2] += value
    return {'danger': {json.dumps(k): v for k, v in out.items()},
            'tenpai': {json.dumps(k): v for k, v in tenpai.items()},
            'yakuman': {json.dumps(k): v for k, v in yakuman.items()}}


def main():
    path, out_path = sys.argv[1], sys.argv[2]
    max_games = int(sys.argv[3]) if len(sys.argv) > 3 else 10 ** 9
    workers = int(sys.argv[4]) if len(sys.argv) > 4 else max(1, mp.cpu_count() - 2)
    z = zipfile.ZipFile(path)
    names = [n for n in z.namelist() if n.endswith('.mjlog')][:max_games]
    blobs = [z.read(n) for n in names]
    total = {'danger': collections.defaultdict(lambda: [0, 0, 0.0]), 'tenpai': collections.defaultdict(lambda: [0, 0]), 'yakuman': collections.Counter()}
    games = 0
    with mp.Pool(workers) as pool:
        for r in pool.imap_unordered(analyse_game, blobs, chunksize=8):
            if r is None:
                continue
            games += 1
            for k, v in r['danger'].items():
                t = total['danger'][k]
                for i in range(3):
                    t[i] += v[i]
            for k, v in r['tenpai'].items():
                t = total['tenpai'][k]
                t[0] += v[0]
                t[1] += v[1]
            for k, v in r['yakuman'].items():
                total['yakuman'][k] += v
    json.dump({'games': games, **{k: dict(v) for k, v in total.items()}}, open(out_path, 'w'), indent=0)
    print('games', games)


if __name__ == '__main__':
    main()
