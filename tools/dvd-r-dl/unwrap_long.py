"""Unwrap the long-range scan (scan_full.csv) into absolute turns using LOCAL extrapolation.

For a candidate period T, phase_i = (delay_i mod T)/T. Points are walked outward from the reference
in both directions; each new point's integer turn is chosen from a quadratic fit to the previous W
unwrapped points (in LBA), so no global geometry model is assumed. The T that gives the smallest
prediction residuals wins. Outputs unwrapped turns and diagnostics.
Usage: unwrap_long.py <scan_full.csv> <out.csv> [Tmin Tmax step] [W=8]"""
import sys
import numpy as np

csv, out = sys.argv[1], sys.argv[2]
Tmin, Tmax, Tstep = (float(v) for v in sys.argv[3:6]) if len(sys.argv) > 5 else (15.30, 15.50, 0.0005)
W = int(sys.argv[6]) if len(sys.argv) > 6 else 8
REF, L0_END = 100000, 2092896
a = np.loadtxt(csv, delimiter=',')
a = a[(a[:, 0] >= 32) & (a[:, 0] + 16 <= L0_END)]
o = np.argsort(a[:, 0]); L, y = a[o, 0], a[o, 1]
i_ref = int(np.argmin(np.abs(L - REF)))
SB, CB, P0, R0 = 38688, 146.7e-6, 0.7373753259e-3, 24.0

def seed_slope(Lv):  # turns per sector from the nominal model, only to start the walk
    r = np.sqrt(R0 ** 2 + Lv * SB * CB * P0 / np.pi)
    return SB * CB / (2 * np.pi * r) * 0.9953  # measured local scale

def walk(T):
    ph = (y % T) / T
    U = np.full(len(L), np.nan); pred_res = []
    U[i_ref] = ph[i_ref]
    for direction in (1, -1):
        idx = list(range(i_ref + direction, len(L) if direction > 0 else -1, direction))
        hist = [i_ref]
        for i in idx:
            h = hist[-W:]
            if len(h) >= 4:
                c = np.polyfit(L[h] - L[i], U[h], 2)
                pred = c[-1]
            else:
                j = h[-1]; pred = U[j] + seed_slope((L[i] + L[j]) / 2) * (L[i] - L[j])
            d = (ph[i] - pred + 0.5) % 1 - 0.5
            U[i] = pred + d; pred_res.append(d); hist.append(i)
    return U, np.array(pred_res)

best = None
for T in np.arange(Tmin, Tmax, Tstep):
    U, r = walk(T)
    score = np.sqrt(np.mean(r ** 2))
    if best is None or score < best[0]: best = (score, T, U, r)
score, T, U, r = best
print(f'best T={T:.4f} ms  prediction-residual rms={score:.4f} turn  |res|>0.1: {np.sum(np.abs(r) > 0.1)} of {len(r)}')
# global smoothness check: residual of a smoothing fit in sliding windows
sm = np.array([np.polyval(np.polyfit(L[max(0, i - 10):i + 11] - L[i], U[max(0, i - 10):i + 11], 3), 0) for i in range(len(L))])
res = U - sm
print(f'local-cubic residual rms={np.sqrt(np.mean(res ** 2)):.4f} turn; 95th pct |res|={np.percentile(np.abs(res), 95):.4f}')
# compare total turns with the nominal model
model = lambda Lv: (np.sqrt(R0 ** 2 + Lv * SB * CB * P0 / np.pi) - R0) / P0
span = U[-1] - U[0]; mspan = model(L[-1]) - model(L[0])
print(f'LBA {int(L[0])}..{int(L[-1])}: measured {span:.2f} turns, nominal model {mspan:.2f} turns, ratio {span/mspan:.6f}')
np.savetxt(out, np.c_[L, y, U, res], delimiter=',', header='lba,delay_ms,turns,local_resid', comments='', fmt='%.6f')
