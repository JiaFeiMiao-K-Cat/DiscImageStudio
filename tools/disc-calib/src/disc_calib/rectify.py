from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np


@dataclass
class RectifyResult:
    succeeded: bool
    matrix: np.ndarray | None
    outer_rms: float
    hole_rms: float
    message: str


def _map(matrix, points):
    points = np.asarray(points, dtype=float)
    divisor = matrix[6] * points[..., 0] + matrix[7] * points[..., 1] + matrix[8]
    x = (matrix[0] * points[..., 0] + matrix[1] * points[..., 1] + matrix[2]) / divisor
    y = (matrix[3] * points[..., 0] + matrix[4] * points[..., 1] + matrix[5]) / divisor
    return np.stack([x, y], axis=-1)


@dataclass(frozen=True)
class PhotoMap:

    matrix: np.ndarray
    k1: float
    k2: float
    cx: float
    cy: float
    norm: float

    def distort(self, points):
        points = np.asarray(points, dtype=float)
        dx, dy = (points[..., 0] - self.cx) / self.norm, (points[..., 1] - self.cy) / self.norm
        rho2 = dx * dx + dy * dy
        factor = 1 + rho2 * (self.k1 + rho2 * self.k2)
        return np.stack([self.cx + dx * factor * self.norm, self.cy + dy * factor * self.norm], axis=-1)

    def undistort(self, points, iterations=6):
        points = np.asarray(points, dtype=float)
        ux, uy = (points[..., 0] - self.cx) / self.norm, (points[..., 1] - self.cy) / self.norm
        dx, dy = ux, uy
        for _ in range(iterations):
            rho2 = ux * ux + uy * uy
            factor = 1 + rho2 * (self.k1 + rho2 * self.k2)
            ux, uy = dx / factor, dy / factor
        return np.stack([self.cx + ux * self.norm, self.cy + uy * self.norm], axis=-1)


def homography_of(matrix) -> np.ndarray:
    return (matrix.matrix if isinstance(matrix, PhotoMap) else np.asarray(matrix, dtype=float)).ravel()


def disc_to_source(matrix, points_mm):
    projected = _map(homography_of(matrix), points_mm)
    return matrix.distort(projected) if isinstance(matrix, PhotoMap) else projected


def source_to_disc(matrix, points_px):
    points = matrix.undistort(points_px) if isinstance(matrix, PhotoMap) else points_px
    inverse = np.linalg.inv(homography_of(matrix).reshape(3, 3)).ravel()
    return _map(inverse, points)


def _is_spread(points) -> bool:
    delta = points[:, None, :] - points[None, :, :]
    squared = np.sum(delta * delta, axis=2)
    np.fill_diagonal(squared, 1.0)
    if (squared < 1e-10).any():
        return False
    centred = points - points.mean(axis=0)
    xx = float(np.sum(centred[:, 0] ** 2))
    yy = float(np.sum(centred[:, 1] ** 2))
    xy = float(np.sum(centred[:, 0] * centred[:, 1]))
    return xx + yy > 1e-6 and xx * yy - xy * xy > (xx + yy) ** 2 * 1e-5


def _initial_affine_ellipse(points):
    mean = points.mean(axis=0)
    x, y = points[:, 0] - mean[0], points[:, 1] - mean[1]
    rows = np.column_stack([x * x, 2 * x * y, y * y, x, y])
    try:
        q = np.linalg.solve(rows.T @ rows, rows.sum(axis=0))
    except np.linalg.LinAlgError:
        return None
    determinant = q[0] * q[2] - q[1] * q[1]
    if q[0] <= 0 or q[2] <= 0 or determinant <= 1e-10:
        return None
    cx = -(q[2] * q[3] - q[1] * q[4]) / (2 * determinant)
    cy = -(q[0] * q[4] - q[1] * q[3]) / (2 * determinant)
    k = 1 + q[0] * cx * cx + 2 * q[1] * cx * cy + q[2] * cy * cy
    if k <= 0:
        return None
    a = math.sqrt(q[0] / k)
    b = q[1] / (k * a)
    d_squared = q[2] / k - b * b
    if d_squared <= 1e-10:
        return None
    d = math.sqrt(d_squared)
    return np.array([
        math.log(a), b, -a * (cx + mean[0]) - b * (cy + mean[1]),
        math.log(d), -d * (cy + mean[1]), 0.0, 0.0,
    ])


