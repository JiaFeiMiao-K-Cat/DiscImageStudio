"""Assemble the final DVD-R DL image.

  LBA 0..        ISO9660 + Joliet metadata, then L0 ring files (folder 'L0_内圈' + extraction tools)
  ..243232       unused ring sectors keep the grayscale pattern (+10% random)
  243232..L0     grayscale pattern; 10% slots carry the hidden zip (see hidecodec.py)
  L0..TOTAL      L1 files (folder 'L1'), rest zero

Usage: build_final.py tone.npy out.iso [--l1 DIR] [--ring DIR] [--hidden DIR]"""
import sys, os, io, struct, hashlib, zipfile, argparse, datetime, unicodedata, re
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from hidecodec import (S, L0_END, TOTAL, PATTERN_START, MAGIC, keys_for, slot_mask, whitening)

TONE_START = 32
PALETTE = np.array([0x60, 0x2D, 0xD0, 0xC1, 0x74, 0xC3, 0x61], np.uint8)  # light -> dark, all low-DSV
HERE = os.path.dirname(os.path.abspath(__file__))

ap = argparse.ArgumentParser()
ap.add_argument('tone'); ap.add_argument('out')
ap.add_argument('--l1'); ap.add_argument('--ring'); ap.add_argument('--hidden')
ap.add_argument('--l1-root', action='store_true', help='put the L1 folder contents at the disc root')
ap.add_argument('--autoplay', help='folder (relative to the L1 root) whose audio files get an autorun playlist')
ap.add_argument('--label', default='NEKO_DVD')
a = ap.parse_args()

# ---------------- tree ----------------
class Node:
    def __init__(self, name, path=None, size=0, data=None):
        self.name, self.path, self.size, self.data = name, path, size, data
        self.children = None; self.lba = 0; self.iso = self.jol = None
    @property
    def isdir(self): return self.children is not None

def mkdir(name):
    n = Node(name); n.children = []; return n

def scan(path, name):
    d = mkdir(name)
    for e in sorted(os.scandir(path), key=lambda e: e.name):
        if e.is_dir(): d.children.append(scan(e.path, e.name))
        elif e.is_file():
            sz = e.stat().st_size
            if sz >= 1 << 32: sys.exit(f'file too large for ISO9660 (>4GB): {e.path}')
            d.children.append(Node(e.name, e.path, sz))
    return d

root = mkdir('')
tools = mkdir('隐藏数据提取')   # small generated files placed right after the metadata
if a.hidden:
    for fn in ('extract_hidden.py', 'hidecodec.py'):
        p = os.path.join(HERE, fn); tools.children.append(Node(fn, p, os.path.getsize(p)))
    readme = ('本盘 L0 图案区的 10% 字节里藏有一个 zip 压缩包。\r\n'
              '提取方法（需要 Python 3 和 numpy）：\r\n'
              '  python extract_hidden.py \\\\.\\G: hidden.zip\r\n'
              '其中 G: 换成光驱盘符；也可以传入本盘的 ISO 镜像路径。\r\n').encode('utf-8-sig')
    tools.children.append(Node('说明.txt', None, len(readme), readme))
    root.children.append(tools)
ring = scan(a.ring, 'L0_内圈') if a.ring else None
l1 = scan(a.l1, 'L1') if a.l1 else None
if ring: root.children.append(ring)
if l1:
    if a.l1_root: root.children.extend(l1.children)
    else: root.children.append(l1)

if a.autoplay:
    AUDIO = ('.flac', '.mp3', '.m4a', '.wav', '.ogg', '.wma', '.aac')
    tracks = sorted(f for f in os.listdir(os.path.join(a.l1, a.autoplay)) if f.lower().endswith(AUDIO))
    if not tracks: sys.exit('no audio files for autoplay')
    rel = a.autoplay.replace('/', '\\')
    m3u = ('#EXTM3U\r\n' + ''.join(f'{rel}\\{t}\r\n' for t in tracks)).encode('utf-8-sig')
    inf = ('[autorun]\r\nshellexecute=播放音乐.m3u8\r\naction=播放 Music 里的音乐\r\n'
           f'label={a.label}\r\n').encode('utf-16')
    for nm, data in (('播放音乐.m3u8', m3u), ('autorun.inf', inf)):
        tools.children.append(Node(nm, None, len(data), data)); root.children.append(tools.children[-1])

def walk(d, depth=0):
    yield d, depth
    for c in d.children:
        if c.isdir: yield from walk(c, depth + 1)

# ---------------- names ----------------
def iso_name(name, isdir, used):
    base = unicodedata.normalize('NFKD', name).encode('ascii', 'ignore').decode().upper()
    if not isdir and '.' in base:
        stem, ext = base.rsplit('.', 1)
    else:
        stem, ext = base.replace('.', '_'), ''
    stem = re.sub('[^A-Z0-9_]', '_', stem)[:24] or 'F'; ext = re.sub('[^A-Z0-9_]', '_', ext)[:4]
    cand, k = stem, 1
    while True:
        full = cand + ('' if isdir else '.' + ext + ';1')
        if full not in used: used.add(full); return full
        k += 1; cand = f'{stem[:20]}_{k}'

