"""Parametric hull form shared by the block generator (block boxes) and the model generator (shell surfaces).

Ship coordinates (m): x forward from the aft end, y to port, z up from the baseline.
"""
import math

# Longitudinal zones as fractions of LOA, aft to fore.
STERN_END, ENGINE_END, HOLDS_END = 0.08, 0.20, 0.88


class HullForm:
    """Half-breadth hb(x, z): parallel body with bilge radius, fine bow with bulb, stern aperture over a skeg."""

    def __init__(self, length, beam, depth, draft):
        self.L, self.B, self.D, self.T = length, beam, depth, draft
        self.xs, self.xb = length * STERN_END, length * HOLDS_END
        self.bilge = min(2.5, beam * 0.055)
        # Stern aperture: below the counter the hull narrows to a skeg so propeller and rudder are clear.
        self.counter = draft * 0.92
        self.aperture_x = self.xs * 0.72
        # Bulb: centre height, vertical semi-axis, max half-width.
        self.bulb = (draft * 0.5, draft * 0.42, beam * 0.08)

    def hb(self, x, z):
        half, D = self.B / 2, self.D
        zr = min(1.0, max(0.0, z / D))
        if x < self.xs:  # stern
            u = 1 - x / self.xs
            width = half * min(1.0, 0.55 + 0.45 * x / self.xs)
            if x < self.aperture_x and z < self.counter:
                blend = max(0.0, min(1.0, (z - (self.counter - 2.5)) / 2.5))
                width = 0.7 + (width * (1 - 0.6 * u) - 0.7) * blend ** 2
            else:
                width *= 1 - 0.85 * u ** 0.8 * (1 - zr) ** 1.5
        elif x > self.xb:  # bow: waterlines close smoothly to a sharp stem; forefoot narrower; bulb below
            t = min(1.0, (x - self.xb) / (self.L - self.xb))
            width = half * max(0.0, 1 - t ** 2.2) ** 0.75
            width *= 1 - 0.6 * t * (1 - zr ** 0.4)
            zc, rz, rb = self.bulb
            if abs(z - zc) < rz and x > self.L - 30:
                along = min(1.0, (self.L - x) / 18)
                radius = rb * math.sqrt(max(0.0, 1 - ((z - zc) / rz) ** 2)) * (0.25 + 0.75 * math.sqrt(along))
                width = max(width, radius)
        else:
            width = half
        if z < self.bilge and self.xs <= x <= self.xb:
            width -= self.bilge - math.sqrt(max(0.0, self.bilge ** 2 - (self.bilge - z) ** 2))
        return max(0.3, width)

    def max_half_breadth(self, x0, x1, z0, z1, samples=12):
        """Largest half-breadth over a block's x/z range (for its bounding box)."""
        xs = [x0 + (x1 - x0) * i / samples for i in range(samples + 1)]
        zs = [z0 + (z1 - z0) * i / samples for i in range(samples + 1)]
        return max(self.hb(x, z) for x in xs for z in zs)

    def paint(self, z):
        if z < self.T - 1.0:
            return 'antifouling'
        return 'boottop' if z < self.T + 1.5 else 'topside'