def _initial_joint_projection(outer_affine, hole, ratio):
    a, b, c = math.exp(outer_affine[0]), outer_affine[1], outer_affine[2]
    d, e = math.exp(outer_affine[3]), outer_affine[4]
    normalized = np.column_stack([a * hole[:, 0] + b * hole[:, 1] + c, d * hole[:, 1] + e])
    ellipse = _initial_affine_ellipse(normalized)
    if ellipse is None:
        return None
    center_y = -ellipse[4] / math.exp(ellipse[3])
    center_x = -(ellipse[2] + ellipse[1] * center_y) / math.exp(ellipse[0])
    length = math.hypot(center_x, center_y)
    if length < 1e-10:
        return outer_affine.copy()
    if length >= 0.95:
        return None
    r2 = ratio * ratio
    one_minus = 1 - r2
    v = 2 * length / (one_minus + math.sqrt(one_minus * one_minus + 4 * length * length * r2))
    if v >= 0.98:
        return None
    nx, ny = center_x / length, center_y / length
    vx, vy, gamma = v * nx, v * ny, 1 / math.sqrt(1 - v * v)
    ax = 1 + (gamma - 1) * nx * nx
    ay = 1 + (gamma - 1) * ny * ny
    cross = (gamma - 1) * nx * ny
    m = [
        ax * a, ax * b + cross * d, ax * c + cross * e - gamma * vx,
        cross * a, cross * b + ay * d, cross * c + ay * e - gamma * vy,
        -gamma * vx * a, -gamma * (vx * b + vy * d), gamma * (1 - vx * c - vy * e),
    ]
    first = math.hypot(m[0], m[3])
    cosine, sine, scale = m[0] / first, m[3] / first, m[8]
    new_a = first / scale
    new_b = (cosine * m[1] + sine * m[4]) / scale
    new_c = (cosine * m[2] + sine * m[5]) / scale
    new_d = (-sine * m[1] + cosine * m[4]) / scale
    new_e = (-sine * m[2] + cosine * m[5]) / scale
    if new_a <= 0 or new_d <= 0:
        return None
    return np.array([math.log(new_a), new_b, new_c, math.log(new_d), new_e, m[6] / scale, m[7] / scale])


def _evaluate(p, x, y, radius, weight):
    if (
        not np.isfinite(p).all() or abs(p[0]) > 7 or abs(p[3]) > 7
        or abs(p[5]) > 10 or abs(p[6]) > 10
    ):
        return np.full(x.shape, np.inf)
    a, d = math.exp(p[0]), math.exp(p[3])
    determinant = a * d - a * p[4] * p[6] + p[1] * p[4] * p[5] - p[2] * d * p[5]
    horizon_x, horizon_y = -d * p[5], p[1] * p[5] - a * p[6]
    if determinant <= a * d * 1e-4 or a * d <= math.hypot(horizon_x, horizon_y) * 1.02:
        return np.full(x.shape, np.inf)
    u = a * x + p[1] * y + p[2]
    v = d * y + p[4]
    w = p[5] * x + p[6] * y + 1
    r2 = radius * radius
    gx = 2 * (a * u - r2 * p[5] * w)
    gy = 2 * (p[1] * u + d * v - r2 * p[6] * w)
    gradient = np.hypot(gx, gy)
    bad = (w <= 0.03) | (gradient < 1e-12)
    safe = np.where(bad, 1.0, gradient)
    return np.where(bad, np.inf, (u * u + v * v - r2 * w * w) / safe * weight)


