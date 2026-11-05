from __future__ import annotations

import math
from dataclasses import dataclass, field

import cv2
import numpy as np
from scipy.ndimage import gaussian_filter1d


class DetectError(ValueError):
    pass


@dataclass(frozen=True)
class Ellipse:
    cx: float
    cy: float
    a: float
    b: float
    angle: float

    @classmethod
    def from_cv(cls, box) -> "Ellipse":
        (cx, cy), (d1, d2), degrees = box
        angle = math.radians(degrees)
        if d1 >= d2:
            return cls(cx, cy, d1 / 2, d2 / 2, angle)
        return cls(cx, cy, d2 / 2, d1 / 2, angle + math.pi / 2)

    def points(self, t, scale=1.0):
        t = np.asarray(t, dtype=float)
        scale = np.asarray(scale, dtype=float)
        ca, sa = math.cos(self.angle), math.sin(self.angle)
        u = self.a * scale * np.cos(t)
        v = self.b * scale * np.sin(t)
        return self.cx + u * ca - v * sa, self.cy + u * sa + v * ca

    def conic(self) -> np.ndarray:
        ca, sa = math.cos(self.angle), math.sin(self.angle)
        rotate = np.array([[ca, sa, 0], [-sa, ca, 0], [0, 0, 1.0]])
        shift = np.array([[1, 0, -self.cx], [0, 1, -self.cy], [0, 0, 1.0]])
        unit = np.diag([1 / self.a**2, 1 / self.b**2, -1.0])
        m = rotate @ shift
        return m.T @ unit @ m

    def distance(self, x, y):
        c = self.conic()
        h = np.stack([x, y, np.ones_like(x)])
        ch = c @ h
        value = np.sum(h * ch, axis=0)
        gradient = 2 * np.hypot(ch[0], ch[1])
        return np.abs(value) / np.maximum(gradient, 1e-12)

    def parameter_of(self, x, y):
        ca, sa = math.cos(self.angle), math.sin(self.angle)
        dx, dy = x - self.cx, y - self.cy
        return np.arctan2((-dx * sa + dy * ca) / self.b, (dx * ca + dy * sa) / self.a)


@dataclass
class Contour:
    ellipse: Ellipse
    x: np.ndarray
    y: np.ndarray
    inlier: np.ndarray
    rms: float
    max_gap_deg: float
    notes: list[str] = field(default_factory=list)

    @property
    def inlier_points(self):
        return np.column_stack([self.x[self.inlier], self.y[self.inlier]])

    def sample(self, count: int) -> np.ndarray:
        points = self.inlier_points
        t = self.ellipse.parameter_of(points[:, 0], points[:, 1])
        order = np.argsort(t)
        t, points = t[order], points[order]
        chosen = []
        for target in -math.pi + (np.arange(count) + 0.5) * 2 * math.pi / count:
            gap = np.abs(np.angle(np.exp(1j * (t - target))))
            index = int(np.argmin(gap))
            if gap[index] <= 2 * math.pi / count:
                chosen.append(index)
        return points[sorted(set(chosen))]


def _max_angular_gap_deg(t) -> float:
    t = np.sort(np.asarray(t))
    if t.size < 2:
        return 360.0
    gaps = np.diff(np.concatenate([t, [t[0] + 2 * math.pi]]))
    return math.degrees(float(gaps.max()))


def lab_lightness(bgr) -> np.ndarray:
    return cv2.cvtColor(bgr, cv2.COLOR_BGR2Lab)[..., 0].astype(np.float32)


