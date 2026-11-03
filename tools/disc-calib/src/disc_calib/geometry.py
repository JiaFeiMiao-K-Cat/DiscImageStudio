from __future__ import annotations

import math
from dataclasses import dataclass, replace

import numpy as np
from scipy.interpolate import CubicSpline
from scipy.optimize import brentq

CD = "Cd"
DVD = "Dvd"
BLU_RAY = "BluRay"

DVD_CHANNEL_BITS_PER_SECTOR = 38_688
BD_SECTORS_PER_CLUSTER = 32
BD_CHANNEL_BITS_PER_UNIT = 962_136
BD_CHANNEL_BIT_NM = 74.5

_GL_HALF_NODES = np.array(
    [0.1834346424956498, 0.5255324099163290, 0.7966664774136267, 0.9602898564975363]
)
_GL_HALF_WEIGHTS = np.array(
    [0.3626837833783620, 0.3137066458778873, 0.2223810344533745, 0.1012285362903763]
)
_GL_X = np.concatenate([-_GL_HALF_NODES[::-1], _GL_HALF_NODES])
_GL_W = np.concatenate([_GL_HALF_WEIGHTS[::-1], _GL_HALF_WEIGHTS])


class GeometryError(ValueError):
    pass


def _polynomial(c1: float, c2: float, c3: float, x):
    return 1 + x * (c1 + x * (c2 + x * c3))


def pitch_positive_on_interval(c1: float, c2: float, c3: float, first=0.0, last=1.0) -> bool:
    if not all(math.isfinite(v) for v in (c1, c2, c3)):
        return False

    def positive(x):
        value = _polynomial(c1, c2, c3, x)
        return math.isfinite(value) and value > 0

    if not positive(first) or not positive(last):
        return False
    scale = max(abs(c1), abs(c2), abs(c3))
    if scale == 0:
        return True
    a, b, c = 3 * (c3 / scale), 2 * (c2 / scale), c1 / scale

    def check(x):
        return not (first < x < last) or positive(x)

    if a == 0:
        return b == 0 or check(-c / b)
    discriminant = b * b - 4 * a * c
    if discriminant < 0:
        return True
    q = -0.5 * (b + math.copysign(math.sqrt(discriminant), b))
    if q == 0:
        return check(-b / (2 * a))
    return check(q / a) and check(c / q)


@dataclass(frozen=True)
class Params:

    kind: str
    inner: float
    outer: float
    sectors: int
    linear_density: float
    pitch_linear: float = 0.0
    pitch_quadratic: float = 0.0
    pitch_cubic: float = 0.0
    bd_invert_mapping: bool = False
    drawing_start_sector: int = 0
    bd_iso_timestamp: str | None = None
    drawing_end_sector_exclusive: int = 0

    @property
    def track_length(self) -> float:
        if self.kind == CD:
            return self.sectors / 75.0 * self.linear_density
        if self.kind == DVD:
            return float(self.sectors) * DVD_CHANNEL_BITS_PER_SECTOR * self.linear_density * 1e-6
        if self.kind == BLU_RAY:
            return (
                self.sectors / BD_SECTORS_PER_CLUSTER
                * BD_CHANNEL_BITS_PER_UNIT * self.linear_density * 1e-6
            )
        raise GeometryError(f"Unknown disc kind {self.kind!r}")

    @property
    def is_constant_pitch(self) -> bool:
        return self.pitch_linear == 0 and self.pitch_quadratic == 0 and self.pitch_cubic == 0

    def with_radii(self, inner: float, outer: float) -> "Params":
        return replace(self, inner=inner, outer=outer)

    def validate(self) -> None:
        values = (self.inner, self.outer, self.linear_density)
        if (
            self.kind not in (CD, DVD, BLU_RAY)
            or self.sectors <= 0
            or not all(math.isfinite(v) for v in values)
            or self.inner <= 0
            or self.outer <= self.inner
            or self.linear_density <= 0
            or not math.isfinite(self.track_length)
            or self.track_length <= self.outer - self.inner
        ):
            raise GeometryError("Invalid calibration parameters: check the disc kind, inner and outer radii, sector count, and linear velocity or bit length.")
        if not pitch_positive_on_interval(self.pitch_linear, self.pitch_quadratic, self.pitch_cubic):
            raise GeometryError("Pitch coefficients must be finite, and 1+c1x+c2x^2+c3x^3 must stay positive on 0<=x<=1.")
        if self.kind != DVD and not self.is_constant_pitch:
            raise GeometryError("Cubic pitch parameters apply to DVD only.")


class _EqualArea:

    def __init__(self, p: Params):
        self.inner, self.outer = p.inner, p.outer
        self._range = p.outer * p.outer - p.inner * p.inner
        self.length = p.track_length
        self.b = self._range / (2 * self.length)

    def growth_at(self, radius):
        return np.full_like(np.asarray(radius, dtype=float), self.b)

    def progress(self, radius):
        radius = np.asarray(radius, dtype=float)
        out = (radius - self.inner) * (radius + self.inner) / self._range
        return np.where(radius > 0, out, np.nan)

    def angle_at(self, radius):
        return (np.asarray(radius, dtype=float) - self.inner) / self.b

    def at_progress(self, progress):
        progress = np.asarray(progress, dtype=float)
        squared = self.inner * self.inner + progress * self._range
        with np.errstate(invalid="ignore"):
            radius = np.where(squared > 0, np.sqrt(np.where(squared > 0, squared, np.nan)), np.nan)
        return radius, self.angle_at(radius)