for d, _ in walk(root):
    used = set(); jused = set()
    for c in d.children:
        c.iso = iso_name(c.name, c.isdir, used)
        jn, k = c.name[:64], 1
        while jn.lower() in jused:
            k += 1; stem, dot, ext = c.name.rpartition('.')
            tail = f'~{k}' + (dot + ext if dot and len(ext) <= 8 else '')
            jn = (stem if dot else c.name)[:64 - len(tail)] + tail
        jused.add(jn.lower())
        c.jol = jn + ('' if c.isdir else ';1')

# ---------------- records ----------------
now = datetime.datetime.now()
REC_DATE = bytes([now.year - 1900, now.month, now.day, now.hour, now.minute, now.second, 0])
VOL_DATE = now.strftime('%Y%m%d%H%M%S00').encode() + b'\x00'
def b16(v): return struct.pack('<H', v) + struct.pack('>H', v)
def b32(v): return struct.pack('<I', v) + struct.pack('>I', v)
def rec(extent, size, ident, isdir):
    l = 33 + len(ident) + (1 - len(ident) % 2)
    r = bytes([l, 0]) + b32(extent) + b32(size) + REC_DATE + bytes([2 if isdir else 0, 0, 0]) + b16(1) + bytes([len(ident)]) + ident
    return r.ljust(l, b'\x00')

def ident(c, joliet):
    return c.jol.encode('utf-16-be') if joliet else c.iso.encode('ascii')

