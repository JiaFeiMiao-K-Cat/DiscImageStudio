from __future__ import annotations

import math
from dataclasses import dataclass, field

import cv2
import numpy as np
from scipy.ndimage import gaussian_filter1d, median_filter

from .rectify import disc_to_source

RING_RADII_PX = np.array([490.0, 570, 650, 730, 810, 890, 970, 1050, 1130])
LINE_WIDTH_PX = 10.0
PATTERN_SIZE = 2400


def pattern_scale(outer: float, kind: str) -> float:
    return outer / ((PATTERN_SIZE - 1) / 2.0 if kind == "Dvd" else PATTERN_SIZE / 2.0)


def ring_radii_mm(outer: float, kind: str) -> np.ndarray:
    return RING_RADII_PX * pattern_scale(outer, kind)


def line_width_mm(outer: float, kind: str) -> float:
    return LINE_WIDTH_PX * pattern_scale(outer, kind)


def signal_image(bgr: np.ndarray) -> np.ndarray:
    return bgr.max(axis=2).astype(np.float32)


def _remap(image, x, y, interpolation=cv2.INTER_CUBIC):
    return cv2.remap(
        image, x.astype(np.float32), y.astype(np.float32), interpolation,
        borderMode=cv2.BORDER_CONSTANT, borderValue=0.0,
    )


def sample_polar(image, matrix, radius, theta):
    rr, tt = np.meshgrid(radius, theta, indexing="ij")
    source = disc_to_source(matrix, np.stack([rr * np.cos(tt), rr * np.sin(tt)], axis=-1))
    return _remap(image, source[..., 0], source[..., 1])


def _valley_kernel(sigma: float) -> np.ndarray:
    half = max(int(math.ceil(4 * sigma)), 2)
    x = np.arange(-half, half + 1, dtype=np.float64)
    kernel = (1 - (x / sigma) ** 2) * np.exp(-0.5 * (x / sigma) ** 2)
    kernel -= kernel.mean()
    return -(kernel / np.sum(np.abs(kernel))).astype(np.float32)


@dataclass
class CommonPath:
    radius: np.ndarray
    theta: np.ndarray
    support: np.ndarray
    response: np.ndarray
    score: float