def seed_ellipse(lightness: np.ndarray) -> Ellipse:
    factor = 8
    small = cv2.resize(lightness, None, fx=1 / factor, fy=1 / factor, interpolation=cv2.INTER_AREA)
    small = cv2.GaussianBlur(small, (0, 0), 1.5)
    image = np.clip(small, 0, 255).astype(np.uint8)
    height, width = image.shape
    shortest = min(height, width)
    circles = cv2.HoughCircles(
        image, cv2.HOUGH_GRADIENT, dp=1.5, minDist=8, param1=60, param2=25,
        minRadius=int(0.2 * shortest), maxRadius=int(0.6 * max(height, width)),
    )
    if circles is None:
        raise DetectError("Disc outline not found: the whole disc must be in frame and fill enough of it.")
    gx = cv2.Sobel(small, cv2.CV_32F, 1, 0, ksize=3)
    gy = cv2.Sobel(small, cv2.CV_32F, 0, 1, ksize=3)
    t = np.arange(360) * math.pi / 180
    scored = []
    for cx, cy, radius in circles[0][:200]:
        score = 0.0
        for shift in (-0.03, -0.015, 0.0, 0.015, 0.03):
            r = radius * (1 + shift)
            x = (cx + r * np.cos(t)).astype(np.float32)
            y = (cy + r * np.sin(t)).astype(np.float32)
            inside = (x >= 0) & (y >= 0) & (x < width - 1) & (y < height - 1)
            if inside.mean() < 0.7:
                continue
            radial = (
                cv2.remap(gx, x, y, cv2.INTER_LINEAR).ravel() * np.cos(t)
                + cv2.remap(gy, x, y, cv2.INTER_LINEAR).ravel() * np.sin(t)
            )
            score = max(score, float(np.median(np.abs(radial[inside]))))
        scored.append((score, float(cx), float(cy), float(radius)))
    _, cx, cy, radius = max(scored, key=lambda item: item[0])
    magnitude = cv2.GaussianBlur(np.hypot(gx, gy), (0, 0), 1.0)
    for _ in range(2):
        ellipse = _outermost_circle(magnitude, cx, cy, radius, t)
        if ellipse is None:
            break
        cx, cy, radius = ellipse.cx, ellipse.cy, math.sqrt(ellipse.a * ellipse.b)
    else:
        return Ellipse(
            (ellipse.cx + 0.5) * factor - 0.5, (ellipse.cy + 0.5) * factor - 0.5,
            ellipse.a * factor, ellipse.b * factor, ellipse.angle,
        )
    return Ellipse((cx + 0.5) * factor - 0.5, (cy + 0.5) * factor - 0.5, radius * factor, radius * factor, 0.0)


def _outermost_circle(magnitude, cx, cy, radius, t) -> Ellipse | None:
    height, width = magnitude.shape
    reach = np.arange(0.5 * radius, float(np.hypot(height, width)), 1.0)
    rr, tt = np.meshgrid(reach, t, indexing="ij")
    x = cx + rr * np.cos(tt)
    y = cy + rr * np.sin(tt)
    sampled = cv2.remap(
        magnitude, x.astype(np.float32), y.astype(np.float32), cv2.INTER_LINEAR,
        borderMode=cv2.BORDER_CONSTANT, borderValue=0.0,
    )
    inside = (x >= 2) & (y >= 2) & (x <= width - 3) & (y <= height - 3)
    sampled[~inside] = 0
    limit = 0.35 * float(np.median(sampled.max(axis=0)))
    strong = sampled >= limit
    any_strong = strong.any(axis=0)
    last = reach.size - 1 - np.argmax(strong[::-1], axis=0)
    columns = np.where(any_strong)[0]
    if columns.size < 32:
        return None
    r = reach[last[columns]]
    px, py = cx + r * np.cos(t[columns]), cy + r * np.sin(t[columns])
    try:
        ellipse, _ = ransac_ellipse(px, py, threshold=2.0, iterations=300)
    except DetectError:
        return None
    return ellipse if ellipse.b / ellipse.a >= 0.7 else None


