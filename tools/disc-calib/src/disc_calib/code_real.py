from __future__ import annotations

import math
from dataclasses import dataclass

import cv2
import numpy as np
from scipy.ndimage import gaussian_filter1d
from scipy.optimize import minimize

from .code import measure_rings, _coverage_deg
from .detect import DetectError, Ellipse, find_outer_edge
from .geometry import Geometry, Params, PhaseTable
from .pattern import PatternSpec
from .rectify import refine_with_circles
from .trace import sample_polar, signal_image

DISC_RADIUS_MM = 60.0


def _boost(ex: float, ey: float) -> np.ndarray:
    norm = math.hypot(ex, ey)
    if norm < 1e-9:
        return np.eye(3)
    d = np.array([ex, ey]) / norm
    m = np.eye(3)
    ch, sh = math.cosh(norm), math.sinh(norm)
    m[:2, :2] += (ch - 1) * np.outer(d, d)
    m[:2, 2] = sh * d
    m[2, :2] = sh * d
    m[2, 2] = ch
    return m


def _ellipse_matrix(e: Ellipse, eta, scale=1.0) -> np.ndarray:
    c, s = math.cos(e.angle), math.sin(e.angle)
    a = np.eye(3)
    a[:2, :2] = np.array([[c, -s], [s, c]]) @ np.diag([e.a, e.b])
    a[:2, 2] = [e.cx, e.cy]
    return a @ _boost(*eta) @ np.diag([scale / DISC_RADIUS_MM, scale / DISC_RADIUS_MM, 1.0])


def _ring_sharpness(gray, matrix) -> float:
    polar = sample_polar(gray, matrix, np.arange(23.0, 57.5, 0.04), np.radians(np.arange(0, 360, 0.5)))
    profile = np.median(polar, axis=1)
    high = profile - gaussian_filter1d(profile, 12)
    return float(np.sum(high ** 2) / (np.mean(profile) ** 2 + 1e-6))


def _ring_scale(gray, matrix, radii) -> float:
    grid = np.arange(22.0, 59.0, 0.01)
    polar = np.log(np.maximum(sample_polar(gray, matrix, grid, np.radians(np.arange(0, 360, 0.25))), 2.0))
    profile = np.median(polar - gaussian_filter1d(polar, 60, axis=0), axis=1)
    factors = np.arange(0.965, 1.0351, 0.0001)
    score = [sum((np.interp(r * k - 0.35, grid, profile) + np.interp(r * k + 0.35, grid, profile)) / 2
                 - np.interp(r * k, grid, profile) for r in radii) for k in factors]
    return float(factors[int(np.argmax(score))])


def locate_from_edge(bgr, signal, spec: PatternSpec):
    try:
        ellipse = find_outer_edge(bgr).ellipse
    except DetectError:
        return None
    radii = spec.solid_ring_radii()
    gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY).astype(np.float32)
    grid = np.arange(-0.16, 0.1601, 0.04)
    _, ex, ey = max((_ring_sharpness(gray, _ellipse_matrix(ellipse, (a, b))), a, b) for a in grid for b in grid)
    simplex = [[ex, ey], [ex + 0.02, ey], [ex, ey + 0.02]]
    eta = minimize(lambda q: -_ring_sharpness(gray, _ellipse_matrix(ellipse, q)), [ex, ey], method="Nelder-Mead",
                   options=dict(xatol=2e-4, fatol=1e-5, initial_simplex=simplex, maxiter=200)).x
    matrix = _ellipse_matrix(ellipse, eta)
    matrix = matrix @ np.diag([_ring_scale(gray, matrix, radii)] * 2 + [1.0])
    model = "edge"
    measured = measure_rings(signal, matrix, radii, 0.5)
    measured = [m for m in measured if abs(float(np.median(m[2])) - m[0]) < 0.3]
    rms = math.inf
    if measured:
        rms = float(np.sqrt(np.mean(np.concatenate([(rk - r) ** 2 for r, _, rk, _ in measured]))))
    for _ in range(3):
        if len(measured) < 4 or _coverage_deg(measured) < 180:
            break
        result = refine_with_circles(matrix, [(r, s) for r, s, _, _ in measured])
        if not result.succeeded:
            break
        candidate = np.asarray(result.matrix, float).reshape(3, 3)
        check = measure_rings(signal, candidate, radii, 0.3)
        check = [m for m in check if abs(float(np.median(m[2])) - m[0]) < 0.2]
        if len(check) < 4:
            break
        new_rms = float(np.sqrt(np.mean(np.concatenate([(rk - r) ** 2 for r, _, rk, _ in check]))))
        if new_rms >= rms:
            break
        matrix, rms, measured, model = candidate, new_rms, check, "rings"
    return matrix, {float(r): int(len(s)) for r, s, _, _ in measured}, _coverage_deg(measured), rms, model