def _optimize(parameters, x, y, radius, weight):
    residual = _evaluate(parameters, x, y, radius, weight)
    cost = float(np.sum(residual * residual))
    damping = 1e-3
    for _ in range(120):
        if not math.isfinite(cost):
            break
        jacobian = np.zeros((7, x.size))
        for axis in range(7):
            step = 1e-5 * max(1.0, abs(parameters[axis]))
            shifted = parameters.copy()
            shifted[axis] += step
            jacobian[axis] = (_evaluate(shifted, x, y, radius, weight) - residual) / step
        normal = jacobian @ jacobian.T
        right = -(jacobian @ residual)
        normal[np.diag_indices(7)] += damping * np.maximum(np.diag(normal), 1e-8)
        if not np.isfinite(normal).all():
            damping *= 10
            if damping > 1e12:
                break
            continue
        try:
            delta = np.linalg.solve(normal, right)
        except np.linalg.LinAlgError:
            break
        candidate = parameters + delta
        candidate_residual = _evaluate(candidate, x, y, radius, weight)
        candidate_cost = float(np.sum(candidate_residual * candidate_residual))
        if candidate_cost < cost:
            improvement = cost - candidate_cost
            parameters, residual, cost = candidate, candidate_residual, candidate_cost
            damping = max(1e-10, damping / 3)
            if cost < 1e-22 or improvement < max(1e-22, cost * 1e-10):
                break
        else:
            damping *= 10
            if damping > 1e12:
                break
    return parameters, cost


def _invert_normalized(source_to_disc_matrix):
    inverse = np.linalg.inv(source_to_disc_matrix)
    return (inverse / np.abs(inverse).max()).ravel()


def _covers_circle(points, matrix) -> bool:
    disc = source_to_disc(matrix, points)
    angles = np.sort(np.arctan2(disc[:, 1], disc[:, 0]))
    gap = max(angles[0] + 2 * math.pi - angles[-1], float(np.max(np.diff(angles))) if angles.size > 1 else 0)
    return gap < math.pi * 0.95


def contour_residual(points, matrix, radius):
    disc = source_to_disc(matrix, points)
    angle = np.arctan2(disc[:, 1], disc[:, 0])
    source = np.asarray(points, dtype=float)

    def project(theta):
        return disc_to_source(matrix, np.column_stack([radius * np.cos(theta), radius * np.sin(theta)]))

    for _ in range(15):
        step = 1e-5
        p, q = project(angle), project(angle + step)
        dx, dy = (q[:, 0] - p[:, 0]) / step, (q[:, 1] - p[:, 1]) / step
        length = dx * dx + dy * dy
        length = np.where(length < 1e-12, np.inf, length)
        delta = np.clip(((p[:, 0] - source[:, 0]) * dx + (p[:, 1] - source[:, 1]) * dy) / length, -0.3, 0.3)
        angle = angle - delta
        if np.abs(delta).max() < 1e-10:
            break
    distance = np.hypot(*(project(angle) - source).T)
    return math.sqrt(float(np.mean(distance * distance))), float(distance.max())


def _failed(message):
    return RectifyResult(False, None, math.nan, math.nan, message)