def common_phase_path(
    image, matrix, inner, outer, line_mm, dr=0.1, dtheta_deg=0.1, max_slope_deg_per_mm=5.0,
    smoothness=0.02, physical_outer=60.0,
) -> CommonPath:
    first = max(inner - 1.0, 5.0)
    last = min(outer + 0.5, physical_outer - 0.8)
    radius = np.arange(first, last, dr)
    columns = int(round(360 / dtheta_deg))
    theta = np.arange(columns) * 2 * math.pi / columns
    polar = sample_polar(image, matrix, radius, theta)
    background = cv2.GaussianBlur(polar, (0, 0), sigmaX=20, sigmaY=10)
    flat = polar / (background + 20.0)
    flat = cv2.GaussianBlur(flat, (0, 0), sigmaX=0.01, sigmaY=max(0.3 / dr, 1.0))
    saturated = polar > 250
    saturated = cv2.dilate(saturated.astype(np.uint8), np.ones((15, 15), np.uint8)) > 0

    response = np.zeros_like(flat)
    step_rad = 2 * math.pi / columns
    band = int(round(2.0 / dr))
    for start in range(0, radius.size, band):
        stop = min(start + band, radius.size)
        mid = radius[(start + stop) // 2]
        sigma = max(line_mm / 2.355 / (mid * step_rad), 1.0)
        kernel = _valley_kernel(sigma)
        pad = kernel.size // 2
        part = flat[start:stop]
        padded = np.concatenate([part[:, -pad:], part, part[:, :pad]], axis=1)
        filtered = cv2.filter2D(padded, cv2.CV_32F, kernel[None, :], borderType=cv2.BORDER_REFLECT)
        response[start:stop] = filtered[:, pad:-pad]
    response[saturated] = 0
    noise = 1.4826 * np.median(np.abs(response), axis=1, keepdims=True) + 1e-4
    score = np.clip(response / noise, 0, 12)
    score[score < 2] = 0

    period = columns // 4
    folded = score[:, :period] + score[:, period:2 * period] + score[:, 2 * period:3 * period] + score[:, 3 * period:4 * period]
    folded = gaussian_filter1d(folded, 0.3 / dtheta_deg, axis=1, mode="wrap")

    reach = max(int(round(max_slope_deg_per_mm * dr / dtheta_deg)), 1)
    moves = np.arange(-reach, reach + 1)
    penalty = smoothness * moves.astype(np.float64) ** 2
    rows = radius.size
    best = folded[0].astype(np.float64)
    back = np.zeros((rows, period), dtype=np.int16)
    for row in range(1, rows):
        stacked = np.stack([np.roll(best, int(move)) - cost for move, cost in zip(moves, penalty)])
        choice = np.argmax(stacked, axis=0)
        best = stacked[choice, np.arange(period)] + folded[row]
        back[row] = moves[choice]
    state = int(np.argmax(best))
    total = float(best[state])
    path = np.zeros(rows, dtype=np.int64)
    unwrapped = np.zeros(rows, dtype=np.float64)
    path[-1] = state
    unwrapped[-1] = state
    for row in range(rows - 1, 0, -1):
        move = int(back[row, path[row]])
        path[row - 1] = (path[row] - move) % period
        unwrapped[row - 1] = unwrapped[row] - move
    support = folded[np.arange(rows), path]
    theta_path = unwrapped * step_rad
    theta_path -= (2 * math.pi / 4) * math.floor(np.median(theta_path) / (math.pi / 2))
    return CommonPath(radius, theta_path, support, folded, total / rows)


@dataclass
class ArmTrace:
    arm: int
    radius: np.ndarray
    theta: np.ndarray
    confidence: np.ndarray
    strip: np.ndarray | None = None
    path_column: np.ndarray | None = None
    metrics: dict = field(default_factory=dict)

    def points(self, min_confidence=0.3, step_mm=0.25, r_max=59.9, max_gap_mm=6.0) -> np.ndarray:
        keep = (self.confidence >= min_confidence) & (self.radius <= r_max)
        keep &= ~_long_gap_mask(self.radius, self.confidence >= min_confidence, max_gap_mm)
        radius, theta, confidence = self.radius[keep], self.theta[keep], self.confidence[keep]
        if radius.size == 0:
            return np.zeros((0, 2))
        bins = np.floor((radius - radius[0]) / step_mm).astype(int)
        out = []
        for b in np.unique(bins):
            selection = bins == b
            weight = confidence[selection]
            r = float(np.average(radius[selection], weights=weight))
            t = float(np.average(theta[selection], weights=weight))
            out.append((r * math.cos(t), r * math.sin(t)))
        return np.array(out)


def _long_gap_mask(radius, good, max_gap_mm):
    mask = np.zeros(radius.shape, dtype=bool)
    start = None
    for i, value in enumerate(good):
        if not value and start is None:
            start = i
        if value and start is not None:
            if radius[i - 1] - radius[start] > max_gap_mm:
                mask[start:i] = True
            start = None
    return mask


def trace_arm(
    image, matrix, path: CommonPath, arm: int, line_mm, half_width_mm=1.5, dr=0.05, ds=0.02,
    smoothness=0.15, physical_outer=60.0, drawn_band: tuple[float, float] | None = None,
) -> ArmTrace:
    radius = np.arange(path.radius[0], path.radius[-1], dr)
    center = np.interp(radius, path.radius, gaussian_filter1d(path.theta, 5.0 / (path.radius[1] - path.radius[0]) / 10))
    center = center + arm * math.pi / 2
    s = np.arange(-half_width_mm, half_width_mm + ds, ds)
    rr, ss = np.meshgrid(radius, s, indexing="ij")
    theta = center[:, None] + ss / rr
    source = disc_to_source(matrix, np.stack([rr * np.cos(theta), rr * np.sin(theta)], axis=-1))
    strip = _remap(image, source[..., 0], source[..., 1])
    background = cv2.GaussianBlur(strip, (0, 0), sigmaX=0.8 / ds, sigmaY=1.0 / dr)
    level = cv2.GaussianBlur(strip, (0, 0), sigmaX=0.3 / ds, sigmaY=0.3 / dr)
    flat = strip / (background + 20.0)
    flat = cv2.GaussianBlur(flat, (0, 0), sigmaX=0.01, sigmaY=max(0.2 / dr, 1.0))
    kernel = _valley_kernel(line_mm / 2.355 / ds)
    response = cv2.filter2D(flat, cv2.CV_32F, kernel[None, :], borderType=cv2.BORDER_REFLECT)
    saturated = cv2.dilate((strip > 250).astype(np.uint8), np.ones((7, 7), np.uint8)) > 0
    response[saturated] = 0
    positive = np.maximum(response, 0)

    noise = 1.4826 * np.median(np.abs(response - np.median(response, axis=1, keepdims=True)), axis=1) + 1e-5
    contrast = positive.max(axis=1) / noise
    confidence = np.clip((contrast - 3) / 5, 0, 1)
    confidence = np.where(np.max(level, axis=1) < 45, 0.0, confidence)
    emission = positive / noise[:, None] * confidence[:, None]

    reach = 4
    moves = np.arange(-reach, reach + 1)
    penalty = smoothness * moves.astype(np.float64) ** 2
    columns = s.size
    best = emission[0].astype(np.float64)
    back = np.zeros(emission.shape, dtype=np.int8)
    for row in range(1, radius.size):
        stacked = np.full((moves.size, columns), -np.inf)
        for index, move in enumerate(moves):
            if move >= 0:
                stacked[index, move:] = best[: columns - move] - penalty[index] if move else best - penalty[index]
            else:
                stacked[index, :move] = best[-move:] - penalty[index]
        choice = np.argmax(stacked, axis=0)
        best = stacked[choice, np.arange(columns)] + emission[row]
        back[row] = moves[choice]
    state = int(np.argmax(best))
    column = np.zeros(radius.size, dtype=np.int64)
    column[-1] = state
    for row in range(radius.size - 1, 0, -1):
        column[row - 1] = column[row] - back[row, column[row]]

    subpixel = column.astype(np.float64)
    ok = (column > 0) & (column < columns - 1)
    rows = np.where(ok)[0]
    left, mid, right = response[rows, column[rows] - 1], response[rows, column[rows]], response[rows, column[rows] + 1]
    denominator = left - 2 * mid + right
    delta = np.where(np.abs(denominator) > 1e-12, 0.5 * (left - right) / np.where(denominator == 0, 1, denominator), 0.0)
    subpixel[rows] += np.clip(delta, -1, 1)
    offset = s[0] + subpixel * ds
    theta_line = center + offset / radius

    local = np.zeros(radius.size)
    local[rows] = np.maximum(mid, 0) / noise[rows]
    confidence = np.clip((local - 3) / 5, 0, 1) * (np.max(level, axis=1) >= 45)
    confidence = np.where(ok, confidence, 0.0)

    if drawn_band is not None:
        confidence = np.where((radius >= drawn_band[0]) & (radius <= drawn_band[1]), confidence, 0.0)

    guard = int(round(2 * line_mm / ds))
    for row in np.where(confidence > 0)[0]:
        c = column[row]
        far = np.concatenate([positive[row, : max(c - guard, 0)], positive[row, c + guard + 1:]])
        if far.size and far.max() * 1.0 > positive[row, c] * 1.5:
            confidence[row] = 0.0

    window = max(int(round(1.0 / dr)) | 1, 3)
    tangential = offset.copy()
    good = confidence > 0
    if good.sum() > window:
        indices = np.where(good)[0]
        smoothed = median_filter(tangential[indices], size=window, mode="nearest")
        residual = tangential[indices] - smoothed
        scale = 1.4826 * np.median(np.abs(residual)) + 1e-3
        outlier = np.abs(residual) > max(3 * scale, 0.1)
        confidence[indices[outlier]] = 0.0

    return ArmTrace(arm, radius, theta_line, confidence, strip, column)


def reject_cross_arm_outliers(arms, min_confidence=0.3, floor_mm=0.25, local_window_mm=5.0, local_limit_mm=0.4) -> int:
    radius = arms[0].radius
    relative = np.full((len(arms), radius.size), np.nan)
    for row, arm in enumerate(arms):
        good = arm.confidence >= min_confidence
        relative[row, good] = arm.theta[good] - arm.arm * math.pi / 2
    if not np.isfinite(relative).any():
        return 0
    counts = np.sum(np.isfinite(relative), axis=0)
    with np.errstate(invalid="ignore"):
        filled = np.where(np.isfinite(relative), relative, np.inf)
        ordered = np.sort(filled, axis=0)
    reference = np.full(radius.size, np.nan)
    enough = counts >= 2
    columns = np.where(enough)[0]
    lower = ordered[(counts[columns] - 1) // 2, columns]
    upper = ordered[counts[columns] // 2, columns]
    reference[columns] = (lower + upper) / 2
    removed = 0
    window = max(int(round(local_window_mm / (radius[1] - radius[0]))) | 1, 3)
    for row, arm in enumerate(arms):
        good = np.isfinite(relative[row])
        if not good.any():
            continue
        crossed = good & enough
        bad = np.zeros(radius.size, dtype=bool)
        if crossed.sum() >= 20:
            difference = radius[crossed] * (relative[row, crossed] - reference[crossed])
            center = float(np.median(difference))
            spread = 1.4826 * float(np.median(np.abs(difference - center)))
            indices = np.where(crossed)[0]
            bad[indices[np.abs(difference - center) > max(floor_mm, 5 * spread)]] = True
        lonely = good & ~enough
        if lonely.sum() >= 8:
            indices = np.where(good)[0]
            tangential = radius[indices] * (relative[row, indices] - float(np.nanmedian(relative[row])))
            smooth = median_filter(tangential, size=min(window, indices.size | 1), mode="nearest")
            mask = np.abs(tangential - smooth) > local_limit_mm
            bad[indices[mask & lonely[indices]]] = True
        removed += int(bad.sum())
        arm.confidence = np.where(bad, 0.0, arm.confidence)
    return removed


def arm_metrics(trace: ArmTrace, inner, outer, min_confidence=0.3) -> dict:
    good = trace.confidence >= min_confidence
    radius = trace.radius
    band = outer - inner
    if good.sum() < 8:
        return dict(coverage=0.0, valid_fraction=0.0, longest_gap_mm=band, smooth_rms_mm=math.inf, score=0.0)
    r, t = radius[good], trace.theta[good]
    coverage = float(r[-1] - r[0]) / band
    gaps = np.diff(r)
    longest = float(gaps.max()) if gaps.size else band
    tangential = r * (t - np.median(t))
    window = 41
    smooth = median_filter(tangential, size=window, mode="nearest")
    smooth_rms = float(np.sqrt(np.mean((tangential - smooth) ** 2)))
    valid = float(good.mean())
    contrast = float(np.median(trace.confidence[good]))
    score = coverage * valid * contrast * math.exp(-longest / 4.0)
    return dict(
        coverage=coverage, valid_fraction=valid, longest_gap_mm=longest,
        smooth_rms_mm=smooth_rms, confidence=contrast, score=score,
    )


def pair_consistency(a: ArmTrace, b: ArmTrace, min_confidence=0.3) -> dict | None:
    if a.radius.size != b.radius.size:
        return None
    both = (a.confidence >= min_confidence) & (b.confidence >= min_confidence)
    if both.sum() < 20:
        return None
    r = a.radius[both]
    expected = (b.arm - a.arm) * math.pi / 2
    difference = np.angle(np.exp(1j * (b.theta[both] - a.theta[both] - expected)))
    return dict(mean_mm=float(np.mean(r * difference)), std_mm=float(np.std(r * difference)), samples=int(both.sum()))


def ring_measurements(image, matrix, ring_radii, line_mm, window_mm=1.2, dr=0.02, dtheta_deg=0.5, physical_outer=60.0, polarity="dark"):
    out = []
    theta = np.radians(np.arange(0, 360, dtheta_deg))
    kernel = _valley_kernel(line_mm / 2.355 / dr)
    pad = kernel.size // 2
    for expected in ring_radii:
        if expected - window_mm < 5 or expected + window_mm > physical_outer - 0.5:
            continue
        full = np.arange(expected - window_mm - pad * dr, expected + window_mm + pad * dr, dr)
        polar = sample_polar(image, matrix, full, theta)
        polar = cv2.GaussianBlur(polar, (0, 0), sigmaX=1.0, sigmaY=0.05 / dr)
        background = cv2.GaussianBlur(polar, (0, 0), sigmaX=0.01, sigmaY=1.0 / dr)
        flat = polar / (background + 20.0)
        response = cv2.filter2D(flat, cv2.CV_32F, kernel[:, None], borderType=cv2.BORDER_REPLICATE)
        if polarity == "any":
            response = np.abs(response)
        response = response[pad:-pad]
        radius = full[pad:-pad]
        level = background[pad:-pad].mean(axis=0)
        usable = (level > 50) & (polar.max(axis=0) < 250)
        if polarity in ("any", "coherent"):
            column_max = response.max(axis=0) + 1e-6
            strong = (response > 0.5 * column_max[None, :]) & usable[None, :]
            coverage = strong.sum(axis=1) / max(int(usable.sum()), 1)
            coverage = gaussian_filter1d(coverage, 0.05 / dr)
            center_index = int(np.argmax(coverage))
            near = np.abs(radius - radius[center_index]) <= 0.12
            response = np.where(near[:, None], response, 0.0)
        peak = np.argmax(response, axis=0)
        columns = np.arange(theta.size)
        ok = usable & (response[peak, columns] > 0.02) & (peak > 0) & (peak < radius.size - 1)
        if ok.sum() < 8:
            continue
        columns = columns[ok]
        p = peak[ok]
        left, mid, right = response[p - 1, columns], response[p, columns], response[p + 1, columns]
        denominator = left - 2 * mid + right
        delta = np.where(np.abs(denominator) > 1e-12, 0.5 * (left - right) / np.where(denominator == 0, 1, denominator), 0.0)
        r_line = radius[0] + (p + np.clip(delta, -1, 1)) * dr
        center = float(np.median(r_line))
        spread = 1.4826 * float(np.median(np.abs(r_line - center))) + 0.02
        keep = np.abs(r_line - center) < 3 * spread
        t = theta[columns[keep]]
        r = r_line[keep]
        if r.size < 8:
            continue
        source = disc_to_source(matrix, np.column_stack([r * np.cos(t), r * np.sin(t)]))
        out.append((float(expected), source, r))
    return out


def refine_matrix_with_rings(image, matrix, generated_outer, kind, inner, outer, line_mm, rounds=3, ring_radii=None, polarity="dark", lens=False):
    from .rectify import refine_with_circles

    expected = ring_radii_mm(generated_outer, kind) if ring_radii is None else np.asarray(ring_radii, dtype=float)
    expected = expected[(expected > inner + 0.6) & (expected < outer - 0.6)]
    report = {"rings": [], "rounds": []}
    current = matrix
    for window in (1.2, 0.8, 0.5)[:rounds]:
        measured = ring_measurements(image, current, expected, line_mm, window_mm=window, polarity=polarity)
        circles = [(r, source) for r, source, _ in measured]
        result = refine_with_circles(current, circles)
        if not result.succeeded:
            report["failure"] = result.message
            return current, report
        before = [float(np.median(values) - r) for r, _, values in measured]
        report["rounds"].append(dict(
            window_mm=window, count=len(measured), residual_px=result.outer_rms,
            median_offset_mm=[round(v, 4) for v in before],
        ))
        current = result.matrix
    if lens:
        current = _refine_lens(image, current, expected, line_mm, polarity, report)
    return current, report


def _refine_lens(image, matrix, expected, line_mm, polarity, report):
    from .rectify import fit_lens

    current = matrix
    first_before = None
    for _ in range(2):
        measured = ring_measurements(image, current, expected, line_mm, window_mm=0.5, polarity=polarity)
        result = fit_lens(current, [(r, source) for r, source, _ in measured], image.shape)
        first_before = result.rms_before_px if first_before is None else first_before
        if not result.applied:
            report["lens"] = dict(
                applied=False, k1=result.k1, k2=result.k2, rmsBeforePx=result.rms_before_px,
                rmsAfterPx=result.rms_after_px, inDiscPercent=result.in_disc_percent, reason=result.reason,
            )
            return matrix if current is matrix else current
        current = result.mapping
        report["lens"] = dict(
            applied=True, k1=result.k1, k2=result.k2, rmsBeforePx=first_before,
            rmsAfterPx=result.rms_after_px, inDiscPercent=result.in_disc_percent, reason=result.reason,
        )
    return current