STEP_DEG = 0.2
WINDOW_DEG = 60.0
WINDOW_STRIDE_DEG = 10.0
SHEAR_LIMIT_DEG_PER_MM = 45.0
SHEAR_STEP_DEG_PER_MM = 0.25
MIN_MATCH = 0.75
MIN_MIRROR_GAP = 0.10


@dataclass
class CodeRead:

    radius: float
    delta: float
    shear: float
    match: float
    mirror: float

    @property
    def readable(self) -> bool:
        return self.match >= MIN_MATCH and self.match - self.mirror >= MIN_MIRROR_GAP


def _level_bits(level) -> np.ndarray:
    return np.asarray(level.bits) if level.bits else (np.arange(level.sectors) % 2 == 0).astype(int)


def _ideal_wave(level, start_angle, theta) -> np.ndarray:
    sector = (np.mod(theta - start_angle, 2 * math.pi) / (2 * math.pi) * level.sectors).astype(int) % level.sectors
    return 1.0 - 2.0 * _level_bits(level)[sector]


def _fine_levels(spec: PatternSpec):
    return sorted(spec.levels, key=lambda lv: -lv.sectors)[:2]


def _window_match(profile, valid, wave, stride, width):
    count = profile.shape[1]
    starts = np.arange(0, count, stride)
    best = np.zeros(count)
    picked = np.zeros(count, int)
    spectrum = np.conj(np.fft.rfft(wave, axis=1))
    doubled = np.concatenate([wave, wave], axis=1)
    energy = np.cumsum(np.concatenate([np.zeros((wave.shape[0], 1)), doubled ** 2], axis=1), axis=1)
    level_sum = np.cumsum(np.concatenate([np.zeros((wave.shape[0], 1)), doubled], axis=1), axis=1)
    for w0 in starts:
        index = (w0 + np.arange(width)) % count
        if valid[:, index].mean() < 0.8:
            continue
        mask = np.zeros(profile.shape)
        mask[:, index] = valid[:, index]
        used = mask.sum(axis=1, keepdims=True)
        windowed = profile * mask
        windowed = windowed - mask * windowed.sum(axis=1, keepdims=True) / np.maximum(used, 1.0)
        norm = np.sqrt((windowed ** 2).sum())
        if norm < 1e-6:
            continue
        cross = np.fft.irfft(np.fft.rfft(windowed, axis=1) * spectrum, n=count, axis=1).sum(axis=0)
        first = (w0 - np.arange(count)) % count
        variance = np.zeros(count)
        for layer in range(wave.shape[0]):
            total = level_sum[layer, first + width] - level_sum[layer, first]
            variance += energy[layer, first + width] - energy[layer, first] - total ** 2 / width
        score = np.abs(cross) / (norm * np.sqrt(np.maximum(variance, 1e-6)))
        better = score > best
        best[better] = score[better]
        picked[better] = w0
    return best, picked