def fit_homography(outer_pixels, hole_pixels, physical_outer=60.0, physical_hole=7.5) -> RectifyResult:
    outer_pixels = np.asarray(outer_pixels, dtype=float).reshape(-1, 2)
    hole_pixels = np.asarray(hole_pixels, dtype=float).reshape(-1, 2)
    if not 5 <= len(outer_pixels) <= 256 or not 5 <= len(hole_pixels) <= 256:
        return _failed("The outer and inner edges each need at least 5 spread-out points, and at most 256 points per contour.")
    if not (np.isfinite(outer_pixels).all() and np.isfinite(hole_pixels).all()):
        return _failed("A contour contains invalid coordinates.")
    low, high = outer_pixels.min(axis=0), outer_pixels.max(axis=0)
    center = (low + high) / 2
    source_scale = float(np.max(high - low)) / 2
    if source_scale < 5:
        return _failed("The disc is too small in the photo to rectify reliably.")
    outer = (outer_pixels - center) / source_scale
    hole = (hole_pixels - center) / source_scale
    if not _is_spread(outer) or not _is_spread(hole):
        return _failed("Contour points repeat or are nearly collinear; spread them along the outer and inner edges.")
    initial = _initial_affine_ellipse(outer)
    if initial is None:
        return _failed("The outer contour does not form a stable ellipse; check the points on the physical outer edge.")
    a0, d0 = math.exp(initial[0]), math.exp(initial[3])
    extent = np.hypot(
        a0 * hole[:, 0] + initial[1] * hole[:, 1] + initial[2], d0 * hole[:, 1] + initial[4]
    ).mean()
    if not 0.015 <= extent <= 0.9:
        return _failed("The inner contour does not match the outer edge; make sure it is the physical through-hole edge, not a printed ring.")

    ratio = physical_hole / physical_outer
    x = np.concatenate([outer[:, 0], hole[:, 0]])
    y = np.concatenate([outer[:, 1], hole[:, 1]])
    radius = np.concatenate([np.ones(len(outer)), np.full(len(hole), ratio)])
    weight = np.concatenate([
        np.full(len(outer), 1 / math.sqrt(len(outer))), np.full(len(hole), 1 / math.sqrt(len(hole))),
    ])
    seeds = []
    joint = _initial_joint_projection(initial, hole, ratio)
    if joint is not None:
        seeds.append(joint)
    for gx, gy in ((0.0, 0.0), (0.25, 0.0), (-0.25, 0.0), (0.0, 0.25), (0.0, -0.25)):
        seed = initial.copy()
        seed[5], seed[6] = gx, gy
        seeds.append(seed)
    best, best_cost = None, math.inf
    for seed in seeds:
        solution, cost = _optimize(seed, x, y, radius, weight)
        if cost < best_cost:
            best, best_cost = solution, cost
        if best_cost < 1e-20:
            break
    if best is None or not math.isfinite(best_cost):
        return _failed("The two contours do not give a valid joint projection; check the points.")

    a, b, c = math.exp(best[0]), best[1], best[2]
    d, e, g, h = math.exp(best[3]), best[4], best[5], best[6]
    R = physical_outer
    forward = np.array([
        [R * a / source_scale, R * b / source_scale, R * (c - (a * center[0] + b * center[1]) / source_scale)],
        [0.0, R * d / source_scale, R * (e - d * center[1] / source_scale)],
        [g / source_scale, h / source_scale, 1 - (g * center[0] + h * center[1]) / source_scale],
    ])
    try:
        matrix = _invert_normalized(forward)
    except np.linalg.LinAlgError:
        return _failed("The photo transform is degenerate; disc coordinates cannot be recovered.")
    variation = R * math.hypot(matrix[6], matrix[7])
    if abs(matrix[8]) <= variation * 1.02 + 1e-12:
        return _failed("The camera angle or contour correspondence makes the projection degenerate; change the angle or redo the points.")
    if not _covers_circle(outer_pixels, matrix) or not _covers_circle(hole_pixels, matrix):
        return _failed("Contour points are concentrated in one area; add points in other directions around both circles.")
    outer_rms, outer_max = contour_residual(outer_pixels, matrix, R)
    hole_rms, hole_max = contour_residual(hole_pixels, matrix, physical_hole)
    tolerance = max(2.0, source_scale * 0.006)
    if outer_rms > tolerance or hole_rms > tolerance or max(outer_max, hole_max) > tolerance * 3:
        return RectifyResult(
            False, None, outer_rms, hole_rms,
            f"The two physical contours cannot both fit: outer error {outer_rms:.2f} px, inner error {hole_rms:.2f} px.",
        )
    return RectifyResult(
        True, matrix, outer_rms, hole_rms,
        f"Outer and inner edges jointly rectified. Contour error: outer {outer_rms:.2f} px, inner {hole_rms:.2f} px.",
    )


