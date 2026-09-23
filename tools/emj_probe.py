"""Read-only Cartographer memory/code capture. Addresses are relocated each session."""
import argparse
import json
from pathlib import Path
import struct
import requests
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

ROOT = Path(__file__).resolve().parents[1] / 'artifacts' / 'emj-audit-20260923'
ROOT.mkdir(parents=True, exist_ok=True)

def get(path):
    response = requests.get('http://127.0.0.1:9790' + path, timeout=15)
    response.raise_for_status()
    return response.json()

def save(name, value):
    (ROOT / (name + '.json')).write_text(json.dumps(value, indent=2), encoding='utf-8')

def memory(address, size):
    result = get(f'/mem?addr={address:#x}&size={size}')
    return bytes.fromhex(result['hex'])

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--code', help='static image address (0x140000000 base)')
    parser.add_argument('--size', type=lambda s: int(s, 0), default=1024)
    parser.add_argument('--snapshot', action='store_true')
    args = parser.parse_args()
    symbols = get('/symbols')
    base = int(symbols['moduleBase'], 16)
    if args.code:
        ea = int(args.code, 16)
        address = base + ea - 0x140000000
        data = memory(address, args.size)
        (ROOT / f'code-{ea:x}.bin').write_bytes(data)
        lines = [f'{i.address:x}  {i.mnemonic:8} {i.op_str}' for i in Cs(CS_ARCH_X86, CS_MODE_64).disasm(data, ea)]
        (ROOT / f'code-{ea:x}.txt').write_text('\n'.join(lines), encoding='utf-8')
        print('\n'.join(lines))
    if args.snapshot:
        save('symbols-current', symbols)
        addon = get('/addon?name=Emj')
        save('addon-current', addon)
        info = addon.get('info')
        if info:
            for name, addr, size in [('addon', info['Address'], 0x6000), ('agent', info['Agent'], 0x1000)]:
                save(name + 'mem-current', get(f'/mem?addr={addr}&size={size}'))
            classes = get('/classes?filter=Emj')
            for c in classes:
                if c['Name'] in ('Client::UI::AddonEmj', 'Client::UI::Agent::AgentEmj'):
                    addr = int(c['vtables'][0]['runtime'], 16)
                    data = memory(addr, 80 * 8)
                    print(c['Name'])
                    for i, (p,) in enumerate(struct.iter_unpack('<Q', data)):
                        print(i, hex(p - base + 0x140000000))
            print('base flags', memory(int(info['Address'], 16) + 0x198, 24).hex())

if __name__ == '__main__':
    main()