def read_code(bgr, matrix, spec: PatternSpec) -> CodeRead | None:
    levels = _fine_levels(spec)
    start = math.radians(spec.half_disc_start_deg)
    count = int(round(360 / STEP_DEG))
    theta = np.radians(np.arange(count) * STEP_DEG)
    gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY).astype(np.float32)
    high = bgr.max(axis=2).astype(np.float32)
    middle = float(np.mean([v for lv in levels for v in (lv.inner_mm, lv.outer_mm)]))
    rows = []
    for level in levels:
        radius = np.arange(level.inner_mm + 0.3, level.outer_mm - 0.3, 0.1)
        polar = sample_polar(gray, matrix, radius, theta)
        bright = sample_polar(high, matrix, radius, theta)
        rows.append((radius, np.log(np.maximum(polar, 3.0)), (bright < 248) & (bright > 6)))
    wave = np.array([_ideal_wave(lv, start, theta) for lv in levels])
    wave = wave - gaussian_filter1d(wave, 4.0 / STEP_DEG, axis=1, mode="wrap")
    mirror = wave[:, ::-1]
    width = int(WINDOW_DEG / STEP_DEG)
    stride = int(WINDOW_STRIDE_DEG / STEP_DEG)
    best = None
    best_mirror = 0.0
    for shear in np.arange(-SHEAR_LIMIT_DEG_PER_MM, SHEAR_LIMIT_DEG_PER_MM + 1e-9, SHEAR_STEP_DEG_PER_MM):
        profile, valid = [], []
        for radius, polar, ok in rows:
            shift = np.rint(shear * (radius - middle) / STEP_DEG).astype(int)
            index = (np.arange(count)[None, :] + shift[:, None]) % count
            lines = np.take_along_axis(polar, index, axis=1)
            good = np.take_along_axis(ok, index, axis=1)
            profile.append(np.median(lines, axis=0))
            valid.append(good.mean(axis=0) > 0.7)
        profile = np.array(profile)
        profile = profile - gaussian_filter1d(profile, 4.0 / STEP_DEG, axis=1, mode="wrap")
        valid = np.array(valid)
        score, _ = _window_match(profile, valid, wave, stride, width)
        k = int(np.argmax(score))
        if best is None or score[k] > best[0]:
            best = (float(score[k]), k * STEP_DEG, float(shear))
        best_mirror = max(best_mirror, float(_window_match(profile, valid, mirror, stride, width)[0].max()))
    if best is None:
        return None
    return CodeRead(middle, math.radians(best[1]), math.radians(best[2]), best[0], best_mirror)


EDGE_STEP_DEG = 0.1
EDGE_DR_MM = 0.05


def _table(generated: Params, actual: Params) -> PhaseTable:
    return PhaseTable(Geometry(generated), Geometry(actual), generated.inner + 0.3, generated.outer - 0.3)


def _with(generated: Params, x) -> Params:
    pitch = tuple(x[2:5]) if len(x) >= 5 else (generated.pitch_linear, generated.pitch_quadratic, generated.pitch_cubic)
    return Params(generated.kind, float(x[0]), float(x[1]), generated.sectors, generated.linear_density, *pitch)


@dataclass
class EdgeMap:

    radius: np.ndarray
    strength: np.ndarray
    level_of_row: np.ndarray
    edges: list
    levels: list


def edge_map(bgr, matrix, spec: PatternSpec) -> EdgeMap:
    start = spec.half_disc_start_deg
    theta = np.radians(np.arange(0, 360, EDGE_STEP_DEG))
    radii, rows, owner, edges, levels = [], [], [], [], []
    for level in spec.levels:
        bits = _level_bits(level)
        changed = np.flatnonzero(bits != np.roll(bits, 1))
        radius = np.arange(level.inner_mm + 0.15, level.outer_mm - 0.15, EDGE_DR_MM)
        if radius.size == 0 or changed.size == 0:
            continue
        polar = sample_polar(bgr.astype(np.float32), matrix, radius, theta)
        log = np.log(np.maximum(polar, 3.0))
        grad = gaussian_filter1d(log, 2.5, axis=1, order=1, mode="wrap")
        strength = np.sqrt((grad ** 2).sum(axis=2))
        bad = (polar.max(axis=2) > 248) | (polar.max(axis=2) < 6)
        strength[cv2.dilate(bad.astype(np.uint8), np.ones((1, 21), np.uint8)) > 0] = 0.0
        local = gaussian_filter1d(strength, 100, axis=1, mode="wrap") + 1e-3
        rows.append(np.minimum(strength / local, 6.0))
        radii.append(radius)
        owner.append(np.full(radius.size, len(levels)))
        edges.append(np.mod(start + changed * 360.0 / level.sectors, 360.0))
        levels.append(level)
    return EdgeMap(np.concatenate(radii), np.concatenate(rows), np.concatenate(owner), edges, levels)