def _seed_from_matrix(matrix, center, source_scale, reference_radius):
    forward = np.linalg.inv(np.asarray(matrix, dtype=float).reshape(3, 3))
    denormalize = np.array([
        [source_scale, 0.0, center[0]], [0.0, source_scale, center[1]], [0.0, 0.0, 1.0],
    ])
    m = forward @ denormalize
    m[:2] /= reference_radius
    m = m / m[2, 2]
    first = math.hypot(m[0, 0], m[1, 0])
    cosine, sine = m[0, 0] / first, m[1, 0] / first
    top = cosine * m[0] + sine * m[1]
    bottom = -sine * m[0] + cosine * m[1]
    if top[0] <= 0 or bottom[1] <= 0:
        raise ValueError("The existing matrix cannot be converted to optimizer form.")
    return np.array([math.log(top[0]), top[1], top[2], math.log(bottom[1]), bottom[2], m[2, 0], m[2, 1]])


def _matrix_from_solution(solution, center, source_scale, reference):
    a, b, c = math.exp(solution[0]), solution[1], solution[2]
    d, e, g, h = math.exp(solution[3]), solution[4], solution[5], solution[6]
    forward = np.array([
        [reference * a / source_scale, reference * b / source_scale,
         reference * (c - (a * center[0] + b * center[1]) / source_scale)],
        [0.0, reference * d / source_scale, reference * (e - d * center[1] / source_scale)],
        [g / source_scale, h / source_scale, 1 - (g * center[0] + h * center[1]) / source_scale],
    ])
    return _invert_normalized(forward)


LENS_MIN_IMPROVEMENT = 0.10
LENS_MAX_IN_DISC = 0.02


@dataclass
class LensResult:
    applied: bool
    mapping: object
    k1: float
    k2: float
    rms_before_px: float
    rms_after_px: float
    in_disc_percent: float
    reason: str


def _circle_rms(mapping, circles) -> float:
    values = [contour_residual(points, mapping, radius)[0] for radius, points in circles]
    return float(np.sqrt(np.mean(np.square(values))))


