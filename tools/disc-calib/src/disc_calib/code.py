from __future__ import annotations

import itertools
import json
import math
from dataclasses import dataclass, field
from pathlib import Path

import cv2
import numpy as np
from scipy.ndimage import gaussian_filter, gaussian_filter1d
from scipy.optimize import least_squares
from scipy.signal import find_peaks

from .geometry import CrossModel, Geometry, Params, PhaseTable
from .parallel import parallel_map
from .pattern import PatternSpec, SOLID_RING_MM, design_code
from .rectify import refine_with_circles
from .trace import disc_to_source, sample_polar, signal_image

RING_HALF_MM = SOLID_RING_MM / 2
LANE_MM = 0.3
SCALES_MM_PER_PX = np.geomspace(0.012, 0.09, 36)
WEDGE_DEG = 15.0
READABLE_BER = 0.12


def load_photo(path: Path) -> np.ndarray:
    path = Path(path)
    if path.suffix.lower() in (".heic", ".heif"):
        from PIL import Image, ImageOps
        import pillow_heif

        pillow_heif.register_heif_opener()
        image = ImageOps.exif_transpose(Image.open(path)).convert("RGB")
        return cv2.cvtColor(np.asarray(image), cv2.COLOR_RGB2BGR)
    image = cv2.imread(str(path), cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError(f"Cannot read photo: {path}")
    return image


def center_seed(signal: np.ndarray, window: float = 20.0, step: int = 12) -> np.ndarray:
    h, w = signal.shape
    gx = gaussian_filter(signal, 1.5, order=(0, 1))
    gy = gaussian_filter(signal, 1.5, order=(1, 0))
    jxx = gaussian_filter(gx * gx, window)
    jyy = gaussian_filter(gy * gy, window)
    jxy = gaussian_filter(gx * gy, window)
    ys, xs = np.mgrid[int(window):h - int(window):step, int(window):w - int(window):step]
    xx, yy, xy = jxx[ys, xs], jyy[ys, xs], jxy[ys, xs]
    angle = 0.5 * np.arctan2(2 * xy, xx - yy)
    big = (xx + yy) / 2 + np.sqrt(((xx - yy) / 2) ** 2 + xy ** 2)
    small = xx + yy - big
    coherence = (big - small) / (big + small + 1e-6)
    weight0 = (coherence * np.sqrt(big)).ravel()
    keep = weight0 > np.percentile(weight0, 40)
    points = np.column_stack([xs.ravel(), ys.ravel()]).astype(float)[keep]
    normal = np.column_stack([np.cos(angle.ravel()), np.sin(angle.ravel())])[keep]
    weight0 = weight0[keep] / weight0[keep].max()
    a = np.column_stack([-normal[:, 1], normal[:, 0]])
    b = np.sum(points * a, axis=1)
    weight = weight0.copy()
    center = np.array([w / 2, h / 2])
    for _ in range(12):
        center, *_ = np.linalg.lstsq(a * weight[:, None], b * weight, rcond=None)
        residual = a @ center - b
        scale = 1.4826 * np.median(np.abs(residual)) + 1.0
        weight = weight0 / np.sqrt(1 + (residual / (2 * scale)) ** 2)
    return center


def _ring_kernel(scale: float) -> np.ndarray:
    half = int(math.ceil(0.5 / scale)) + 1
    x = np.arange(-half, half + 1) * scale
    kernel = np.where(np.abs(x) <= RING_HALF_MM, -1.0, np.where(np.abs(x) <= RING_HALF_MM + LANE_MM, 1.0, 0.0))
    kernel -= kernel.mean()
    return kernel / np.sum(np.abs(kernel))


def wedge_profile(signal, center, angle_lo, angle_hi, step_deg=0.25):
    h, w = signal.shape
    corners = np.array([[0, 0], [w, 0], [0, h], [w, h]], float)
    rmax = float(np.hypot(*(corners - center).T).max())
    radius = np.arange(0.0, rmax, 1.0)
    theta = np.radians(np.arange(angle_lo, angle_hi, step_deg))
    x = center[0] + np.outer(radius, np.cos(theta))
    y = center[1] + np.outer(radius, np.sin(theta))
    polar = cv2.remap(signal, x.astype(np.float32), y.astype(np.float32), cv2.INTER_LINEAR,
                      borderMode=cv2.BORDER_CONSTANT, borderValue=-1.0)
    inside = polar >= 0
    count = inside.sum(axis=1)
    values = np.where(inside, polar, np.nan)
    with np.errstate(all="ignore"):
        profile = np.nanmedian(values, axis=1)
    profile[count < max(3, 0.6 * theta.size)] = np.nan
    good = np.isfinite(profile)
    if good.sum() < 50:
        return radius, np.full_like(radius, np.nan)
    filled = np.interp(radius, radius[good], profile[good])
    background = gaussian_filter1d(filled, 40.0)
    flat = filled / (background + 8.0) - 1.0
    flat[~good] = np.nan
    return radius, flat


def ring_candidates(radius, flat, max_candidates=12):
    good = np.isfinite(flat)
    envelope = np.zeros(radius.size)
    if good.sum() < 50:
        return [], envelope
    signal = np.where(good, flat, 0.0)
    found: list[tuple[float, float, float]] = []
    for scale in SCALES_MM_PER_PX:
        kernel = _ring_kernel(scale)
        if kernel.size >= signal.size // 2:
            continue
        response = np.correlate(signal, kernel, mode="same")
        response[~good] = 0.0
        envelope = np.maximum(envelope, response)
        distance = max(3, int(0.8 / scale))
        peaks, properties = find_peaks(response, height=0.03, distance=distance)
        found.extend((float(radius[p]), float(scale), float(h)) for p, h in zip(peaks, properties["peak_heights"]))
    found.sort(key=lambda item: -item[2])
    candidates: list[tuple[float, float, float]] = []
    for item in found:
        if all(abs(item[0] - c[0]) > max(4.0, 0.3 / c[1]) for c in candidates):
            candidates.append(item)
        if len(candidates) >= max_candidates:
            break
    return candidates, envelope


def identify_rings(candidates, code_radii, radius=None, envelope=None, tolerance=0.08, min_response=0.07):
    if len(candidates) < 3:
        return None
    rho = np.array(sorted(c[0] for c in candidates))
    template = np.median([c[1] for c in candidates])
    code = np.asarray(code_radii, float)
    hypotheses = []
    for size in range(min(len(rho), len(code)), 2, -1):
        for subset in itertools.combinations(range(len(rho)), size):
            p = rho[list(subset)]
            for mapping in itertools.combinations(range(len(code)), size):
                gaps_px = np.diff(p)
                gaps_mm = np.diff(code[list(mapping)])
                ratio = gaps_px / gaps_mm
                scale = float(np.exp(np.mean(np.log(ratio))))
                if abs(math.log(scale * template)) > math.log(3.0):
                    continue
                cost = float(np.max(np.abs(np.log(ratio / scale))))
                if cost < tolerance:
                    origin = float(np.mean(p - scale * code[list(mapping)]))
                    hypotheses.append((scale, origin, size, cost))
    if not hypotheses:
        return None
    best = None
    for scale, origin, size, cost in hypotheses:
        predicted = origin + scale * code
        responses = {}
        for i, value in enumerate(predicted):
            j = int(round(value))
            if envelope is None:
                responses[i] = 1.0
            elif 2 <= j < envelope.size - 2 and np.isfinite(envelope[j - 2:j + 3]).all():
                responses[i] = float(envelope[j - 2:j + 3].max())
        if len(responses) < 3 or min(responses.values()) < 0.7 * min_response:
            continue
        score = sum(responses.values()) - cost
        if best is None or score > best[0]:
            best = (score, scale, origin, sorted(responses))
    if best is None:
        return None
    score, scale, origin, matched = best
    positions = {}
    for i in matched:
        value = origin + scale * code[i]
        nearest = rho[np.argmin(np.abs(rho - value))]
        positions[float(code[i])] = float(nearest) if abs(nearest - value) < 3 else float(value)
    return scale, positions, score


def affine_matrix(p) -> np.ndarray:
    cx, cy, alpha, q, s = p
    rot = np.array([[math.cos(alpha), -math.sin(alpha)], [math.sin(alpha), math.cos(alpha)]])
    a = s * rot @ np.diag([1.0, q]) @ rot.T
    return np.array([[a[0, 0], a[0, 1], cx], [a[1, 0], a[1, 1], cy], [0.0, 0.0, 1.0]])


def _affine_radius(p, points):
    cx, cy, alpha, q, s = p
    rot = np.array([[math.cos(alpha), -math.sin(alpha)], [math.sin(alpha), math.cos(alpha)]])
    inverse = np.linalg.inv(s * rot @ np.diag([1.0, q]) @ rot.T)
    u = (points - [cx, cy]) @ inverse.T
    return np.hypot(u[:, 0], u[:, 1])


def fit_affine(points, radii, seed):
    result = least_squares(
        lambda p: _affine_radius(p, points) - radii, seed, loss="soft_l1", f_scale=0.1,
        x_scale=[10, 10, 0.1, 0.05, 1], bounds=([-1e6, -1e6, -math.pi, 0.4, 1e-3], [1e6, 1e6, math.pi, 1.0, 1e4]),
    )
    return result.x, float(np.sqrt(np.mean(result.fun ** 2)))


def measure_rings(signal, matrix, radii, window_mm, dr=0.02, dtheta_deg=0.5):
    theta = np.radians(np.arange(0, 360, dtheta_deg))
    out = []
    for expected in radii:
        radius = np.arange(expected - window_mm - 1.2, expected + window_mm + 1.2, dr)
        polar = sample_polar(signal, matrix, radius, theta).astype(np.float32)
        inside = polar > 0
        polar = cv2.GaussianBlur(polar, (0, 0), sigmaX=1.5, sigmaY=0.13 / dr)
        background = cv2.GaussianBlur(polar, (0, 0), sigmaX=0.01, sigmaY=0.7 / dr)
        flat = polar / (background + 10.0)
        kernel = _ring_kernel(dr)
        response = cv2.filter2D(flat, cv2.CV_32F, kernel[::-1][:, None].astype(np.float32), borderType=cv2.BORDER_REPLICATE)
        i0 = int(1.2 / dr)
        sub, rr = -response[i0:-i0], radius[i0:-i0]
        p = np.argmin(sub, axis=0)
        columns = np.arange(theta.size)
        depth = -sub[p, columns]
        level = background[i0:-i0].mean(axis=0)
        ok = (depth > 0.03) & (level > 25) & (p > 0) & (p < rr.size - 1) & (inside[i0:-i0].mean(axis=0) > 0.98)
        r = rr[p].astype(float)
        left, mid, right = sub[np.clip(p - 1, 0, None), columns], sub[p, columns], sub[np.clip(p + 1, None, rr.size - 1), columns]
        denominator = left - 2 * mid + right
        r = r + dr * np.clip(0.5 * (left - right) / np.where(np.abs(denominator) > 1e-9, denominator, 1), -1, 1)
        masked = np.where(ok, r, np.nan)
        window = int(30 / dtheta_deg)
        local = np.full(theta.size, np.nan)
        for i in columns:
            block = np.take(masked, range(i - window, i + window + 1), mode="wrap")
            if np.isfinite(block).sum() > 5:
                local[i] = np.nanmedian(block)
        keep = ok & np.isfinite(local) & (np.abs(r - local) < 0.12)
        if keep.sum() < 24:
            continue
        t, rk = theta[keep], r[keep]
        source = disc_to_source(matrix, np.column_stack([rk * np.cos(t), rk * np.sin(t)]))
        out.append((float(expected), source, rk, t))
    return out


def _coverage_deg(measured) -> float:
    angles = np.concatenate([t for _, _, _, t in measured]) if measured else np.array([])
    if angles.size == 0:
        return 0.0
    bins = np.zeros(72, bool)
    bins[(np.degrees(angles) / 5).astype(int) % 72] = True
    return float(bins.sum() * 5)


def fringe_frequency(signal, matrix, bands, dr=0.02, dtheta_deg=0.25, block_deg=10.0):
    theta = np.radians(np.arange(0, 360, dtheta_deg))
    grid = np.arange(1.0, 7.0, 0.004)
    block = int(block_deg / dtheta_deg)
    out = []
    for lo, hi in bands:
        radius = np.arange(lo, hi, dr)
        if radius.size < 40:
            continue
        polar = sample_polar(signal, matrix, radius, theta).astype(np.float32)
        inside = polar > 0
        background = cv2.GaussianBlur(polar, (0, 0), sigmaX=3, sigmaY=0.5 / dr)
        ok = inside & (background > 18) & (polar < 250)
        columns = np.nonzero(ok.mean(axis=0) > 0.95)[0]
        if columns.size < block:
            continue
        hp = np.where(ok, polar / (background + 5.0) - 1.0, 0.0).astype(np.float64)
        basis = np.exp(-2j * math.pi * np.outer(grid, radius)) * np.hanning(radius.size)
        spectrum = np.zeros(grid.size)
        for start in range(0, columns.size - block + 1, block):
            spectrum += np.abs(basis @ hp[:, columns[start:start + block]].sum(axis=1)) ** 2
        k = int(np.argmax(spectrum))
        peak = grid[k]
        if 0 < k < grid.size - 1:
            a, b, c = spectrum[k - 1:k + 2]
            curvature = a - 2 * b + c
            if curvature < 0:
                peak += 0.004 * 0.5 * (a - c) / curvature
        snr = float(spectrum[k] / (np.median(spectrum) + 1e-9))
        out.append(((lo + hi) / 2, float(peak), snr, int(columns.size)))
    return out


def fringe_handedness(signal, matrix, bands, table: PhaseTable, dr=0.02, dtheta_deg=0.25, block_deg=6.0):
    theta = np.radians(np.arange(0, 360, dtheta_deg))
    block = int(block_deg / dtheta_deg)
    step = math.radians(block_deg)
    slopes = []
    for lo, hi in bands:
        radius = np.arange(lo, hi, dr)
        if radius.size < 40:
            continue
        polar = sample_polar(signal, matrix, radius, theta).astype(np.float32)
        background = cv2.GaussianBlur(polar, (0, 0), sigmaX=3, sigmaY=0.5 / dr)
        ok = (polar > 0) & (background > 18) & (polar < 250)
        hp = np.where(ok, polar / (background + 5.0) - 1.0, 0.0).astype(np.float64)
        basis = np.exp(-1j * table.phase(radius)) * np.hanning(radius.size)
        blocks = []
        for start in range(0, theta.size - block + 1, block):
            usable = ok[:, start:start + block].mean() > 0.95
            blocks.append((basis * hp[:, start:start + block].sum(axis=1)).sum() if usable else np.nan)
        blocks = np.array(blocks)
        amplitude = np.abs(blocks)
        strong = np.isfinite(blocks) & (amplitude > np.nanpercentile(amplitude, 50))
        pair = blocks[1:] * np.conj(blocks[:-1])
        use = strong[1:] & strong[:-1]
        if use.sum() > 3:
            slopes.append(float(np.angle(pair[use].sum()) / (2 * math.pi) / step))
    return slopes


def _lane_white(signal, matrix, ring_radii, theta):
    lanes = sample_polar(signal, matrix, np.concatenate([ring_radii - 0.33, ring_radii + 0.33]), theta)
    lanes = cv2.GaussianBlur(lanes.astype(np.float32), (0, 0), sigmaX=4, sigmaY=0.01)
    lanes[lanes <= 0] = np.nan
    with np.errstate(all="ignore"):
        return np.nanmax(lanes, axis=0)


def decode_layer(signal, matrix, level, ring_radii, white, theta, phase_at_middle, start_angle, step_deg=0.2):
    middle = (level.inner_mm + level.outer_mm) / 2
    ring = min(ring_radii, key=lambda value: abs(value - middle))
    polar = sample_polar(signal, matrix, np.array([middle, ring]), theta)
    inside = polar[0] > 0
    polar = cv2.GaussianBlur(polar.astype(np.float32), (0, 0), sigmaX=4, sigmaY=0.01)
    value = (polar[0] - polar[1]) / np.maximum(white - polar[1], 8.0)
    value[~inside | ~np.isfinite(value)] = np.nan
    bits = np.asarray(level.bits) if level.bits else (np.arange(level.sectors) % 2 == 0).astype(int)
    ideal = 1.0 - 2.0 * bits
    base = theta - phase_at_middle - start_angle
    finite = np.isfinite(value)
    centered = np.where(finite, value - 0.5, 0.0)
    best = (1.0, 0.0, 0)
    for rotation in np.radians(np.arange(0, 360, step_deg)):
        source = np.mod(base - rotation, 2 * math.pi) / (2 * math.pi) * level.sectors
        sector = source.astype(int) % level.sectors
        fraction = source - np.floor(source)
        middle_part = (fraction > 0.2) & (fraction < 0.8) & finite
        sums = np.bincount(sector[middle_part], weights=value[middle_part], minlength=level.sectors)
        counts = np.bincount(sector[middle_part], minlength=level.sectors)
        visible = counts >= 3
        if visible.sum() < max(3, level.window + 1):
            continue
        decoded = sums[visible] / counts[visible] < 0.5
        ber = float(np.mean(decoded != (bits[visible] == 1)))
        score = float(np.sum(centered[finite] * ideal[sector[finite]])) / max(finite.sum(), 1)
        if (ber, -score) < (best[0], -best[3] if len(best) > 3 else 0.0):
            best = (ber, float(rotation), int(visible.sum()), score)
    if best[2] == 0:
        return best[:3]
    fine = np.radians(np.arange(-1.5 * step_deg * 5, 1.5 * step_deg * 5, 0.02)) + best[1]
    scores = []
    for rotation in fine:
        source = np.mod(base - rotation, 2 * math.pi) / (2 * math.pi) * level.sectors
        sector = source.astype(int) % level.sectors
        scores.append(float(np.sum(centered[finite] * ideal[sector[finite]])))
    k = int(np.argmax(scores))
    return best[0], float(fine[k]), best[2]


def measure_edges(signal, matrix, level, rotation, table: PhaseTable, start_angle, dr=0.05, dtheta_deg=0.1):
    bits = np.asarray(level.bits) if level.bits else (np.arange(level.sectors) % 2 == 0).astype(int)
    edges = [(k, bits[k] - bits[k - 1]) for k in range(level.sectors) if bits[k] != bits[k - 1]]
    sector = 2 * math.pi / level.sectors
    margin = 0.25 * (level.outer_mm - level.inner_mm)
    radius = np.arange(level.inner_mm + margin, level.outer_mm - margin, dr)
    if radius.size < 3:
        return []
    half = sector / 3
    theta = np.arange(-half, half, math.radians(dtheta_deg))
    out = []
    for k, polarity in edges:
        source = start_angle + k * sector
        for r in radius:
            predicted = source + rotation + table.phase(r)
            row = sample_polar(signal, matrix, np.array([r]), predicted + theta)[0]
            if (row <= 0).any() or row.size < 8:
                continue
            smooth = gaussian_filter1d(row, 2.0)
            gradient = np.gradient(smooth) * (-polarity)
            i = int(np.argmax(gradient))
            contrast = gradient[i] / (np.median(np.abs(gradient)) + 1e-6)
            if 0 < i < gradient.size - 1 and contrast > 4:
                a, b, c = gradient[i - 1:i + 2]
                shift = 0.5 * (a - c) / (a - 2 * b + c) if (a - 2 * b + c) != 0 else 0.0
                out.append((float(r), float(predicted + theta[i] + shift * math.radians(dtheta_deg)), float(source)))
    return out


def track_edges(signal, matrix, level, rotation, start_angle, dr=0.02, dtheta_deg=0.1):
    bits = np.asarray(level.bits) if level.bits else (np.arange(level.sectors) % 2 == 0).astype(int)
    edges = [(k, bits[k] - bits[k - 1]) for k in range(level.sectors) if bits[k] != bits[k - 1]]
    sector = 2 * math.pi / level.sectors
    margin = 0.15 * (level.outer_mm - level.inner_mm)
    middle = (level.inner_mm + level.outer_mm) / 2
    outward = np.arange(middle, level.outer_mm - margin, dr)
    inward = np.arange(middle - dr, level.inner_mm + margin, -dr)
    half = sector / 4
    window = np.arange(-half, half, math.radians(dtheta_deg))
    out = []
    for k, polarity in edges:
        source = start_angle + k * sector
        for rows in (outward, inward):
            predicted = source + rotation
            history = []
            for r in rows:
                if len(history) >= 3:
                    recent = np.array(history[-5:])
                    slope = np.median(np.diff(recent[:, 1]) / np.diff(recent[:, 0]))
                    predicted = history[-1][1] + slope * (r - history[-1][0])
                row = sample_polar(signal, matrix, np.array([r]), predicted + window)[0]
                if (row <= 0).any() or row.size < 8:
                    break
                smooth = gaussian_filter1d(row, 2.0)
                gradient = np.gradient(smooth) * (-polarity)
                i = int(np.argmax(gradient))
                contrast = gradient[i] / (np.median(np.abs(gradient)) + 1e-6)
                if not (0 < i < gradient.size - 1 and contrast > 4):
                    break
                a, b, c = gradient[i - 1:i + 2]
                shift = 0.5 * (a - c) / (a - 2 * b + c) if (a - 2 * b + c) != 0 else 0.0
                found = predicted + window[i] + shift * math.radians(dtheta_deg)
                out.append((float(r), float(found), float(source)))
                history.append((r, found))
                predicted = found
    return out


def band_rotation_profile(signal, matrix, level, rotation0, white, theta, start_angle, dr=0.03):
    bits = np.asarray(level.bits) if level.bits else (np.arange(level.sectors) % 2 == 0).astype(int)
    ideal = 1.0 - 2.0 * bits
    ring = min([lv for lv in (level.inner_mm, level.outer_mm)], key=lambda v: abs(v - (level.inner_mm + level.outer_mm) / 2))
    margin = 0.15 * (level.outer_mm - level.inner_mm)
    middle = (level.inner_mm + level.outer_mm) / 2
    radii = np.arange(level.inner_mm + margin, level.outer_mm - margin, dr)
    order = np.argsort(np.abs(radii - middle))
    half = 2 * math.pi / level.sectors / 3
    found = {}
    for j in order:
        r = radii[j]
        polar = sample_polar(signal, matrix, np.array([r]), theta)[0]
        inside = polar > 0
        value = np.where(inside, polar, np.nan)
        black = np.nanpercentile(value, 10)
        normalized = (value - black) / np.maximum(white - black, 8.0) - 0.5
        finite = np.isfinite(normalized)
        if finite.sum() < 50:
            continue
        centered = np.where(finite, normalized, 0.0)
        base = theta - start_angle
        side = [k for k in found if (radii[k] - middle) * (r - middle) >= 0 or abs(radii[k] - middle) < dr]
        side.sort(key=lambda k: abs(radii[k] - r))
        if len(side) >= 2:
            k1, k2 = side[0], side[1]
            slope = (found[k1] - found[k2]) / (radii[k1] - radii[k2]) if radii[k1] != radii[k2] else 0.0
            guess = found[k1] + slope * (r - radii[k1])
        elif side:
            guess = found[side[0]]
        else:
            guess = rotation0
        grid = guess + np.arange(-half, half, math.radians(0.05))
        scores = []
        for rotation in grid:
            sector = (np.mod(base - rotation, 2 * math.pi) / (2 * math.pi) * level.sectors).astype(int) % level.sectors
            scores.append(float(np.sum(centered[finite] * ideal[sector[finite]])))
        k = int(np.argmax(scores))
        if scores[k] <= 0:
            continue
        found[j] = float(grid[k])
    if len(found) < 6:
        return None
    r = np.array([radii[j] for j in sorted(found)])
    rot = np.array([found[j] for j in sorted(found)])
    return r, rot


def edge_slopes(edges):
    if len(edges) < 6:
        return None
    by_source = {}
    for r, theta, source in edges:
        by_source.setdefault(source, []).append((r, theta))
    xs, ys = [], []
    for points in by_source.values():
        if len(points) < 3:
            continue
        arr = np.array(points)
        xs.append(arr[:, 0] - arr[:, 0].mean())
        ys.append(np.unwrap(arr[:, 1]) - np.unwrap(arr[:, 1]).mean())
    if not xs:
        return None
    x, y = np.concatenate(xs), np.concatenate(ys)
    slope = float(np.sum(x * y) / max(np.sum(x * x), 1e-9))
    residual = y - slope * x
    keep = np.abs(residual) < 3 * (1.4826 * np.median(np.abs(residual)) + 1e-3)
    if keep.sum() >= 6:
        slope = float(np.sum(x[keep] * y[keep]) / max(np.sum(x[keep] ** 2), 1e-9))
    return slope, int(keep.sum())


def _wrap(angle):
    return (angle + math.pi) % (2 * math.pi) - math.pi


def fit_from_frequency(generated: Params, samples, sign=1):
    r = np.array([s[0] for s in samples])
    f = np.array([s[1] for s in samples])
    w = np.sqrt(np.array([s[2] for s in samples]))
    gen = Geometry(generated)

    def model(p):
        actual = Params(generated.kind, p[0], p[1], generated.sectors, generated.linear_density)
        try:
            table = PhaseTable(gen, Geometry(actual), generated.inner + 0.3, generated.outer - 0.3)
        except Exception:
            return np.full(r.size, 1e3)
        return sign * table.rate(r) / (2 * math.pi)

    best = None
    for di in (-0.3, -0.1, -0.03, 0.03, 0.1, 0.3):
        for do in (-0.5, -0.15, -0.05, 0.05, 0.15, 0.5):
            try:
                result = least_squares(lambda p: (model(p) - f) * w, [generated.inner + di, generated.outer + do],
                                       x_scale=0.01, diff_step=1e-5, max_nfev=80)
            except Exception:
                continue
            if best is None or result.cost < best.cost:
                best = result
    params = Params(generated.kind, float(best.x[0]), float(best.x[1]), generated.sectors, generated.linear_density)
    return params, float(np.sqrt(np.mean((model(best.x) - f) ** 2)))


def turns_of(generated: Params, actual: Params) -> float:
    table = PhaseTable(Geometry(generated), Geometry(actual), generated.inner + 0.3, generated.outer - 0.3)
    return float((table.phase(generated.outer - 0.3) - table.phase(generated.inner + 0.3)) / (2 * math.pi))


def _strong_direction(generated: Params):
    base = generated
    eps = 0.001
    ti = turns_of(base, Params(base.kind, base.inner + eps, base.outer, base.sectors, base.linear_density)) / eps
    to = turns_of(base, Params(base.kind, base.inner, base.outer + eps, base.sectors, base.linear_density)) / eps
    norm = math.hypot(ti, to)
    return np.array([ti, to]) / norm, norm


def grid_phase_search(generated: Params, groups, sign: int, center: Params, half_turns: float, step_turns: float):
    _, turns_per_mm = _strong_direction(generated)
    step_mm = step_turns / turns_per_mm * math.sqrt(2)
    offsets = np.arange(-half_turns, half_turns + 1e-9, step_turns) / turns_per_mm * math.sqrt(2)
    gen = Geometry(generated)
    best = None
    for a in offsets:
        for c in offsets:
            inner = center.inner + a
            outer = center.outer + c
            actual = Params(generated.kind, inner, outer, generated.sectors, generated.linear_density)
            try:
                model = CrossModel(gen, Geometry(actual))
            except Exception:
                continue
            cost = 0.0
            for _, _, phases, _ in groups:
                r = np.array([v[0] for v in phases])
                delta = np.array([v[1] for v in phases])
                phase, _ = model.phase(r)
                if not np.isfinite(phase).all():
                    cost = math.inf
                    break
                residual = delta - sign * phase
                rotation = np.angle(np.exp(1j * residual).mean())
                cost += float(np.sum((_wrap(residual - rotation) * r) ** 2))
            if best is None or cost < best[1]:
                best = (actual, cost)
    return best


def fit_phase(generated: Params, seed: Params, photos, cubic=False, sign=1):
    gen = Geometry(generated)
    groups = [g for g, _, _, _ in photos]
    rows = []
    for g, _, phases, edges in photos:
        for r, delta in phases:
            rows.append((groups.index(g), r, delta, 0.2))
        for r, delta in edges:
            rows.append((groups.index(g), r, delta, 0.3))
    rows = np.array(rows, float)
    index, radius, delta, sigma = rows[:, 0].astype(int), rows[:, 1], rows[:, 2], rows[:, 3]
    n_rot = len(groups)

    def unpack(p):
        inner, outer = p[0], p[1]
        pitch = tuple(p[2:5]) if cubic else (0.0, 0.0, 0.0)
        return Params(generated.kind, inner, outer, generated.sectors, generated.linear_density, *pitch), p[-n_rot:]

    def residual(p):
        actual, rotations = unpack(p)
        try:
            actual.validate()
            table = PhaseTable(gen, Geometry(actual), generated.inner + 0.3, generated.outer - 0.3)
        except Exception:
            return np.full(radius.size, 1e3)
        predicted = sign * table.phase(radius) + rotations[index]
        return _wrap(delta - predicted) * radius / sigma

    start = [seed.inner, seed.outer] + ([seed.pitch_linear, seed.pitch_quadratic, seed.pitch_cubic] if cubic else [])
    seed_table = PhaseTable(gen, Geometry(seed), generated.inner + 0.3, generated.outer - 0.3)
    for g in range(n_rot):
        mine = index == g
        unit = np.exp(1j * (delta[mine] - sign * seed_table.phase(radius[mine])))
        start.append(float(np.angle(unit.mean())) if mine.any() else 0.0)
    lower = [generated.inner - 0.6, generated.outer - 1.2] + ([-0.5] * 3 if cubic else []) + [-np.inf] * n_rot
    upper = [generated.inner + 0.6, generated.outer + 1.2] + ([0.5] * 3 if cubic else []) + [np.inf] * n_rot
    start = np.clip(start, np.array(lower) + 1e-6, np.array(upper) - 1e-6)
    result = least_squares(residual, start, loss="soft_l1", f_scale=1.0, bounds=(lower, upper), x_scale="jac",
                           diff_step=1e-5, max_nfev=300)
    actual, rotations = unpack(result.x)
    try:
        actual.validate()
        final = _wrap(delta - (sign * PhaseTable(gen, Geometry(actual), generated.inner + 0.3, generated.outer - 0.3).phase(radius) + rotations[index])) * radius
    except Exception:
        return actual, [float(v) for v in rotations], float("inf"), float("inf")
    return actual, [float(v) for v in rotations], float(np.sqrt(np.mean(final ** 2))), float(np.max(np.abs(final)))


@dataclass
class CodePhoto:
    path: str
    matrix: np.ndarray | None = None
    scale_px_per_mm: float = 0.0
    rings: dict = field(default_factory=dict)
    coverage_deg: float = 0.0
    ring_rms_mm: float = 0.0
    model: str = "none"
    fringe: list = field(default_factory=list)
    handedness: list = field(default_factory=list)
    layers: dict = field(default_factory=dict)
    phase_samples: list = field(default_factory=list)
    slope_samples: list = field(default_factory=list)
    row_samples: list = field(default_factory=list)
    edges: list = field(default_factory=list)
    rotation: float = 0.0
    notes: list = field(default_factory=list)


def locate_rings(signal, spec: PatternSpec, center=None) -> CodePhoto | None:
    code_radii = spec.solid_ring_radii()
    center = center_seed(signal) if center is None else np.asarray(center, float)
    wedges = [(a, a + WEDGE_DEG) for a in np.arange(0, 360, WEDGE_DEG)]

    def work(wedge):
        radius, flat = wedge_profile(signal, center, *wedge)
        candidates, envelope = ring_candidates(radius, flat)
        identified = identify_rings(candidates, code_radii, radius, envelope)
        return wedge, identified

    results = [(wedge, identified) for wedge, identified in parallel_map(work, wedges) if identified is not None]
    photo = CodePhoto(path="")
    if len(results) < 3:
        photo.notes.append(f"Ring barcode not recognized: rings found in only {len(results)} wedges.")
        return None
    scales = np.array([identified[0] for _, identified in results])
    scores = np.array([identified[2] for _, identified in results])
    best_scale, best_support = None, -1.0
    for candidate in scales:
        close = np.abs(np.log(scales / candidate)) < math.log(1.15)
        support = float(scores[close].sum())
        if support > best_support:
            best_scale, best_support = float(np.exp(np.mean(np.log(scales[close])))), support
    keep = np.abs(np.log(scales / best_scale)) < math.log(1.15)
    labeled = []
    for ((lo, hi), (scale, mapping, _)), ok in zip(results, keep):
        if not ok:
            continue
        angle = math.radians((lo + hi) / 2)
        for r_mm, rho in mapping.items():
            labeled.append((center[0] + rho * math.cos(angle), center[1] + rho * math.sin(angle), r_mm))
    if len(labeled) < 9 or keep.sum() < 3:
        photo.notes.append(f"Ring barcode not recognized: {int(keep.sum())} consistent wedges, {len(labeled)} ring points.")
        return None
    labeled = np.array(labeled)
    scale = best_scale
    p, rms = fit_affine(labeled[:, :2], labeled[:, 2], [center[0], center[1], 0.0, 0.95, scale])
    matrix = affine_matrix(p)
    photo.model = "affine"
    wedge_angles = np.array([(lo + hi) / 2 for ((lo, hi), _), ok in zip(results, keep) if ok])
    wide = wedge_angles.size >= 8 and (np.ptp(np.sort(wedge_angles)) >= 180)

    def upgrade(current, circles, current_rms, measured):
        result = refine_with_circles(current, circles)
        if not result.succeeded:
            return current, current_rms, measured, False
        candidate = np.asarray(result.matrix, float).reshape(3, 3)
        check = measure_rings(signal, candidate, code_radii, 0.5)
        if len(check) < 3:
            return current, current_rms, measured, False
        rms_h = float(np.sqrt(np.mean(np.concatenate([(rk - r) ** 2 for r, _, rk, _ in check]))))
        if rms_h < current_rms:
            return candidate, rms_h, check, True
        return current, current_rms, measured, False

    if wide:
        circles = {}
        for x, y, r_mm in labeled:
            circles.setdefault(float(r_mm), []).append((x, y))
        matrix, rms, _, ok = upgrade(matrix, [(r, np.array(pts)) for r, pts in circles.items() if len(pts) >= 4], 1e9, [])
        if ok:
            photo.model = "homography"
    measured = []
    for window in (1.5, 0.8, 0.5, 0.3):
        measured = measure_rings(signal, matrix, code_radii, window)
        measured = [m for m in measured if abs(float(np.median(m[2])) - m[0]) < 0.5 * window + 0.05]
        if len(measured) < 3:
            photo.notes.append("Fewer than 3 rings available for refinement.")
            return None
        points = np.concatenate([s for _, s, _, _ in measured])
        radii = np.concatenate([np.full(len(s), r) for r, s, _, _ in measured])
        if photo.model == "homography":
            matrix, rms, measured, _ = upgrade(matrix, [(r, s) for r, s, _, _ in measured], 1e9, measured)
        else:
            p, rms = fit_affine(points, radii, p)
            matrix = affine_matrix(p)
    coverage = _coverage_deg(measured)
    if photo.model == "affine" and coverage >= 180 and len(measured) >= 4:
        for _ in range(3):
            matrix, rms, measured, ok = upgrade(matrix, [(r, s) for r, s, _, _ in measured], rms, measured)
            if not ok:
                break
            photo.model = "homography"
    photo.matrix = matrix
    photo.scale_px_per_mm = float(p[4])
    photo.rings = {float(r): int(len(s)) for r, s, _, _ in measured}
    photo.coverage_deg = coverage
    photo.ring_rms_mm = rms
    return photo


def analyze_photo(path, generated: Params, spec: PatternSpec) -> CodePhoto:
    image = load_photo(path)
    signal = signal_image(image)
    photo = locate_rings(signal, spec)
    if photo is None:
        failed = CodePhoto(path=str(path))
        failed.notes.append("Could not locate the disc from the ring barcode.")
        return failed
    photo.path = str(path)
    coarse = [(lv.inner_mm + 0.2, lv.outer_mm - 0.2) for lv in spec.levels if lv.sectors == 2 and lv.outer_mm - lv.inner_mm >= 1.2]
    photo.fringe = fringe_frequency(signal, photo.matrix, coarse)
    return photo, signal


def _layers_and_edges(photo: CodePhoto, signal, generated: Params, actual: Params, spec: PatternSpec):
    gen = Geometry(generated)
    table = PhaseTable(gen, Geometry(actual), generated.inner + 0.3, generated.outer - 0.3)
    theta = np.radians(np.arange(0, 360, 0.1))
    rings = spec.solid_ring_radii()
    white = _lane_white(signal, photo.matrix, rings, theta)
    start = math.radians(spec.half_disc_start_deg)
    photo.layers, photo.phase_samples, photo.edges, photo.slope_samples, photo.row_samples = {}, [], [], [], []
    rotations = []
    for level in spec.levels:
        if level.sectors < 4 or level.outer_mm - level.inner_mm < 0.8:
            continue
        middle = (level.inner_mm + level.outer_mm) / 2
        ber, rotation, visible = decode_layer(signal, photo.matrix, level, rings, white, theta, float(table.phase(middle)), start)
        photo.layers[f"{level.sectors}@{middle:.1f}"] = dict(ber=round(ber, 3), rotation_deg=round(math.degrees(rotation), 2), visible=visible)
        if ber <= READABLE_BER:
            photo.phase_samples.append((middle, rotation + float(table.phase(middle))))
            rotations.append(rotation)
            profile = band_rotation_profile(signal, photo.matrix, level, rotation, white, theta, start)
            if profile is not None:
                r_rows, rot_rows = profile
                pairs = [(rot_rows[b] - rot_rows[a]) / (r_rows[b] - r_rows[a])
                         for a in range(len(r_rows)) for b in range(a + 1, len(r_rows)) if r_rows[b] - r_rows[a] > 0.3]
                if pairs:
                    slope = float(np.median(pairs))
                    residual = rot_rows - rot_rows.mean() - slope * (r_rows - r_rows.mean())
                    keep = np.abs(residual) < math.radians(1.5)
                    if keep.sum() >= 6:
                        photo.row_samples.extend((float(r), float(v)) for r, v in zip(r_rows[keep], rot_rows[keep]))
                        photo.slope_samples.append((middle, slope / (2 * math.pi), int(keep.sum())))
                        photo.layers[f"{level.sectors}@{middle:.1f}"]["slopeTurnsPerMm"] = round(slope / (2 * math.pi), 4)
    if rotations:
        unit = np.exp(1j * np.array(rotations))
        photo.rotation = float(np.angle(unit.mean()))
    return photo


def _measure_photo_edges(photo: CodePhoto, signal, generated: Params, actual: Params, spec: PatternSpec, sign: int):
    table = PhaseTable(Geometry(generated), Geometry(actual), generated.inner + 0.3, generated.outer - 0.3)
    start = math.radians(spec.half_disc_start_deg)
    photo.edges = []

    class _Signed:
        def phase(self, r):
            return sign * table.phase(r)

    for level in spec.levels:
        key = f"{level.sectors}@{(level.inner_mm + level.outer_mm) / 2:.1f}"
        if key in photo.layers and photo.layers[key]["ber"] <= READABLE_BER:
            photo.edges.extend(measure_edges(signal, photo.matrix, level, photo.rotation, _Signed(), start))


@dataclass
class CodeResult:
    quality: str
    actual: Params | None
    turns: float
    sign: int
    fringe_rms: float
    phase_rms_mm: float | None
    photos: list
    notes: list

    def to_json(self) -> str:
        node = dict(
            quality=self.quality, turns=self.turns, sign=self.sign, fringeRms=self.fringe_rms,
            phaseRmsMm=self.phase_rms_mm, notes=self.notes,
            actual=None if self.actual is None else self.actual.__dict__,
            photos=[dict(path=p.path, model=p.model, scalePxPerMm=p.scale_px_per_mm, rings=p.rings,
                         coverageDeg=p.coverage_deg, ringRmsMm=p.ring_rms_mm,
                         fringe=[dict(r=r, f=f, snr=s, columns=c) for r, f, s, c in p.fringe],
                         handedness=p.handedness, layers=p.layers, rotationDeg=math.degrees(p.rotation),
                         phaseSamples=len(p.phase_samples), edgeSamples=len(p.row_samples), notes=p.notes) for p in self.photos],
        )
        return json.dumps(node, ensure_ascii=False, indent=2, default=float)


def decode(paths, generated: Params, spec: PatternSpec | None = None, cubic=False) -> CodeResult:
    spec = spec or design_code(generated.inner, generated.outer)
    from .code_real import decode_real

    real = decode_real(paths, generated, spec, cubic=cubic)
    if real is not None:
        return real
    analyzed = parallel_map(lambda p: analyze_photo(p, generated, spec), list(paths))
    photos = [a[0] if isinstance(a, tuple) else a for a in analyzed]
    signals = {id(a[0]): a[1] for a in analyzed if isinstance(a, tuple)}
    usable = [p for p in photos if p.matrix is not None]
    notes = []
    if not usable:
        return CodeResult("poor", None, 0.0, 0, 0.0, None, photos, ["No photo located the ring barcode."])
    gen = Geometry(generated)
    samples = [(r, f, min(snr, 50.0)) for p in usable for r, f, snr, _ in p.fringe if snr > 6 and 1.1 < f < 6.9]
    fringe_rms = 0.0
    sign = 1
    if len(samples) >= 3:
        coarse, fringe_rms = fit_from_frequency(generated, samples)
        table = PhaseTable(gen, Geometry(coarse), generated.inner + 0.3, generated.outer - 0.3)
        coarse_bands = [(lv.inner_mm + 0.2, lv.outer_mm - 0.2) for lv in spec.levels if lv.sectors == 2 and lv.outer_mm - lv.inner_mm >= 1.2]
        votes = []
        for p in usable:
            p.handedness = fringe_handedness(signals[id(p)], p.matrix, coarse_bands, table)
            votes.extend(np.sign(p.handedness))
        sign = int(np.sign(np.sum(votes))) or 1
        if sign < 0:
            coarse, _ = fit_from_frequency(generated, samples, sign=-1)
            notes.append("Handedness vote points the opposite way: parameters come from the mirrored solution.")
    else:
        coarse = generated
        notes.append("Fringes are too wide for the frequency method: the parameter error should be under 1 turn/mm, so using the angle code directly.")
    table = PhaseTable(gen, Geometry(coarse), generated.inner + 0.3, generated.outer - 0.3)
    turns = float(sign * (table.phase(generated.outer - 0.3) - table.phase(generated.inner + 0.3)) / (2 * math.pi))
    readable = 0
    for p in usable:
        _layers_and_edges(p, signals[id(p)], generated, coarse, spec)
        readable += len(p.phase_samples)
    if readable < 2:
        if len(samples) < 3:
            return CodeResult("poor", None, 0.0, 0, 0.0, None, photos, notes + ["Neither fringe frequency nor angle code could be read."])
        quality = "fair" if abs(turns) < 150 else "poor"
        notes.append("Fewer than 2 readable angle-code layers, using fringe frequency only; the parameter error is known only to a few turns.")
        return CodeResult(quality, coarse, turns, sign, fringe_rms, None, photos, notes)
    def search(groups, seeds, signs):
        found = None
        for candidate_sign in signs:
            for seed in seeds:
                try:
                    fitted = fit_phase(generated, seed, groups, cubic=False, sign=candidate_sign)
                except Exception:
                    continue
                if math.isfinite(fitted[2]) and (found is None or fitted[2] < found[0][2]):
                    found = (fitted, candidate_sign)
        return found

    slopes = [(r, f, min(n, 50.0)) for p in usable for r, f, n in p.slope_samples if n >= 6]
    if len(slopes) >= 2:
        signed, slope_rms = fit_from_frequency(generated, slopes, sign=1)
        turns_signed = turns_of(generated, signed)
        if len(samples) < 3 or abs(abs(turns_signed) - abs(turns)) < 3:
            coarse, sign = signed, 1
            turns = turns_signed
            notes.append(f"Edge slopes give a signed coarse solution: {turns:.2f} turns ({len(slopes)} code bands).")
    groups = [(i, p.rotation, p.phase_samples, []) for i, p in enumerate(usable) if p.phase_samples]
    signs = (sign, -sign) if (len(samples) >= 3 or len(slopes) >= 2) else (1, -1)
    seeds = []
    half_turns = 1.5 if len(slopes) >= 2 else 3.0
    for candidate_sign in signs:
        found = grid_phase_search(generated, groups, candidate_sign, coarse, half_turns, 0.02)
        if found is not None:
            seeds.append((found[0], candidate_sign))
    best = None
    for seed, candidate_sign in seeds:
        found = search(groups, [seed], (candidate_sign,))
        if found is not None and (best is None or found[0][2] < best[0][2]):
            best = found
    if best is None:
        return CodeResult("poor", coarse, turns, sign, fringe_rms, None, photos, notes + ["Absolute phase fit failed."])
    (stage_one, rotations, rms_one, _), sign = best
    for (i, *_), rot in zip(groups, rotations):
        usable[i].rotation = rot
    groups = [(i, p.rotation, p.phase_samples, p.row_samples) for i, p in enumerate(usable) if p.phase_samples]
    best = search(groups, [stage_one], (sign,))
    (actual, rotations, rms, worst), sign = best if best is not None else ((stage_one, rotations, rms_one, 0.0), sign)
    if rms > 1.0:
        notes.append(f"Absolute phase fit residual {rms:.2f} mm is not trustworthy: giving the coarse frequency solution only.")
        quality = "fair" if len(samples) >= 3 and abs(turns) < 150 else "poor"
        return CodeResult(quality, coarse if len(samples) >= 3 else None, turns, sign, fringe_rms, rms, photos, notes)
    if cubic:
        cubic_fit = fit_phase(generated, actual, groups, cubic=True, sign=sign)
        if cubic_fit[2] < 0.98 * rms:
            actual, rotations, rms, worst = cubic_fit
    for (i, *_), rot in zip(groups, rotations):
        usable[i].rotation = rot
    final = PhaseTable(gen, Geometry(actual), generated.inner + 0.3, generated.outer - 0.3)
    turns = float(sign * (final.phase(generated.outer - 0.3) - final.phase(generated.inner + 0.3)) / (2 * math.pi))
    edges = sum(len(p.row_samples) for p in usable)
    quality = "good" if (readable >= 2 and rms < 0.6 and edges >= 20) else "fair"
    notes.append(f"{readable} readable code bands, {edges} per-row phase samples, phase residual rms {rms:.3f} mm.")
    return CodeResult(quality, actual, turns, sign, fringe_rms, rms, photos, notes)