def _profiles(image, ellipse: Ellipse, scales, count):
    t = np.arange(count) * 2 * math.pi / count
    tt, ss = np.meshgrid(t, scales, indexing="ij")
    x, y = ellipse.points(tt, ss)
    sampled = cv2.remap(
        image, x.astype(np.float32), y.astype(np.float32), cv2.INTER_CUBIC,
        borderMode=cv2.BORDER_REPLICATE,
    )
    height, width = image.shape[:2]
    inside = (x >= 2) & (y >= 2) & (x <= width - 3) & (y <= height - 3)
    return t, x, y, sampled, inside


def ransac_ellipse(x, y, threshold=1.5, iterations=500, seed=0):
    rng = np.random.default_rng(seed)
    points = np.column_stack([x, y]).astype(np.float32)
    n = len(points)
    if n < 8:
        raise DetectError("Too few edge points to fit an ellipse.")
    best_inliers, best_count = None, 0
    for _ in range(iterations):
        sample = points[rng.choice(n, 5, replace=False)]
        try:
            candidate = Ellipse.from_cv(cv2.fitEllipseDirect(sample))
        except cv2.error:
            continue
        if not (candidate.a > 0 and candidate.b > 0 and candidate.b / candidate.a > 0.5):
            continue
        inliers = candidate.distance(x, y) < threshold
        count = int(inliers.sum())
        if count > best_count:
            best_inliers, best_count = inliers, count
    if best_inliers is None or best_count < 8:
        raise DetectError("RANSAC found no consistent ellipse.")
    inliers = best_inliers
    ellipse = None
    for limit in (threshold, 1.0, 1.0):
        ellipse = Ellipse.from_cv(cv2.fitEllipseAMS(points[inliers]))
        inliers = ellipse.distance(x, y) < limit
        if inliers.sum() < 8:
            raise DetectError("Too few inliers after ellipse refinement.")
    return ellipse, inliers


def _subpixel(values, index):
    a, b, c = values[index - 1], values[index], values[index + 1]
    denominator = a - 2 * b + c
    return index + (0.5 * (a - c) / denominator if denominator != 0 else 0.0)


def _peak_table(image, ellipse: Ellipse, first, last, count, sigma_px, floor, robust=True):
    step = 0.25 / ellipse.a
    scales = np.arange(first, last, step)
    t, _, _, profile, inside = _profiles(image, ellipse, scales, count)
    sigma = sigma_px / 0.25
    gradient = np.abs(gaussian_filter1d(profile, sigma, axis=1, order=1)) / 0.25
    margin = int(4 * sigma)
    gradient[:, :margin] = 0
    gradient[:, -margin:] = 0
    gradient[~inside] = 0
    positive = gradient[gradient > 0]
    noise = 1.4826 * float(np.median(positive)) if positive.size else 0.0
    limit = max(floor, 5 * noise) if robust else floor
    peaks = np.zeros_like(gradient, dtype=bool)
    peaks[:, 1:-1] = (
        (gradient[:, 1:-1] >= gradient[:, :-2]) & (gradient[:, 1:-1] > gradient[:, 2:])
        & (gradient[:, 1:-1] >= limit)
    )
    return t, scales, gradient, peaks


def _points_near(ellipse, t, scales, gradient, peaks, center, width):
    xs, ys = [], []
    step = scales[1] - scales[0]
    for row in range(t.size):
        choice = np.where(peaks[row] & (np.abs(scales - center) <= width))[0]
        if choice.size == 0:
            continue
        index = int(choice[np.argmax(gradient[row, choice])])
        scale = scales[0] + _subpixel(gradient[row], index) * step
        x, y = ellipse.points(t[row], scale)
        xs.append(float(x))
        ys.append(float(y))
    return np.array(xs), np.array(ys)


def _clusters(scales, peaks, smooth):
    histogram = np.bincount(np.where(peaks)[1], minlength=scales.size).astype(float)
    histogram = gaussian_filter1d(histogram, smooth / (scales[1] - scales[0]))
    local = np.where(
        (histogram[1:-1] >= histogram[:-2]) & (histogram[1:-1] > histogram[2:])
    )[0] + 1
    return histogram, local


