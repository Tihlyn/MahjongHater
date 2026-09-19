"""Dev harness: drives a live match through the plugin's debug API.

Polls /state and executes each fresh policy decision once via /act (discard, call, pass,
win); advances the round recap with the Next button. Requires `/mhater debug` (or a dev
build, which auto-starts the API) and PluginEnabled. It is a test driver for the policy,
not a feature of the plugin — there is deliberately no auto-play inside the plugin.

    python tools/play_loop.py [logfile] [max_minutes]
"""
import urllib.request, urllib.parse, json, time, sys, datetime, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

API = "http://127.0.0.1:9787"
LOG = sys.argv[1] if len(sys.argv) > 1 else "play.log"
MAX_MINUTES = float(sys.argv[2]) if len(sys.argv) > 2 else 60
ACTIONS = {"Discard", "Riichi", "Pon", "Chi", "MinKan", "AnKan", "ShouMinKan", "Tsumo", "Ron"}


def get(path):
    with urllib.request.urlopen(API + path, timeout=8) as r:
        return json.loads(r.read().decode("utf-8", "replace"))


def log(msg):
    line = f"{datetime.datetime.now():%H:%M:%S} {msg}"
    print(line, flush=True)
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def main():
    start = time.time()
    acted = {}          # sequence -> (action, tile)
    last_seq = None
    seq_since = time.time()
    last_next = 0
    stalls = 0
    while time.time() - start < MAX_MINUTES * 60:
        try:
            st = get("/state")
        except Exception as e:
            log(f"api error: {e}")
            time.sleep(2)
            continue
        s, p, r = st["status"], st["prompt"], st["reco"]
        if not s.get("emjOpen"):
            log("Emj closed — stopping")
            break
        seq = s.get("sequence")
        phase = s.get("phase")
        if seq is not None and last_seq is not None and seq < last_seq:
            acted.clear()          # plugin reloaded: sequence numbers restarted
            log("sequence restarted (plugin reload) - forgetting acted set")
        if seq != last_seq:
            last_seq, seq_since = seq, time.time()
            hand = " ".join(s.get("hand") or [])
            log(f"seq={seq} phase={phase} code={s.get('stateCode')} legal={s.get('legal')} wall={s.get('wall')} "
                f"hand=[{hand}] drawn={s.get('drawn')} call={s.get('callTile')} opts={s.get('callOptions')} "
                f"melds={s.get('melds')} notes={s.get('notes')}")

        # Recap screen: advance with the Next button (node 97 = ButtonClick param 7).
        if phase == "RoundEnd" or (s.get("stateCode") in (27, 29, 32) and phase not in ("OurTurn", "CallPrompt")):
            if time.time() - last_next > 4:
                last_next = time.time()
                try:
                    out = get("/click?node=97&param=7")
                    log(f"round end -> Next: {json.dumps(out)[:160]}")
                except Exception as e:
                    log(f"Next failed: {e}")
            time.sleep(1.5)
            continue

        if r.get("status") != "Ready" or not r.get("fresh"):
            time.sleep(0.4)
            continue

        action = r.get("action")
        actionable = action in ACTIONS or (action == "Pass" and phase in ("CallPrompt", "SelfDeclare"))
        if not actionable:
            time.sleep(0.4)
            continue
        if seq in acted:
            # Same snapshot, already acted: the game hasn't advanced. Retry once after 6 s.
            if time.time() - seq_since > 6 and acted[seq][2] < 2:
                acted[seq] = (action, r.get("tile"), acted[seq][2] + 1)
                stalls += 1
                log(f"seq={seq} still here after act - retrying {action} {r.get('tile')}")
            else:
                time.sleep(0.5)
                continue
        else:
            acted[seq] = (action, r.get("tile"), 1)

        log(f"DECISION {action} {r.get('tile') or ''} | {r.get('summary')} | shanten={r.get('shanten')} ukeire={r.get('ukeire')} "
            f"| top={[(c['tile'], c['shanten'], c['ukeire'], c['risk']) for c in (r.get('ranked') or [])[:3]]}")
        for step in r.get("steps") or []:
            log(f"    {step}")
        try:
            out = get("/act")
            log(f"ACT -> {json.dumps(out)[:300]}")
        except Exception as e:
            log(f"act failed: {e}")
        time.sleep(1.2)
    log(f"done: {len(acted)} decisions acted, {stalls} retries")


if __name__ == "__main__":
    main()
