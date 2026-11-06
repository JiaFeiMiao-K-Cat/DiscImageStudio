"""Shared, version-stable codec for data hidden in the pattern area (numpy only, no numpy RNG).

Layout in the scrambled domain of LBA PATTERN_START..L0_END: a byte position p (global index from
PATTERN_START*2048) is a carrier slot when splitmix64(p ^ SEED) % 1000 < 100 (10%).
Slots carry: MAGIC(8) | length(8, LE) | sha256(32) | payload, XOR-whitened by splitmix64(i ^ WSEED) low byte.
Unused slots carry whitening of zeros (random-looking)."""
import numpy as np

S = 2048; PSN0 = 0x30000
L0_END, TOTAL = 2092896, 4171712
PATTERN_START = 243232
SEED, WSEED = 0x5EED_D15C_0001, 0x5EED_D15C_0002
MAGIC = b'DVDHIDE1'
PRESETS = [0x0001, 0x5500, 0x0002, 0x2A00, 0x0004, 0x5400, 0x0008, 0x2800,
           0x0010, 0x5000, 0x0020, 0x2001, 0x0040, 0x4002, 0x0080, 0x0005]

def _table(seed):
    r = seed; out = bytearray(S)
    for i in range(S):
        out[i] = r & 0xFF
        for _ in range(8): r = ((r << 1) | (((r >> 14) ^ (r >> 10)) & 1)) & 0x7FFF
    return np.frombuffer(bytes(out), np.uint8)
KEYS = np.stack([_table(p) for p in PRESETS])

def keys_for(lba, n):
    return KEYS[((np.arange(lba, lba + n) + PSN0) >> 4) & 0xF]

def splitmix64(x):
    with np.errstate(over='ignore'):
        z = x + np.uint64(0x9E3779B97F4A7C15)
        z = (z ^ (z >> np.uint64(30))) * np.uint64(0xBF58476D1CE4E5B9)
        z = (z ^ (z >> np.uint64(27))) * np.uint64(0x94D049BB133111EB)
        return z ^ (z >> np.uint64(31))

def slot_mask(lba, n):
    p = np.arange((lba - PATTERN_START) * S, (lba - PATTERN_START + n) * S, dtype=np.uint64)
    return (splitmix64(p ^ np.uint64(SEED)) % np.uint64(1000) < np.uint64(100)).reshape(n, S)

def whitening(start, count):
    i = np.arange(start, start + count, dtype=np.uint64)
    return (splitmix64(i ^ np.uint64(WSEED)) & np.uint64(0xFF)).astype(np.uint8)