def _primitive(radius, b):
    return 0.5 * (radius * np.sqrt(radius * radius + b * b) + b * b * np.arcsinh(radius / b))


class _Archimedean:

    def __init__(self, inner: float, outer: float, length: float):
        self.inner, self.outer, self.length = inner, outer, length

        def excess(b):
            return (_primitive(outer, b) - _primitive(inner, b)) / b - length

        guess = (outer * outer - inner * inner) / (2 * length)
        low, high = guess * 0.5, guess * 2
        while excess(high) > 0:
            high *= 2
        while excess(low) < 0:
            low /= 2
        self.b = brentq(excess, low, high, xtol=1e-18, rtol=8.9e-16, maxiter=200)
        self._inner_primitive = _primitive(inner, self.b)

    def growth_at(self, radius):
        return np.full_like(np.asarray(radius, dtype=float), self.b)

    def progress(self, radius):
        radius = np.asarray(radius, dtype=float)
        with np.errstate(invalid="ignore"):
            arc = (_primitive(radius, self.b) - self._inner_primitive) / self.b
        return np.where(radius > 0, arc / self.length, np.nan)

    def angle_at(self, radius):
        return (np.asarray(radius, dtype=float) - self.inner) / self.b

    def at_progress(self, progress):
        progress = np.asarray(progress, dtype=float)
        b = self.b
        target = self._inner_primitive + progress * self.length * b
        squared = self.inner * self.inner + 2 * b * progress * self.length
        radius = np.sqrt(np.maximum(squared, b * b))
        for _ in range(4):
            step = (_primitive(radius, b) - target) / np.sqrt(radius * radius + b * b)
            nxt = radius - step
            radius = np.where(nxt > 0, nxt, radius / 2)
        radius = np.where(target > 0, radius, np.nan)
        return radius, self.angle_at(radius)


class _CubicPitch:

    PANELS = 32
    KNOT_MM = 1.0

    def __init__(self, inner, outer, length, c1, c2, c3):
        self.inner, self.outer, self.length = inner, outer, length
        self.c1, self.c2, self.c3 = c1, c2, c3
        self._width = outer - inner
        self._radius_limits = self._valid_radius_interval()

        edges = np.linspace(inner, outer, self.PANELS + 1)
        half = np.diff(edges) / 2
        nodes = (edges[:-1] + half)[:, None] + half[:, None] * _GL_X
        weights = half[:, None] * _GL_W
        ratio = nodes / self._q(nodes)
        thin = float(np.sum(weights * ratio))
        lower, upper = 0.0, length / thin
        inverse = upper
        for _ in range(80):
            z = ratio * inverse
            root = np.sqrt(1 + z * z)
            error = float(np.sum(weights * root)) - length
            if abs(error) <= length * 2e-15:
                break
            if error > 0:
                upper = inverse
            else:
                lower = inverse
            derivative = float(np.sum(weights * ratio * z / root))
            nxt = inverse - error / derivative
            if not math.isfinite(nxt) or nxt <= lower or nxt >= upper:
                nxt = lower + (upper - lower) / 2
            if nxt == inverse:
                break
            inverse = nxt
        if not math.isfinite(inverse) or inverse <= 0:
            raise GeometryError("These pitch coefficients have no solution for this track length.")
        self.inverse_b = inverse
        self.b = 1 / inverse
        self._table = None

    def _q(self, radius):
        return _polynomial(self.c1, self.c2, self.c3, (radius - self.inner) / self._width)

    def _valid_radius_interval(self):
        roots = np.roots([self.c3, self.c2, self.c1, 1.0])
        real = roots[np.abs(roots.imag) < 1e-12].real if roots.size else np.array([])
        below = real[real < 0]
        above = real[real > 1]
        low = self.inner + (below.max() * self._width if below.size else -math.inf)
        high = self.inner + (above.min() * self._width if above.size else math.inf)
        return max(low, 0.0), high

    def growth_at(self, radius):
        return self.b * self._q(np.asarray(radius, dtype=float))

    def _arc_angle(self, radius):
        radius = np.asarray(radius, dtype=float)
        arc = np.full(radius.shape, np.nan)
        angle = np.full(radius.shape, np.nan)
        low, high = self._radius_limits
        valid = (radius > low) & (radius < high)
        if not valid.any():
            return arc, angle
        wanted = radius[valid]
        first = min(float(wanted.min()), self.inner)
        last = max(float(wanted.max()), self.inner)
        knots = np.arange(first, last + self.KNOT_MM, self.KNOT_MM)
        knots = knots[knots < last]
        breaks = np.unique(np.concatenate([wanted, knots, [self.inner]]))
        if breaks.size == 1:
            arc[valid] = 0.0
            angle[valid] = 0.0
            return arc, angle
        half = np.diff(breaks) / 2
        nodes = (breaks[:-1] + half)[:, None] + half[:, None] * _GL_X
        q = self._q(nodes)
        z = nodes * self.inverse_b / q
        weights = half[:, None] * _GL_W
        arc_cells = np.sum(weights * np.sqrt(1 + z * z), axis=1)
        angle_cells = np.sum(weights * self.inverse_b / q, axis=1)
        arc_sum = np.concatenate([[0.0], np.cumsum(arc_cells)])
        angle_sum = np.concatenate([[0.0], np.cumsum(angle_cells)])
        origin = np.searchsorted(breaks, self.inner)
        index = np.searchsorted(breaks, wanted)
        arc[valid] = arc_sum[index] - arc_sum[origin]
        angle[valid] = angle_sum[index] - angle_sum[origin]
        return arc, angle

    def progress(self, radius):
        return self._arc_angle(radius)[0] / self.length

    def angle_at(self, radius):
        return self._arc_angle(radius)[1]

    def _arc_table(self):
        if self._table is None:
            low, high = self._radius_limits
            first = max(low + 1e-6, 0.05, self.inner - self._width)
            last = min(high - 1e-6, self.outer + self._width)
            radii = np.linspace(first, last, int((last - first) / 0.5) + 2)
            self._table = (self._arc_angle(radii)[0], radii)
        return self._table

    def at_progress(self, progress):
        progress = np.asarray(progress, dtype=float)
        target = progress * self.length
        arcs, radii = self._arc_table()
        outside = (target < arcs[0]) | (target > arcs[-1]) | ~np.isfinite(target)
        radius = np.interp(target, arcs, radii)
        for _ in range(4):
            arc = self._arc_angle(radius)[0]
            growth = self.growth_at(radius)
            radius = radius - (arc - target) * growth / np.hypot(radius, growth)
        arc, angle = self._arc_angle(radius)
        radius = np.where(outside, np.nan, radius)
        angle = np.where(outside, np.nan, angle)
        return radius, angle