def _score_table(m: EdgeMap) -> np.ndarray:
    count = m.strength.shape[1]
    blurred = gaussian_filter1d(m.strength - m.strength.mean(axis=1, keepdims=True), 5.0, axis=1, mode="wrap")
    spectrum = np.fft.rfft(blurred, axis=1)
    out = np.empty(m.strength.shape, np.float32)
    for i, angles in enumerate(m.edges):
        comb = np.zeros(count)
        comb[np.rint(angles / EDGE_STEP_DEG).astype(int) % count] = 1.0 / math.sqrt(angles.size)
        rows = m.level_of_row == i
        out[rows] = np.fft.irfft(spectrum[rows] * np.conj(np.fft.rfft(comb)), n=count, axis=1)
    return out


def _phase_basis(generated: Params, radius):
    h = 2e-5
    def phi(inner, outer):
        return _table(generated, _with(generated, [inner, outer])).phase(radius)
    return np.column_stack([(phi(generated.inner + h, generated.outer) - phi(generated.inner - h, generated.outer)) / (2 * h),
                            (phi(generated.inner, generated.outer + h) - phi(generated.inner, generated.outer - h)) / (2 * h)])


def search_parameters(generated: Params, photos, half_turns=1.5, step_turns=0.01, shear_tolerance_deg=2.0):
    radius = photos[0][1].radius
    rows = np.arange(0, radius.size, 3)
    r = radius[rows]
    rm = photos[0][0].radius
    basis = _phase_basis(generated, r)
    fine = np.arange(rm - 1.0, rm + 1.01, 0.5)
    slope_basis = np.gradient(_phase_basis(generated, fine), fine, axis=0)[2]
    at_mid = np.array([np.interp(rm, r, basis[:, 0]), np.interp(rm, r, basis[:, 1])])
    u_, s_, vt = np.linalg.svd(basis - basis.mean(0), full_matrices=False)
    per = s_ / math.sqrt(r.size) / (2 * math.pi)
    us = np.arange(-half_turns, half_turns + 1e-9, step_turns) / per[0]
    vs = np.arange(-half_turns, half_turns + 1e-9, step_turns) / per[1]
    uu, vv = np.meshgrid(us, vs)
    deltas = uu[..., None] * vt[0] + vv[..., None] * vt[1]
    shear = np.median([p[0].shear for p in photos])
    allowed = np.abs(deltas @ slope_basis - shear) < math.radians(shear_tolerance_deg)
    grid = deltas[allowed]
    if grid.shape[0] == 0:
        return None
    count = int(round(360 / EDGE_STEP_DEG))
    table = np.empty((len(photos), grid.shape[0]))
    for p, (read, _, scores) in enumerate(photos):
        kept = scores[rows]
        phase = (grid @ basis.T - (grid @ at_mid)[:, None]) + read.delta
        index = np.rint(np.degrees(phase) / EDGE_STEP_DEG).astype(np.int64) % count
        best = None
        for slack in range(-10, 11, 2):
            value = kept[np.arange(rows.size)[None, :], (index + slack) % count].sum(axis=1)
            best = value if best is None else np.maximum(best, value)
        table[p] = best
    median = np.median(table, axis=1, keepdims=True)
    scale = 1.4826 * np.median(np.abs(table - median), axis=1, keepdims=True) + 1e-6
    normalized = (table - median) / scale
    total = normalized.sum(axis=0)
    if len(photos) >= 3:
        total = total - normalized.max(axis=0)
    k = int(np.argmax(total))
    return grid[k], float(total[k]), float(np.median(total)), float(np.median(np.abs(total - np.median(total))) * 1.4826)