def dir_records(d, parent, joliet, ext_of):
    recs = [rec(ext_of(d), d.dsize[joliet], b'\x00', True), rec(ext_of(parent), parent.dsize[joliet], b'\x01', True)]
    for c in sorted(d.children, key=lambda c: ident(c, joliet)):
        recs.append(rec(ext_of(c) if c.isdir else c.lba, c.dsize[joliet] if c.isdir else c.size, ident(c, joliet), c.isdir))
    out = bytearray()
    for r in recs:
        if len(out) % S + len(r) > S: out += b'\x00' * (S - len(out) % S)
        out += r
    return bytes(out).ljust(-(-len(out) // S) * S, b'\x00')

# directory sizes (independent of extents)
parents = {}
for d, _ in walk(root):
    for c in d.children:
        if c.isdir: parents[id(c)] = d
parents[id(root)] = root
for d, _ in walk(root): d.dsize = {False: S, True: S}
for _ in range(2):
    for d, _ in walk(root):
        for j in (False, True):
            d.dsize[j] = len(dir_records(d, parents[id(d)], j, lambda n: 0))

# path tables
dirs = [d for d, _ in sorted(walk(root), key=lambda t: t[1])]
order = []
for lvl in range(max(t[1] for t in walk(root)) + 1):
    for d, dep in walk(root):
        if dep == lvl: order.append(d)
num = {id(d): i + 1 for i, d in enumerate(order)}
def path_table(joliet, big, ext_of):
    out = bytearray()
    for d in order:
        nm = b'\x00' if d is root else ident(d, joliet)
        e = struct.pack('>I' if big else '<I', ext_of(d)); p = struct.pack('>H' if big else '<H', num[id(parents[id(d)])])
        out += bytes([len(nm), 0]) + e + p + nm + (b'\x00' if len(nm) % 2 else b'')
    return bytes(out)
pt_size = {j: len(path_table(j, False, lambda n: 0)) for j in (False, True)}
pt_sec = {j: -(-pt_size[j] // S) for j in (False, True)}

# ---------------- allocation ----------------
lba = 19
pt_lba = {}
for j in (False, True):
    pt_lba[j] = (lba, lba + pt_sec[j]); lba += 2 * pt_sec[j]
for d in order:
    d.ext = {}
for j in (False, True):
    for d in order:
        d.ext[j] = lba; lba += d.dsize[j] // S
meta_end = lba

def place(d, cur, limit, where):
    for c in d.children:
        if c.isdir: cur = place(c, cur, limit, where)
        else:
            c.lba = cur if c.size else 0
            cur += -(-c.size // S)
            if cur > limit: sys.exit(f'{where} full: needs more than {limit} sectors')
    return cur
ring_end = place(tools, meta_end, PATTERN_START, 'L0 ring')
if ring: ring_end = place(ring, ring_end, PATTERN_START, 'L0 ring')
l1_end = place(l1, L0_END, TOTAL, 'L1') if l1 else L0_END

# ---------------- hidden payload ----------------
payload = b''
if a.hidden:
    bio = io.BytesIO()
    with zipfile.ZipFile(bio, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for base, _, files in os.walk(a.hidden):
            for fn in sorted(files):
                p = os.path.join(base, fn); z.write(p, os.path.relpath(p, a.hidden))
    payload = bio.getvalue()
blob = MAGIC + len(payload).to_bytes(8, 'little') + hashlib.sha256(payload).digest() + payload
cap = int(sum(slot_mask(x, min(4096, L0_END - x)).sum() for x in range(PATTERN_START, L0_END, 4096)))
if len(blob) > cap: sys.exit(f'hidden data too large: {len(blob)} > {cap} bytes')

# ---------------- write ----------------
tone = np.load(a.tone, mmap_mode='r')
def desc(joliet):
    t = 2 if joliet else 1
    def txt(s, n):
        return s.encode('utf-16-be').ljust(n, b'\x00 '[0:1]) if joliet else s.encode().ljust(n, b' ')
    vid = 'NEKO_DVD'
    d = (bytes([t]) + b'CD001\x01\x00' + txt('', 32) + (vid.encode('utf-16-be').ljust(32, b'\x00') if joliet else vid.encode().ljust(32, b' '))
         + b'\x00' * 8 + b32(TOTAL) + ((b'%/E' + b'\x00' * 29) if joliet else b'\x00' * 32)
         + b16(1) + b16(1) + b16(S) + b32(pt_size[joliet])
         + struct.pack('<I', pt_lba[joliet][0]) + b'\x00' * 4 + struct.pack('>I', pt_lba[joliet][1]) + b'\x00' * 4
         + rec(root.ext[joliet], root.dsize[joliet], b'\x00', True)
         + b' ' * 128 * 3 + (b'\x00 ' * 64 if joliet else b' ' * 128)
         + b' ' * 37 * 3 + VOL_DATE * 2 + b'0' * 16 + b'\x00' + b'0' * 16 + b'\x00' + b'\x01\x00')
    return d.ljust(S, b'\x00')

files_in = lambda d: [c for x, _ in walk(d) for c in x.children if not c.isdir and c.size]
rng = np.random.default_rng(7)

def pattern(lba0, n, hidden_pos):
    """Tone sectors for LBA lba0..+n in user domain; hidden_pos = running index into blob slots or None."""
    lv = np.asarray(tone[(lba0 - TONE_START) * S:(lba0 - TONE_START + n) * S]).reshape(n, S)
    s = PALETTE[np.minimum(lv, len(PALETTE) - 1)]
    if hidden_pos is None:
        m = rng.random((n, S)) < 0.10
        s[m] = rng.integers(0, 256, int(m.sum()), dtype=np.uint8)
    else:
        m = slot_mask(lba0, n); k = int(m.sum())
        chunk = np.frombuffer(blob[hidden_pos:hidden_pos + k], np.uint8)
        data = np.zeros(k, np.uint8); data[:len(chunk)] = chunk
        s[m] = data ^ whitening(hidden_pos, k)
        hidden_pos += k
    return (s ^ keys_for(lba0, n)).tobytes(), hidden_pos

with open(a.out, 'xb') as g:
    g.write(b'\x00' * 16 * S + desc(False) + desc(True) + (b'\xffCD001\x01').ljust(S, b'\x00'))
    for j in (False, True):
        g.write(path_table(j, False, lambda n: n.ext[j]).ljust(pt_sec[j] * S, b'\x00'))
        g.write(path_table(j, True, lambda n: n.ext[j]).ljust(pt_sec[j] * S, b'\x00'))
    for j in (False, True):
        for d in order:
            g.write(dir_records(d, parents[id(d)], j, lambda n: n.ext[j]))
    assert g.tell() == meta_end * S

    def put_files(nodes):
        for c in sorted(nodes, key=lambda c: c.lba):
            assert g.tell() == c.lba * S, (c.name, g.tell() // S, c.lba)
            if c.data is not None: g.write(c.data)
            else:
                with open(c.path, 'rb') as f:
                    while True:
                        b = f.read(8 << 20)
                        if not b: break
                        g.write(b)
            g.write(b'\x00' * (-c.size % S))
    put_files(files_in(tools) + (files_in(ring) if ring else []))
    assert g.tell() == ring_end * S
    x = ring_end
    while x < PATTERN_START:   # leftover ring area keeps the picture
        n = min(4096, PATTERN_START - x)
        if x < TONE_START:
            g.write(b'\x00' * n * S)  # cannot happen in practice (metadata > 32 sectors is not required)
        else:
            g.write(pattern(x, n, None)[0])
        x += n
    hp = 0
    while x < L0_END:
        n = min(4096, L0_END - x)
        data, hp = pattern(x, n, hp); g.write(data); x += n
        if (x - PATTERN_START) % (4096 * 64) < 4096: print(f'L0 {x}/{L0_END}', flush=True)
    if l1: put_files(files_in(l1))
    assert g.tell() == l1_end * S
    left = TOTAL - l1_end; z = b'\x00' * 4096 * S
    while left:
        n = min(4096, left); g.write(z[:n * S]); left -= n
    assert g.tell() == TOTAL * S

print(f'done: meta_end={meta_end} ring_end={ring_end} (pattern kept from LBA {ring_end}) '
      f'l1_end={l1_end} hidden={len(payload)} B of {cap - 48} B capacity')