def fit_lens(matrix, circles, image_shape, max_iterations=60) -> LensResult:
    plain = homography_of(matrix)
    before = _circle_rms(plain, circles)
    terms = 2 if len(circles) >= 5 else 1 if len(circles) == 4 else 0
    if terms == 0:
        return LensResult(False, plain, 0.0, 0.0, before, before, 0.0, f"Only {len(circles)} rings; lens distortion not solved.")
    height, width = image_shape[:2]
    cx, cy = (width - 1) / 2, (height - 1) / 2
    norm = math.hypot(width, height) / 2
    pixels = np.concatenate([np.asarray(points, dtype=float) for _, points in circles])
    reference = max(radius for radius, _ in circles)
    low, high = pixels.min(axis=0), pixels.max(axis=0)
    center = (low + high) / 2
    source_scale = float(np.max(high - low)) / 2
    radius = np.concatenate([np.full(len(points), r / reference) for r, points in circles])
    weight = np.concatenate([np.full(len(points), 1 / math.sqrt(len(points))) for _, points in circles])
    parameters = np.concatenate([_seed_from_matrix(plain, center, source_scale, reference), np.zeros(terms)])

    def residual_of(p):
        k1 = p[7]
        k2 = p[8] if terms == 2 else 0.0
        undistorted = PhotoMap(plain, k1, k2, cx, cy, norm).undistort(pixels, iterations=4)
        normalized = (undistorted - center) / source_scale
        return _evaluate(p[:7], normalized[:, 0], normalized[:, 1], radius, weight)

    residual = residual_of(parameters)
    cost = float(np.sum(residual * residual))
    damping = 1e-3
    count = parameters.size
    for _ in range(max_iterations):
        if not math.isfinite(cost):
            break
        jacobian = np.zeros((count, pixels.shape[0]))
        for axis in range(count):
            step = 1e-5 * max(1.0, abs(parameters[axis]))
            shifted = parameters.copy()
            shifted[axis] += step
            jacobian[axis] = (residual_of(shifted) - residual) / step
        normal = jacobian @ jacobian.T
        right = -(jacobian @ residual)
        normal[np.diag_indices(count)] += damping * np.maximum(np.diag(normal), 1e-8)
        if not np.isfinite(normal).all():
            break
        try:
            delta = np.linalg.solve(normal, right)
        except np.linalg.LinAlgError:
            break
        candidate = parameters + delta
        candidate_residual = residual_of(candidate)
        candidate_cost = float(np.sum(candidate_residual * candidate_residual))
        if candidate_cost < cost:
            improvement = cost - candidate_cost
            parameters, residual, cost = candidate, candidate_residual, candidate_cost
            damping = max(1e-10, damping / 3)
            if improvement < max(1e-22, cost * 1e-10):
                break
        else:
            damping *= 10
            if damping > 1e12:
                break
    k1 = float(parameters[7])
    k2 = float(parameters[8]) if terms == 2 else 0.0
    try:
        refined = _matrix_from_solution(parameters[:7], center, source_scale, reference)
    except np.linalg.LinAlgError:
        return LensResult(False, plain, 0.0, 0.0, before, before, 0.0, "Homography is degenerate after the distortion fit.")
    mapping = PhotoMap(refined, k1, k2, cx, cy, norm)
    after = _circle_rms(mapping, circles)
    rho = np.hypot(pixels[:, 0] - cx, pixels[:, 1] - cy) / norm
    grid = np.linspace(0, float(rho.max()), 64)
    in_disc = float(np.abs(grid * grid * (k1 + grid * grid * k2)).max())
    if in_disc > LENS_MAX_IN_DISC:
        return LensResult(False, plain, k1, k2, before, after, in_disc * 100, f"Distortion of {in_disc:.1%} across the disc is implausible; not applied.")
    if after > before * (1 - LENS_MIN_IMPROVEMENT):
        return LensResult(False, plain, k1, k2, before, after, in_disc * 100, "Residual did not drop noticeably; not applied.")
    return LensResult(True, mapping, k1, k2, before, after, in_disc * 100, "Applied.")


def refine_with_circles(matrix, circles) -> RectifyResult:
    if len(circles) < 3:
        return _failed("Fewer than 3 usable concentric rings.")
    pixels = np.concatenate([np.asarray(points, dtype=float) for _, points in circles])
    reference = max(radius for radius, _ in circles)
    low, high = pixels.min(axis=0), pixels.max(axis=0)
    center = (low + high) / 2
    source_scale = float(np.max(high - low)) / 2
    normalized = (pixels - center) / source_scale
    radius = np.concatenate([np.full(len(points), r / reference) for r, points in circles])
    weight = np.concatenate([np.full(len(points), 1 / math.sqrt(len(points))) for _, points in circles])
    seed = _seed_from_matrix(matrix, center, source_scale, reference)
    solution, cost = _optimize(seed, normalized[:, 0], normalized[:, 1], radius, weight)
    if not math.isfinite(cost):
        return _failed("The concentric rings do not give a valid joint projection.")
    try:
        refined = _matrix_from_solution(solution, center, source_scale, reference)
    except np.linalg.LinAlgError:
        return _failed("The transform is degenerate after ring refinement.")
    residual = []
    for r, points in circles:
        residual.append(contour_residual(points, refined, r)[0])
    rms = float(np.sqrt(np.mean(np.square(residual))))
    return RectifyResult(True, refined, rms, rms, f"Ring refinement: {len(circles)} rings, residual {rms:.2f} px.")
