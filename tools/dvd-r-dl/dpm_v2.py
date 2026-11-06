"""Read-only rotation-timing experiments with full raw logging.

Every READ(12)/FUA of one sector is logged as: seq, tag, lba, t_start_ns, t_end_ns, ok.
Pair delay = t_end(target) - t_end(ref).

  stair <lba> <wmax_ms> <step_ms> <out_prefix>
        read lba, busy-wait w, read lba again, for w = 0..wmax. Total time rises in steps of one
        revolution T; edge spacing measures T independently of any geometry.
  window <ref> <K> <reps> <order:fwd|rev|rand> <out_prefix> [seed]
        pairs (ref, ref+16k) for k=-K..K, k!=0, visited in the given order, reps passes."""
import sys, os, time, csv, random
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.argv, args = [sys.argv[0], 'noop'], sys.argv[1:]
import dpm_probe as d  # noqa: E402

class Log:
    def __init__(self, prefix):
        self.f = open(prefix + '_raw.csv', 'w', newline=''); self.w = csv.writer(self.f)
        self.w.writerow(['seq', 'tag', 'lba', 't_start_ns', 't_end_ns', 'ok']); self.seq = 0
    def read(self, lba, tag):
        t0 = time.perf_counter_ns(); ok = 1
        try: d.read_fua(lba)
        except RuntimeError: ok = 0
        t1 = time.perf_counter_ns()
        self.w.writerow([self.seq, tag, lba, t0, t1, ok]); self.seq += 1
        return t1, ok

def busy(ms):
    end = time.perf_counter_ns() + int(ms * 1e6)
    while time.perf_counter_ns() < end: pass

mode = args[0]
if mode == 'stair':
    lba, wmax, step, prefix = int(args[1]), float(args[2]), float(args[3]), args[4]
    log = Log(prefix); out = open(prefix + '_pairs.csv', 'w')
    out.write('w_ms,delay_ms\n')
    n = int(wmax / step) + 1
    for i in range(n):
        w = i * step
        t_ref, _ = log.read(lba, 'ref'); busy(w); t_tgt, ok = log.read(lba, f'w={w:.3f}')
        if ok: out.write(f'{w:.3f},{(t_tgt - t_ref) / 1e6:.4f}\n')
elif mode == 'window':
    ref, K, reps, order, prefix = int(args[1]), int(args[2]), int(args[3]), args[4], args[5]
    seed = int(args[6]) if len(args) > 6 else 1
    ks = [k for k in range(-K, K + 1) if k]
    if order == 'rev': ks = ks[::-1]
    log = Log(prefix); out = open(prefix + '_pairs.csv', 'w')
    out.write('pass,k,target_lba,delay_ms\n')
    rng = random.Random(seed)
    for p in range(reps):
        seq = ks[:]
        if order == 'rand': rng.shuffle(seq)
        for k in seq:
            t_ref, _ = log.read(ref, 'ref'); t_tgt, ok = log.read(ref + 16 * k, f'k={k}')
            if ok: out.write(f'{p},{k},{ref + 16 * k},{(t_tgt - t_ref) / 1e6:.4f}\n')
elif mode == 'long':
    # same protocol as the original long scan: ref then target, targets ref+16*(first+i*stride)
    ref, first, count, stride, reps, prefix = (int(v) for v in args[1:6]) + (args[6],) if False else \
        (int(args[1]), int(args[2]), int(args[3]), int(args[4]), int(args[5]), args[6])
    log = Log(prefix); out = open(prefix + '_pairs.csv', 'w')
    out.write('target_lba,median_ms,spread_ms\n')
    import statistics
    for i in range(count):
        tgt = ref + (first + i * stride) * 16
        ds = []
        for _ in range(reps):
            t_ref, _ = log.read(ref, 'ref'); t_tgt, ok = log.read(tgt, 'tgt')
            if ok: ds.append((t_tgt - t_ref) / 1e6)
        if ds:
            ds.sort(); out.write(f'{tgt},{statistics.median(ds):.4f},{ds[-1] - ds[0]:.4f}\n'); out.flush()
print('done', mode)
