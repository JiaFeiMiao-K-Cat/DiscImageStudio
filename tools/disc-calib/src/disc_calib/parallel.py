from __future__ import annotations

import os
from concurrent.futures import ThreadPoolExecutor

import numpy as np

_pool: ThreadPoolExecutor | None = None


def thread_count() -> int:
    configured = os.environ.get("DISC_CALIB_THREADS")
    if configured:
        return max(int(configured), 1)
    available = len(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else (os.cpu_count() or 1)
    return max(available, 1)


def _executor() -> ThreadPoolExecutor:
    global _pool
    if _pool is None:
        _pool = ThreadPoolExecutor(max_workers=thread_count())
    return _pool


def chunks(length: int, minimum: int = 1) -> list[tuple[int, int]]:
    parts = max(min(thread_count() * 2, length // max(minimum, 1)), 1)
    edges = np.linspace(0, length, parts + 1).astype(int)
    return [(int(a), int(b)) for a, b in zip(edges[:-1], edges[1:]) if b > a]


def parallel_rows(function, length: int, minimum: int = 1) -> None:
    parts = chunks(length, minimum)
    if len(parts) == 1 or thread_count() == 1:
        for start, stop in parts:
            function(start, stop)
        return
    for future in [_executor().submit(function, start, stop) for start, stop in parts]:
        future.result()


def parallel_map(function, items) -> list:
    items = list(items)
    if len(items) <= 1 or thread_count() == 1:
        return [function(item) for item in items]
    return list(_executor().map(function, items))


_process_pool = None


def _quiet_worker() -> None:
    os.environ["DISC_CALIB_THREADS"] = "1"
    try:
        import cv2

        cv2.setNumThreads(1)
    except ImportError:
        pass


def _context():
    import multiprocessing as mp

    context = mp.get_context("forkserver")
    os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
    os.environ.setdefault("OMP_NUM_THREADS", "1")
    context.set_forkserver_preload(["numpy", "scipy.optimize", "scipy.interpolate"])
    return context


def enable_processes(workers: int | None = None) -> None:
    global _process_pool
    if _process_pool is None and thread_count() > 1:
        from concurrent.futures import ProcessPoolExecutor

        _process_pool = ProcessPoolExecutor(
            max_workers=workers or thread_count(), mp_context=_context(), initializer=_quiet_worker,
        )


def disable_processes() -> None:
    global _process_pool
    if _process_pool is not None:
        _process_pool.shutdown()
        _process_pool = None


def process_map(function, items) -> list:
    items = list(items)
    if _process_pool is None or len(items) <= 1:
        return [function(item) for item in items]
    return list(_process_pool.map(function, items))


def photo_pool(photos: int):
    from concurrent.futures import ProcessPoolExecutor

    workers = max(min(photos, thread_count() // 4), 1)
    threads = max(thread_count() // workers, 1)
    return ProcessPoolExecutor(
        max_workers=workers, mp_context=_context(), initializer=_photo_worker, initargs=(threads,),
    ), threads


def _photo_worker(threads: int) -> None:
    os.environ["DISC_CALIB_THREADS"] = str(threads)
    import cv2

    cv2.setNumThreads(threads)
