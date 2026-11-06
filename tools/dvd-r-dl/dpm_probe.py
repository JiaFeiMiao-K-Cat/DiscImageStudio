"""Rotation-timing probe (read-only). Issues READ(12) with FUA so every read must come from the medium,
and times consecutive reads with perf_counter_ns.

  probe T  <lba> <n>         : re-read the same sector n times -> revolution period statistics
  probe pair <lba> <step> <n>: read lba, then lba+k*step for k=0..n -> phase of each relative to lba"""
import ctypes, ctypes.wintypes as wt, sys, time, statistics

k32 = ctypes.WinDLL('kernel32', use_last_error=True)
k32.CreateFileW.restype = wt.HANDLE

class SPTD(ctypes.Structure):
    _fields_ = [('Length', ctypes.c_ushort), ('ScsiStatus', ctypes.c_ubyte), ('PathId', ctypes.c_ubyte),
                ('TargetId', ctypes.c_ubyte), ('Lun', ctypes.c_ubyte), ('CdbLength', ctypes.c_ubyte),
                ('SenseInfoLength', ctypes.c_ubyte), ('DataIn', ctypes.c_ubyte), ('DataTransferLength', ctypes.c_ulong),
                ('TimeOutValue', ctypes.c_ulong), ('DataBuffer', ctypes.c_void_p), ('SenseInfoOffset', ctypes.c_ulong),
                ('Cdb', ctypes.c_ubyte * 16)]
class SPTDS(ctypes.Structure):
    _fields_ = [('sptd', SPTD), ('pad', ctypes.c_ulong), ('sense', ctypes.c_ubyte * 32)]

h = k32.CreateFileW('\\\\.\\G:', 0xC0000000, 3, None, 3, 0, None)
buf = ctypes.create_string_buffer(2048)

def cmd(cdb, n=2048, data_in=True):
    s = SPTDS(); s.sptd.Length = ctypes.sizeof(SPTD); s.sptd.CdbLength = len(cdb)
    s.sptd.SenseInfoLength = 32; s.sptd.DataIn = 1 if data_in else 0; s.sptd.DataTransferLength = n
    s.sptd.TimeOutValue = 30; s.sptd.DataBuffer = ctypes.addressof(buf) if n else None
    s.sptd.SenseInfoOffset = SPTDS.sense.offset
    for i, b in enumerate(cdb): s.sptd.Cdb[i] = b
    ret = wt.DWORD()
    ok = k32.DeviceIoControl(h, 0x4D014, ctypes.byref(s), ctypes.sizeof(s), ctypes.byref(s), ctypes.sizeof(s), ctypes.byref(ret), None)
    if not ok or s.sptd.ScsiStatus:
        raise RuntimeError(f'cmd {cdb[0]:02X} failed status={s.sptd.ScsiStatus} sense={bytes(s.sense)[2]&15:X}/{bytes(s.sense)[12]:02X}')

def read_fua(lba):
    cmd([0xA8, 0x08] + list(lba.to_bytes(4, 'big')) + [0, 0, 0, 1, 0, 0])

def set_speed(kbps):  # SET CD SPEED (also honoured by many DVD drives)
    try: cmd([0xBB, 0, kbps >> 8, kbps & 255, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0], 0, False)
    except RuntimeError as e: print('set speed:', e)

mode = sys.argv[1]
if len(sys.argv) > 2 and sys.argv[-1].startswith('speed='):
    set_speed(int(sys.argv.pop()[6:]))
if mode == 'T':
    lba, n = int(sys.argv[2]), int(sys.argv[3])
    read_fua(lba); t = time.perf_counter_ns(); d = []
    for _ in range(n):
        read_fua(lba); t2 = time.perf_counter_ns(); d.append((t2 - t) / 1e6); t = t2
    d.sort()
    print(f'n={n} min={d[0]:.3f} p10={d[n//10]:.3f} median={statistics.median(d):.3f} p90={d[9*n//10]:.3f} max={d[-1]:.3f} ms')
    print('stdev of middle 80%:', round(statistics.pstdev(d[n//10:9*n//10]), 4), 'ms')
elif mode == 'pair':
    lba, step, n = int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
    for k in range(n + 1):
        ds = []
        for _ in range(7):
            read_fua(lba); t = time.perf_counter_ns(); read_fua(lba + k * step); ds.append((time.perf_counter_ns() - t) / 1e6)
        print(k * step, ' '.join(f'{x:.3f}' for x in sorted(ds)))