def _make_contour(x, y, threshold=1.5) -> Contour:
    ellipse, inliers = ransac_ellipse(x, y, threshold=threshold)
    distance = ellipse.distance(x, y)
    gap = _max_angular_gap_deg(ellipse.parameter_of(x[inliers], y[inliers]))
    return Contour(ellipse, x, y, inliers, float(np.sqrt(np.mean(distance[inliers] ** 2))), gap)


def find_outer_edge(bgr, count=720, seed: Ellipse | None = None) -> Contour:
    lightness = lab_lightness(bgr)
    ellipse = seed or seed_ellipse(lightness)

    t, scales, gradient, peaks = _peak_table(lightness, ellipse, 0.95, 1.05, count, 3.0, 0.3)
    strongest = np.argmax(gradient, axis=1)
    keep = gradient[np.arange(count), strongest] > 0
    x, y = ellipse.points(t[keep], scales[strongest[keep]])
    ellipse, _ = ransac_ellipse(np.asarray(x), np.asarray(y), threshold=6.0)

    t, scales, gradient, peaks = _peak_table(lightness, ellipse, 0.975, 1.035, count, 1.5, 0.6)
    histogram, local = _clusters(scales, peaks, 0.0012)
    if local.size == 0:
        raise DetectError("No consistent outer disc edge found around the full circle.")
    chosen = None
    for index in local[::-1]:
        near = np.abs(scales - scales[index]) <= 0.004
        if peaks[:, near].any(axis=1).mean() >= 0.25:
            chosen = index
            break
    chosen = int(np.argmax(histogram)) if chosen is None else int(chosen)
    final_x, final_y = _points_near(ellipse, t, scales, gradient, peaks, scales[chosen], 0.004)
    ellipse, _ = ransac_ellipse(final_x, final_y, threshold=2.5)
    t, scales, gradient, peaks = _peak_table(lightness, ellipse, 0.99, 1.01, count, 1.5, 0.6, robust=False)
    final_x, final_y = _points_near(ellipse, t, scales, gradient, peaks, 1.0, 0.003)

    contour = _make_contour(final_x, final_y, threshold=1.5)
    e = contour.ellipse
    if contour.max_gap_deg >= 60:
        contour.notes.append(f"Largest angular gap in outer-edge inliers is {contour.max_gap_deg:.0f}°, above 60°.")
    if e.b / e.a < 0.75:
        contour.notes.append(f"Camera tilt too large: ellipse axis ratio {e.b / e.a:.2f} < 0.75.")
    if contour.rms >= 1.0:
        contour.notes.append(f"Outer-edge fit residual {contour.rms:.2f} px is at least 1 px.")
    return contour


