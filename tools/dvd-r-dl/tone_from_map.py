"""Build tone.npy from the line art using a timing map table WITHOUT re-smoothing it, plus a phase-origin
offset in sectors.

Map: CSV with column 'lba' and a turns column (default candidate_timing_turns). Interpolated with an
interpolating cubic spline (passes through every point; the local structure is kept).
Offset: the host-observed completion phase of LBA L is assumed to belong to physical position L + dn,
so the physical turns of a byte at L is map(L - dn). dn may be fractional (photo fits gave ~14.2-14.8).
Usage: tone_from_map.py <map.csv> <image> <out.npy> <dn_sectors> [turns_column] [rot_deg=0] [mirror=0]"""
import sys, csv
import numpy as np
from PIL import Image
from scipy.interpolate import CubicSpline

mp, img_path, out, dn = sys.argv[1], sys.argv[2], sys.argv[3], float(sys.argv[4])
col = sys.argv[5] if len(sys.argv) > 5 else 'candidate_timing_turns'
rot = np.radians(float(sys.argv[6])) if len(sys.argv) > 6 else 0.0
mirror = len(sys.argv) > 7 and sys.argv[7] == '1'
S, SB, START, L0_END, LEVELS, RIMG = 2048, 38688, 32, 2092896, 7, 58.0

rows = list(csv.DictReader(open(mp)))
Lm = np.array([float(r['lba']) for r in rows]); Um = np.array([float(r[col]) for r in rows])
o = np.argsort(Lm); Lm, Um = Lm[o], Um[o]
cs = CubicSpline(Lm, Um, extrapolate=True)
def turns(L): return cs(L - dn)
U0, U1 = float(turns(START)), float(turns(L0_END))
if U1 < U0: sgn = -1.0          # map may be stored with decreasing turns; angle direction follows the track
else: sgn = 1.0
P = (58.0 - 24.0) / abs(U1 - U0)
print(f'map points {len(Lm)} ({Lm[0]:.0f}..{Lm[-1]:.0f}); turns over L0 {abs(U1-U0):.2f}; dn={dn} sectors')

im = np.asarray(Image.open(img_path).convert('L'), np.float32)
if mirror: im = im[:, ::-1]
H, W = im.shape
dark = np.clip((245.0 - im) / 215.0, 0, 1) * (LEVELS - 1)
q = np.arange(S) + 12
BITOFF = ((q // 172) * 2 * 1488 + ((q % 172) // 91) * 1488 + 32 + ((q % 172) % 91) * 16) / SB

tone = np.lib.format.open_memmap(out, 'w+', np.uint8, ((L0_END - START) * S,))
rng = np.random.default_rng(5)
lba, B = START, 1024
while lba < L0_END:
    n = min(B, L0_END - lba)
    Ub = sgn * (turns(np.arange(lba, lba + n + 1, dtype=np.float64)) - U0)
    Uf = Ub[:-1, None] + (Ub[1:] - Ub[:-1])[:, None] * BITOFF[None, :]
    r = 24.0 + P * Uf
    th = 2 * np.pi * (Uf % 1.0) + rot
    px = np.clip(((r * np.cos(th)) / RIMG * 0.5 + 0.5) * (W - 1), 0, W - 1).astype(np.int32)
    py = np.clip(((r * np.sin(th)) / RIMG * 0.5 + 0.5) * (H - 1), 0, H - 1).astype(np.int32)
    v = dark[py, px]
    lv = np.minimum(np.floor(v + rng.random(v.shape, dtype=np.float32)), LEVELS - 1).astype(np.uint8)
    tone[(lba - START) * S:(lba - START + n) * S] = lv.ravel()
    lba += n
tone.flush(); print('done', out)