def measure_edges_near(m: EdgeMap, table: PhaseTable, read: CodeRead, half_deg=3.0, band_mm=0.5):
    count = m.strength.shape[1]
    half = int(half_deg / EDGE_STEP_DEG)
    offsets = np.arange(-half, half + 1)
    base = read.delta - float(table.phase(read.radius))
    samples = []
    for i, angles in enumerate(m.edges):
        rows = np.flatnonzero(m.level_of_row == i)
        radius = m.radius[rows]
        chunk = max(int(band_mm / EDGE_DR_MM), 3)
        for angle in angles:
            for s in range(0, rows.size - chunk + 1, chunk):
                part = rows[s:s + chunk]
                r = radius[s:s + chunk]
                center = np.degrees(table.phase(r) + base) + angle
                index = np.rint(center / EDGE_STEP_DEG).astype(int)[:, None] + offsets[None, :]
                line = gaussian_filter1d(m.strength[part[:, None], index % count].mean(axis=0), 1.5)
                k = int(np.argmax(line))
                if not 2 <= k < line.size - 2 or line[k] < 2.5 * np.median(line) or line[k] < 1.5:
                    continue
                a, b, c = line[k - 1:k + 2]
                bottom = a - 2 * b + c
                sub = 0.5 * (a - c) / bottom if bottom != 0 else 0.0
                offset = (offsets[k] + sub) * EDGE_STEP_DEG
                measured = float(np.mean(center)) + offset
                samples.append((float(np.mean(r)), math.radians(measured - angle), math.radians(measured)))
    return samples


def fit_edges(generated: Params, seed: Params, samples, cubic=False, iterations=6):
    x = np.array([seed.inner, seed.outer] + ([seed.pitch_linear, seed.pitch_quadratic, seed.pitch_cubic] if cubic else []))
    index = np.concatenate([np.full(len(s), i) for i, s in enumerate(samples)])
    data = np.array([v for s in samples for v in s], float)
    r, delta, theta = data[:, 0], data[:, 1], data[:, 2]
    n = len(samples)
    steps = np.full(x.size, 2e-5)
    nuisance = np.zeros((r.size, 3 * n))
    for i in range(n):
        mine = index == i
        nuisance[mine, 3 * i] = 1.0
        nuisance[mine, 3 * i + 1] = np.cos(theta[mine]) / r[mine]
        nuisance[mine, 3 * i + 2] = np.sin(theta[mine]) / r[mine]
    weight = np.ones(r.size)
    residual = np.zeros(r.size)
    coefficient = np.zeros(3 * n)
    for iteration in range(iterations):
        base = _table(generated, _with(generated, x)).phase(r)
        jac = np.column_stack([
            (_table(generated, _with(generated, x + np.eye(x.size)[j] * steps[j])).phase(r)
             - _table(generated, _with(generated, x - np.eye(x.size)[j] * steps[j])).phase(r)) / (2 * steps[j])
            for j in range(x.size)])
        target = delta - base
        design = np.column_stack([jac, nuisance])
        solution, *_ = np.linalg.lstsq(design * weight[:, None], target * weight, rcond=None)
        x = x + solution[:x.size]
        coefficient = solution[x.size:]
        residual = target - design @ solution
        scale = max(1.4826 * float(np.median(np.abs(residual))), math.radians(0.1))
        weight = np.sqrt(1.0 / np.maximum(np.abs(residual) / (2 * scale), 1.0))
    keep = np.abs(residual) < 3 * max(1.4826 * float(np.median(np.abs(residual))), math.radians(0.1))
    return _with(generated, x), coefficient, residual, keep