def _hole_seed(lightness, outer: Ellipse, low, high, max_offset) -> Ellipse:
    radius = outer.a
    box = int((max_offset + high) * radius * 1.1)
    x0, y0 = max(int(outer.cx) - box, 0), max(int(outer.cy) - box, 0)
    crop = lightness[y0:int(outer.cy) + box, x0:int(outer.cx) + box]
    blurred = cv2.GaussianBlur(crop, (0, 0), 1.5)
    gx = cv2.Sobel(blurred, cv2.CV_32F, 1, 0, ksize=3)
    gy = cv2.Sobel(blurred, cv2.CV_32F, 0, 1, ksize=3)
    gradient = np.hypot(gx, gy)
    angles = np.arange(180) * 2 * math.pi / 180
    cosine, sine = np.cos(angles), np.sin(angles)
    radii = np.arange(low * radius, high * radius + 1.5, 1.5)

    def search(magnitude, centers_x, centers_y):
        height, width = magnitude.shape
        found, found_score = None, -1.0
        for cy in centers_y:
            for cx in centers_x:
                xs = np.rint(cx + radii[:, None] * cosine).astype(int)
                ys = np.rint(cy + radii[:, None] * sine).astype(int)
                if xs.min() < 0 or ys.min() < 0 or xs.max() >= width or ys.max() >= height:
                    continue
                scores = np.median(magnitude[ys, xs], axis=1)
                index = int(np.argmax(scores))
                if scores[index] > found_score:
                    found_score = float(scores[index])
                    found = (float(cx), float(cy), float(radii[index]))
        return found

    span = max_offset * radius
    cx0, cy0 = outer.cx - x0, outer.cy - y0
    coarse = np.arange(-span, span + 8.0, 8.0)
    wide = cv2.GaussianBlur(gradient, (0, 0), 4.0)
    best = search(wide, cx0 + coarse, cy0 + coarse)
    if best is not None:
        fine = np.arange(-10.0, 11.0, 2.0)
        narrow = cv2.GaussianBlur(gradient, (0, 0), 1.5)
        best = search(narrow, best[0] + fine, best[1] + fine)
    if best is not None:
        best = (best[0] + x0, best[1] + y0, best[2])
    if best is None:
        raise DetectError("Center hole edge not found within the expected ratio window.")
    return Ellipse(best[0], best[1], best[2], best[2] * (outer.b / outer.a), outer.angle)


def _offset_limit(outer: Ellipse) -> float:
    tilt = math.acos(min(outer.b / outer.a, 1.0))
    return min(0.05 + 0.6 * math.tan(tilt), 0.22)


def find_hole(bgr, outer: Ellipse, expected_ratio=0.125, tolerance=0.16, count=720, max_offset=None) -> Contour:
    lightness = lab_lightness(bgr)
    max_offset = _offset_limit(outer) if max_offset is None else max_offset
    low, high = expected_ratio * (1 - tolerance), expected_ratio * (1 + tolerance)
    hole = _hole_seed(lightness, outer, low, high, max_offset)

    for width in (0.05, 0.025):
        t, scales, gradient, peaks = _peak_table(lightness, hole, 0.85, 1.15, count, 1.5, 0.3, robust=False)
        histogram, local = _clusters(scales, peaks, 0.006)
        near = local[np.abs(scales[local] - 1.0) <= width]
        center = float(scales[near[np.argmax(histogram[near])]]) if near.size else 1.0
        x, y = _points_near(hole, t, scales, gradient, peaks, center, width)
        hole, _ = ransac_ellipse(x, y, threshold=2.0)

    contour = _make_contour(x, y, threshold=1.5)
    e = contour.ellipse
    ratio = math.sqrt(e.a * e.b / (outer.a * outer.b))
    offset = math.hypot(e.cx - outer.cx, e.cy - outer.cy) / outer.a
    if not expected_ratio * 0.92 <= ratio <= expected_ratio * 1.08:
        contour.notes.append(f"Hole-to-outer ratio {ratio:.4f} differs from the expected {expected_ratio:.4f}; it may have picked a clear-zone step or hub ring.")
    if contour.max_gap_deg >= 90:
        contour.notes.append(f"Largest angular gap in hole inliers is {contour.max_gap_deg:.0f}°, above 90°.")
    if offset >= max_offset:
        contour.notes.append(f"Hole center is {offset:.3f} r from the outer ellipse center, above {max_offset}.")
    return contour


def draw_contour(canvas, contour: Contour, color=(0, 255, 0), scale=1.0) -> None:
    for x, y, keep in zip(contour.x, contour.y, contour.inlier):
        center = (int(round(x * scale)), int(round(y * scale)))
        cv2.circle(canvas, center, 2, color if keep else (0, 0, 255), -1)
    t = np.linspace(0, 2 * math.pi, 720)
    ex, ey = contour.ellipse.points(t)
    path = np.column_stack([ex * scale, ey * scale]).astype(np.int32)
    cv2.polylines(canvas, [path], True, (255, 255, 0), 1, cv2.LINE_AA)
