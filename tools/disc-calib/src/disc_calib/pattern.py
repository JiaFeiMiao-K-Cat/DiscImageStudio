from __future__ import annotations

import json
import math
from dataclasses import dataclass, field, asdict

import numpy as np

from .parallel import parallel_rows

PIXEL_SIZE = 2400
ARM_WIDTH_MM = 0.5
SOLID_RING_MM = 0.4
RING_PERIOD_MM = 5.0
RING_LANE_MM = 1.0
HALF_DISC_START_DEG = 45.0


@dataclass
class Band:
    kind: str
    center_mm: float
    height_mm: float


@dataclass
class Level:

    inner_mm: float
    outer_mm: float
    sectors: int
    bits: list[int] = field(default_factory=list)
    window: int = 0


@dataclass
class PatternSpec:
    inner_mm: float
    outer_mm: float
    arms: int = 4
    arm_width_mm: float = ARM_WIDTH_MM
    half_disc: bool = True
    half_disc_start_deg: float = HALF_DISC_START_DEG
    ring_lane_mm: float = RING_LANE_MM
    bands: list[Band] = field(default_factory=list)
    levels: list[Level] = field(default_factory=list)
    design: str = "halfdisc"

    def to_json(self) -> str:
        return json.dumps(asdict(self), ensure_ascii=False, indent=1)

    @classmethod
    def from_json(cls, text: str) -> "PatternSpec":
        node = json.loads(text)
        node["bands"] = [Band(**b) for b in node["bands"]]
        node["levels"] = [Level(**level) for level in node.get("levels", [])]
        return cls(**node)

    def solid_ring_radii(self) -> np.ndarray:
        return np.array([b.center_mm for b in self.bands if b.kind == "solid"])

    def edge_angles(self) -> list[tuple[float, int]]:
        start = math.radians(self.half_disc_start_deg)
        return [(start, +1), (start + math.pi, -1)]


def design(inner_mm: float = 24.0, outer_mm: float = 58.0) -> PatternSpec:
    spec = PatternSpec(inner_mm, outer_mm)
    r = inner_mm + 1.0
    while r <= outer_mm - 1.0 + 1e-9:
        spec.bands.append(Band("solid", r, SOLID_RING_MM))
        r += RING_PERIOD_MM
    return spec


CODE_RING_RADII = (25.0, 30.5, 39.0, 42.5, 49.5, 55.0)
CODE_LEVELS = ((2, 0), (8, 3), (32, 5), (128, 7))


def de_bruijn(k: int, n: int) -> list[int]:
    a = [0] * (k * n)
    out: list[int] = []

    def step(t, p):
        if t > n:
            if n % p == 0:
                out.extend(a[1:p + 1])
            return
        a[t] = a[t - p]
        step(t + 1, p)
        for j in range(a[t - p] + 1, k):
            a[t] = j
            step(t + 1, t)

    step(1, 1)
    return out


def design_code(inner_mm: float = 24.0, outer_mm: float = 58.0) -> PatternSpec:
    spec = PatternSpec(inner_mm, outer_mm, arms=0, half_disc=False, design="code")
    radii = [r for r in CODE_RING_RADII if inner_mm + 0.6 < r < outer_mm - 0.6]
    for r in radii:
        spec.bands.append(Band("solid", r, SOLID_RING_MM))
    margin = SOLID_RING_MM / 2 + spec.ring_lane_mm / 2
    edges = [inner_mm] + radii + [outer_mm]
    for lo, hi in zip(edges[:-1], edges[1:]):
        a = lo + (margin if lo in radii else 0.0)
        b = hi - (margin if hi in radii else 0.0)
        width = b - a
        count = 4 if width >= 6 else 2 if width >= 3 else 1
        cuts = np.linspace(a, b, count + 1)
        for (sectors, window), e0, e1 in zip(CODE_LEVELS[:count], cuts[:-1], cuts[1:]):
            bits = de_bruijn(2, window) if window else []
            spec.levels.append(Level(float(e0), float(e1), sectors, bits, window))
    return spec


def render(spec: PatternSpec, size: int = PIXEL_SIZE, supersample: int = 2) -> np.ndarray:
    n = size * supersample
    centre = (n - 1) / 2
    scale = spec.outer_mm / ((size - 1) / 2) / supersample
    coords = (np.arange(n) - centre) * scale
    output = np.empty((size, size), dtype=np.uint8)
    start_angle = math.radians(spec.half_disc_start_deg)

    def work(first, last):
        x, y = np.meshgrid(coords, coords[first * supersample:last * supersample])
        r = np.hypot(x, y)
        theta = np.arctan2(y, x)
        dark = np.zeros(r.shape, dtype=bool)
        in_annulus = (r >= spec.inner_mm) & (r <= spec.outer_mm)
        lane = np.zeros(r.shape, dtype=bool)
        for band in spec.bands:
            lane |= np.abs(r - band.center_mm) <= spec.ring_lane_mm / 2
        if spec.half_disc:
            dark |= in_annulus & ~lane & (np.mod(theta - start_angle, 2 * math.pi) < math.pi)
        for level in spec.levels:
            sector = (np.mod(theta - start_angle, 2 * math.pi) / (2 * math.pi) * level.sectors).astype(int) % level.sectors
            bit = (sector % 2 == 0) if not level.bits else (np.asarray(level.bits)[sector] == 1)
            dark |= in_annulus & ~lane & (r >= level.inner_mm) & (r < level.outer_mm) & bit
        for band in spec.bands:
            dark |= in_annulus & (np.abs(r - band.center_mm) <= band.height_mm / 2)
        for arm in range(spec.arms):
            start = -math.pi / 2 + arm * math.pi / 2
            offset = np.angle(np.exp(1j * (theta - start)))
            on_arm = in_annulus & (np.abs(r * offset) <= spec.arm_width_mm / 2) & (np.abs(offset) < math.pi / 2)
            dark[on_arm] = ~dark[on_arm]
        coarse = dark.reshape(last - first, supersample, size, supersample).mean(axis=(1, 3))
        output[first:last] = np.where(coarse >= 0.5, 0, 255).astype(np.uint8)

    parallel_rows(work, size, 16)
    return output