def analyze_real(path, spec: PatternSpec, generated: Params):
    from .code import CodePhoto, load_photo, locate_rings

    bgr = load_photo(path)
    signal = signal_image(bgr)
    photo = CodePhoto(path=str(path))
    located = locate_from_edge(bgr, signal, spec)
    if located is not None:
        photo.matrix, photo.rings, photo.coverage_deg, photo.ring_rms_mm, photo.model = located
    else:
        fallback = locate_rings(signal, spec)
        if fallback is None:
            photo.notes.append("Neither the outer edge nor the ring barcode located the disc.")
            return photo, None, None
        fallback.path = str(path)
        photo = fallback
    read = read_code(bgr, photo.matrix, spec)
    if read is not None:
        photo.layers["code"] = dict(match=round(read.match, 3), mirror=round(read.mirror, 3), readable=read.readable,
                                    deltaDeg=round(math.degrees(read.delta), 2), shearDegPerMm=round(math.degrees(read.shear), 2))
    if read is None or not read.readable:
        photo.notes.append("Code layer unreadable: no match clearly better than the mirrored target.")
        return photo, None, None
    edges = edge_map(bgr, photo.matrix, spec)
    return photo, read, edges


def decode_real(paths, generated: Params, spec: PatternSpec, cubic=False):
    from .code import CodeResult

    analyzed = [analyze_real(path, spec, generated) for path in paths]
    photos = [a[0] for a in analyzed]
    good = [(photo, read, edges) for photo, read, edges in analyzed if read is not None]
    if not good:
        return None
    inputs = [(read, edges, _score_table(edges)) for _, read, edges in good]
    found = search_parameters(generated, inputs)
    if found is None:
        return None
    step, score, median, spread = found
    actual = _with(generated, [generated.inner + step[0], generated.outer + step[1]])
    residual, keep, coefficient = np.zeros(0), np.zeros(0, bool), np.zeros(0)
    samples = []
    for half in (3.0, 2.0):
        table = _table(generated, actual)
        samples = [measure_edges_near(edges, table, read, half_deg=half) for read, edges, _ in inputs]
        if any(len(s) < 8 for s in samples):
            return None
        actual, coefficient, residual, keep = fit_edges(generated, actual, samples)
    if cubic:
        fitted, coef3, res3, keep3 = fit_edges(generated, actual, samples, cubic=True)
        if np.std(res3[keep3]) < 0.98 * np.std(residual[keep]):
            actual, coefficient, residual, keep = fitted, coef3, res3, keep3
    table = _table(generated, actual)
    turns = float((table.phase(generated.outer - 0.3) - table.phase(generated.inner + 0.3)) / (2 * math.pi))
    radius = np.array([v[0] for s in samples for v in s])
    rms_deg = float(np.degrees(np.std(residual[keep])))
    rms_mm = float(np.sqrt(np.mean((residual[keep] * radius[keep]) ** 2)))
    significance = (score - median) / (spread + 1e-9)
    for i, (photo, _, _) in enumerate(good):
        photo.rotation = float(coefficient[3 * i])
        photo.edges = samples[i]
        photo.phase_samples = [(inputs[i][0].radius, inputs[i][0].delta)]
        photo.layers["code"]["centerErrorMm"] = [round(float(coefficient[3 * i + 1]), 3), round(float(coefficient[3 * i + 2]), 3)]
    quality = "good" if len(good) >= 2 and rms_deg < 1.0 and keep.sum() >= 60 and significance > 6 else "fair"
    notes = [f"Code layer readable in {len(good)}/{len(photos)} photos; edge-sample inliers {int(keep.sum())}/{keep.size}, residual {rms_deg:.2f}°"
             f" ({rms_mm:.2f} mm); narrow-band search significance {significance:.1f}."]
    return CodeResult(quality, actual, turns, 1, 0.0, rms_mm, photos, notes)