class Geometry:

    def __init__(self, params: Params):
        params.validate()
        self.params = params
        if params.kind == CD:
            self._model = _EqualArea(params)
        elif params.kind == BLU_RAY or params.is_constant_pitch:
            self._model = _Archimedean(params.inner, params.outer, params.track_length)
        else:
            self._model = _CubicPitch(
                params.inner, params.outer, params.track_length,
                params.pitch_linear, params.pitch_quadratic, params.pitch_cubic,
            )

    @property
    def growth(self) -> float:
        return self._model.b

    def growth_at(self, radius):
        return self._model.growth_at(radius)

    def progress(self, radius):
        return self._model.progress(radius)

    def angle_at(self, radius):
        return self._model.angle_at(radius)

    def at_progress(self, progress):
        return self._model.at_progress(progress)


class CrossModel:

    def __init__(self, generated: Geometry, actual: Geometry):
        self.generated = generated
        self.actual = actual
        self.identical = generated.params == actual.params

    def phase(self, radius):
        radius = np.asarray(radius, dtype=float)
        if self.identical:
            valid = radius > 0
            if not self.actual.params.is_constant_pitch:
                valid = valid & np.isfinite(self.actual.progress(radius))
            return np.where(valid, 0.0, np.nan), np.where(valid, radius, np.nan)
        progress = self.actual.progress(radius)
        source, source_angle = self.generated.at_progress(progress)
        return self.actual.angle_at(radius) - source_angle, source


class PhaseTable:

    STEP_MM = 0.1

    def __init__(self, generated: Geometry, actual: Geometry, first: float, last: float):
        first, last = first - self.STEP_MM, last + self.STEP_MM
        count = int(math.ceil((last - first) / self.STEP_MM)) + 1
        self.knots = np.linspace(first, last, count)
        phase, _ = CrossModel(generated, actual).phase(self.knots)
        self.valid = bool(np.isfinite(phase).all())
        self._spline = CubicSpline(self.knots, phase) if self.valid else None

    def phase(self, radius):
        return self._spline(radius)

    def rate(self, radius):
        return self._spline(radius, 1)


def arm_start_angle(arm: int = 0, rotation: float = 0.0) -> float:
    return -math.pi / 2 + arm * math.pi / 2 + rotation


def predict_points(generated: Geometry, actual: Geometry, radius, rotation=0.0, arm=0):
    radius = np.asarray(radius, dtype=float)
    phase, _ = CrossModel(generated, actual).phase(radius)
    angle = arm_start_angle(arm, rotation) + phase
    return np.column_stack([radius * np.cos(angle), radius * np.sin(angle)])


def map_source_points(generated: Geometry, actual: Geometry, source_radius, rotation=0.0, arm=0):
    source_radius = np.asarray(source_radius, dtype=float)
    progress = np.clip(generated.progress(source_radius), 0, 1)
    radius, angle = actual.at_progress(progress)
    theta = arm_start_angle(arm, rotation) + angle - generated.angle_at(source_radius)
    return np.column_stack([radius * np.cos(theta), radius * np.sin(theta)])
