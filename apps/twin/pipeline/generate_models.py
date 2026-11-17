"""Generate plausible, painted 3D block models (Wavefront OBJ + MTL) from a generate_blocks.py JSONL file.

These are illustrative meshes, not design CAD. Structure follows typical shipbuilding layout (shell, inner
bottom, floors, girders, web frames, stringers, deck longitudinals); the hull form has a bulbous bow, a stern
aperture with propeller and rudder; LNG-carrier deck outfitting (raised trunk deck, liquid/vapour domes,
pipe rack, manifold, compressor house), forecastle mooring gear and an accommodation block with bridge wings,
funnel and mast. Faces carry paint materials (usemtl): antifouling below the waterline, boot-top, topside,
deck, primer inside, white superstructure. Every plate is a thin double-sided surface.

Output per block: <out>/<model_asset> (e.g. models/SN2040/SN2040-241C1.obj) and <out>/<hull>/paint.mtl.
Vertices are metres relative to the block's centre of gravity, in OBJ axes (Y up, right-handed):
obj = (ship x, ship z, -ship y), ship x forward, y to port, z up. Deck outfitting may stand up to ~6 m above
the block box. <out>/<hull>/<hull>-ship.obj is a whole-ship preview in absolute ship coordinates.
"""

import argparse
import json
import math
from pathlib import Path

from generate_blocks import section_params
from hullform import HullForm
from paths import MODELS

FLAT_BAR = 0.35      # stiffener web height (m)
STIFF_SPACING = 0.9  # longitudinal stiffener spacing (m)
WEB_FRAMES = 4       # web frame / floor every n frames
TRUNK_HEIGHT = 3.0   # LNG membrane trunk deck above the main deck (m)

# Paint palette (diffuse RGB). The Unity app keeps the same names (ShipPalette).
PAINT = {
    'antifouling': (0.55, 0.13, 0.10), 'boottop': (0.08, 0.08, 0.09), 'topside': (0.16, 0.22, 0.33),
    'deck': (0.36, 0.45, 0.38), 'primer': (0.56, 0.54, 0.50), 'trunk': (0.62, 0.65, 0.66),
    'fitting': (0.86, 0.87, 0.87), 'pipe': (0.74, 0.76, 0.78), 'machinery': (0.24, 0.35, 0.30),
    'superstructure': (0.93, 0.93, 0.91), 'glass': (0.10, 0.15, 0.22), 'funnel': (0.93, 0.93, 0.91),
    'funnelband': (0.10, 0.32, 0.62), 'bronze': (0.76, 0.56, 0.26), 'weld': (0.30, 0.27, 0.25),
}


class Mesh:
    def __init__(self, origin):
        self.origin = origin  # ship coordinates subtracted from every vertex
        self.vertices, self.faces = [], []  # faces: (a, b, c, material)
        self.material = 'primer'

    def _add(self, points):
        base = len(self.vertices) + 1
        ox, oy, oz = self.origin
        for x, y, z in points:
            self.vertices.append((x - ox, z - oz, -(y - oy)))
        return base

    def quad(self, a, b, c, d, material=None):
        """Double-sided quad from ship-coordinate corners (skips degenerate quads)."""
        if _area(a, b, c) + _area(a, c, d) < 1e-6:
            return
        m, base = material or self.material, self._add((a, b, c, d))
        self.faces += [(base, base + 1, base + 2, m), (base, base + 2, base + 3, m),
                       (base, base + 2, base + 1, m), (base, base + 3, base + 2, m)]

    def tri(self, a, b, c, material=None):
        if _area(a, b, c) < 1e-6:
            return
        m, base = material or self.material, self._add((a, b, c))
        self.faces += [(base, base + 1, base + 2, m), (base, base + 2, base + 1, m)]

    def box(self, x0, x1, y0, y1, z0, z1, material=None):
        p = [(x, y, z) for z in (z0, z1) for y in (y0, y1) for x in (x0, x1)]
        for a, b, c, d in ((0, 1, 3, 2), (4, 5, 7, 6), (0, 1, 5, 4), (2, 3, 7, 6), (0, 2, 6, 4), (1, 3, 7, 5)):
            self.quad(p[a], p[b], p[c], p[d], material)

    def cylinder(self, centre, axis, radius, length, material=None, sides=16, caps=True):
        """Cylinder along 'x', 'y' or 'z', starting at centre and extending +length along the axis."""
        def point(angle, along):
            u, v = radius * math.cos(angle), radius * math.sin(angle)
            x, y, z = centre
            return {'x': (x + along, y + u, z + v), 'y': (x + u, y + along, z + v), 'z': (x + u, y + v, z + along)}[axis]
        def hub(along):
            offset = {'x': (along, 0, 0), 'y': (0, along, 0), 'z': (0, 0, along)}[axis]
            return tuple(c + o for c, o in zip(centre, offset))
        angles = [2 * math.pi * i / sides for i in range(sides + 1)]
        for a0, a1 in zip(angles, angles[1:]):
            self.quad(point(a0, 0), point(a1, 0), point(a1, length), point(a0, length), material)
            if caps:
                for along in (0, length):
                    self.tri(hub(along), point(a0, along), point(a1, along), material)

    def write(self, path, header, mtl=None):
        lines = [f'# {line}' for line in header]
        if mtl:
            lines.append(f'mtllib {mtl}')
        lines += [f'v {x:.3f} {y:.3f} {z:.3f}' for x, y, z in self.vertices]
        current = None
        for a, b, c, m in sorted(self.faces, key=lambda face: face[3]):  # one usemtl run per material
            if m != current:
                lines.append(f'usemtl {m}')
                current = m
            lines.append(f'f {a} {b} {c}')
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('\n'.join(lines) + '\n', encoding='utf-8')


def _area(a, b, c):
    u = [b[i] - a[i] for i in range(3)]
    v = [c[i] - a[i] for i in range(3)]
    cross = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
    return math.sqrt(sum(value * value for value in cross)) / 2


def steps(low, high, size, breaks=()):
    count = max(1, math.ceil((high - low) / size - 1e-9))
    values = {low + (high - low) * i / count for i in range(count + 1)}
    values |= {b for b in breaks if low + 1e-6 < b < high - 1e-6}
    return sorted(values)


def inside(value, low, high):
    return low + 1e-6 < value < high - 1e-6


class Builder:
    """Plates clipped to the block box and to the hull form."""

    def __init__(self, mesh, box, hull):
        self.m, self.box, self.hull = mesh, box, hull
        self.paint_breaks = (hull.T - 1.0, hull.T + 1.5)

    def clip_y(self, x, z, y0, y1):
        hb = self.hull.hb(x, z)
        return max(y0, self.box['y_min'], -hb), min(y1, self.box['y_max'], hb)

    def yz_plate(self, x, y0, y1, z0, z1, dz=0.6, material=None):
        """Transverse plate (floor, web frame, bulkhead) at x, clipped by the hull in z strips."""
        zs = steps(z0, z1, dz, self.paint_breaks if material == 'shell' else ())
        for za, zb in zip(zs, zs[1:]):
            ya0, ya1 = self.clip_y(x, za, y0, y1)
            yb0, yb1 = self.clip_y(x, zb, y0, y1)
            if ya1 > ya0 and yb1 > yb0:
                paint = self.hull.paint((za + zb) / 2) if material == 'shell' else material
                self.m.quad((x, ya0, za), (x, ya1, za), (x, yb1, zb), (x, yb0, zb), paint)

    def xy_plate(self, z, x0, x1, y0, y1, dx=1.6, material=None):
        """Horizontal plate (bottom, inner bottom, deck, flat) at z, clipped by the hull in x strips."""
        xs = steps(x0, x1, dx)
        for xa, xb in zip(xs, xs[1:]):
            ya0, ya1 = self.clip_y(xa, z, y0, y1)
            yb0, yb1 = self.clip_y(xb, z, y0, y1)
            if ya1 > ya0 and yb1 > yb0:
                self.m.quad((xa, ya0, z), (xb, yb0, z), (xb, yb1, z), (xa, ya1, z), material)

    def xz_plate(self, y, x0, x1, z0, z1, dx=1.6, dz=1.2, material=None):
        """Longitudinal plate (girder, inner hull, stiffener) at y, kept where it lies inside the hull."""
        xs, zs = steps(x0, x1, dx), steps(z0, z1, dz)
        for xa, xb in zip(xs, xs[1:]):
            for za, zb in zip(zs, zs[1:]):
                if all(abs(y) <= self.hull.hb(px, pz) + 1e-6 for px in (xa, xb) for pz in (za, zb)):
                    self.m.quad((xa, y, za), (xb, y, za), (xb, y, zb), (xa, y, zb), material)

    def shell(self, sign, x0, x1, z0, z1, dx=1.6, dz=0.8):
        """Painted side shell y = sign * hb(x, z) over the block range, clipped to the block box."""
        def point(x, z):
            y = sign * self.hull.hb(x, z)
            return (x, min(max(y, self.box['y_min']), self.box['y_max']), z)
        xs, zs = steps(x0, x1, dx), steps(z0, z1, dz, self.paint_breaks)
        for xa, xb in zip(xs, xs[1:]):
            for za, zb in zip(zs, zs[1:]):
                self.m.quad(point(xa, za), point(xb, za), point(xb, zb), point(xa, zb), self.hull.paint((za + zb) / 2))
        # Erection butts and seams: a narrow weld band on the shell along the block edges, just proud of the plate.
        out = lambda p, d=0.03: (p[0], p[1] + sign * d, p[2])
        for x in (x0 + 0.06, x1 - 0.06):
            for za, zb in zip(zs, zs[1:]):
                self.m.quad(out(point(x - 0.06, za)), out(point(x + 0.06, za)), out(point(x + 0.06, zb)), out(point(x - 0.06, zb)), 'weld')
        for z in (z0 + 0.06, z1 - 0.06):
            for xa, xb in zip(xs, xs[1:]):
                self.m.quad(out(point(xa, z - 0.06)), out(point(xb, z - 0.06)), out(point(xb, z + 0.06)), out(point(xa, z + 0.06)), 'weld')

    def bulwark(self, sign, x0, x1, z, height=1.6, dx=1.6):
        xs = steps(x0, x1, dx)
        for xa, xb in zip(xs, xs[1:]):
            ya, yb = sign * self.hull.hb(xa, z), sign * self.hull.hb(xb, z)
            self.m.quad((xa, ya, z), (xb, yb, z), (xb, yb, z + height), (xa, ya, z + height), 'topside')

    def touches_side(self, sign):
        b, x_mid, z_mid = self.box, (self.box['x_min'] + self.box['x_max']) / 2, (self.box['z_min'] + self.box['z_max']) / 2
        edge = b['y_max'] if sign > 0 else -b['y_min']
        return edge >= self.hull.hb(x_mid, z_mid) - 0.5 or edge >= self.hull.B / 2 - 0.5


def build_block(builder, code, box, frame, params, depth, layout):
    h_db, w_side, _, _ = params
    b, hull, m = builder, builder.hull, builder.m
    x0, x1, y0, y1, z0, z1 = (box[k] for k in ('x_min', 'x_max', 'y_min', 'y_max', 'z_min', 'z_max'))
    zone, group, part, seg = code[0], code[1], code[2], int(code[4:] or 1)
    webs = [x for x in steps(x0, x1, frame * WEB_FRAMES)[1:-1]] + [x1 - 0.05]
    sides = [s for s in (1, -1) if b.touches_side(s)]
    m.material = 'primer'

    if zone == '5':
        deckhouse(b, box, int(part), layout['house_tiers'])
        return

    if part == '1':  # double bottom
        b.xy_plate(0, x0, x1, y0, y1, material='antifouling')
        for s in sides:
            b.shell(s, x0, x1, 0, z1)
        b.xy_plate(z1, x0, x1, y0, y1)
        for x in webs:
            b.yz_plate(x, y0, y1, 0, z1, dz=0.4)
        for y in steps(y0, y1, 4.6)[1:-1] + ([0.0] if inside(0, y0, y1) else []):
            b.xz_plate(y, x0, x1, 0, z1, dz=z1)
        for y in steps(y0, y1, STIFF_SPACING)[1:-1]:
            b.xz_plate(y, x0, x1, 0, FLAT_BAR, dz=FLAT_BAR)
            b.xz_plate(y, x0, x1, z1 - FLAT_BAR, z1, dz=FLAT_BAR)
    elif part == '2':  # double side
        outer = 1 if y1 > 0 else -1
        b.shell(outer, x0, x1, z0, z1)
        b.xz_plate(y0 if outer > 0 else y1, x0, x1, z0, z1, dz=z1 - z0)
        for x in webs:
            b.yz_plate(x, y0, y1, z0, z1, dz=z1 - z0)
        for z in steps(z0, z1, 5.0)[1:-1]:
            b.xy_plate(z, x0, x1, y0, y1)
        for x in steps(x0, x1, frame)[1:-1]:
            edge = outer * (hull.B / 2 - FLAT_BAR)
            b.yz_plate(x, min(edge, outer * hull.B / 2), max(edge, outer * hull.B / 2), z0, z1, dz=z1 - z0)
    elif part == '3':  # upper deck
        b.xy_plate(z1, x0, x1, y0, y1, material='deck')
        for x in webs:
            b.yz_plate(x, y0, y1, z1 - 1.5, z1, dz=1.5)
        for y in steps(y0, y1, 8.0)[1:-1] + ([0.0] if inside(0, y0, y1) else []):
            b.xz_plate(y, x0, x1, z0 + 0.5, z1, dz=z1 - z0 - 0.5)
        for y in steps(y0, y1, STIFF_SPACING)[1:-1]:
            b.xz_plate(y, x0, x1, z1 - FLAT_BAR, z1, dz=FLAT_BAR)
        if zone == '2':
            lng_deck(b, x0, x1, z1, int(group), seg, layout)
        elif zone == '3':
            engine_casing(b, x0, x1, z1, seg, layout)
    else:  # curved end blocks (4 lower, 5 upper)
        sign = 1 if y1 > 0 else -1
        b.shell(sign, x0, x1, z0, z1)
        if z0 <= 1e-6:
            b.xy_plate(0, x0, x1, y0, y1, dx=0.8, material='antifouling')
        flats = [z for z in steps(0, depth, depth / 4) if inside(z, z0, z1)]
        for z in flats:
            b.xy_plate(z, x0, x1, y0, y1, dx=0.8)
        if part == '5':
            b.xy_plate(z1, x0, x1, y0, y1, dx=0.8, material='deck')
            b.bulwark(sign, x0, x1, z1)
        if sign > 0:
            b.xz_plate(0.0, x0, x1, z0, z1)
        if x0 <= 1e-6:
            b.yz_plate(0.0, y0, y1, z0, z1, material='shell')  # transom
        for x in webs:
            b.yz_plate(x, y0, y1, z0, z1)
        for x in steps(x0, x1, frame)[1:-1]:
            hb_mid = hull.hb(x, (z0 + z1) / 2)
            edge = max(0.0, hb_mid - FLAT_BAR)
            b.yz_plate(x, edge if sign > 0 else -hb_mid, hb_mid if sign > 0 else -edge, z0, z1)
        if zone == '4' and part == '4' and sign > 0 and x0 <= 1e-6:
            propeller_and_rudder(m, hull)
        if part == '5' and seg == (2 if zone == '1' else 1):
            mooring(m, sign, x0, x1, z1)


def lng_deck(b, x0, x1, deck, hold, seg, layout):
    """Membrane LNG carrier: raised trunk deck, domes, pipe rack, manifold, compressor house."""
    m, B = b.m, b.hull.B
    half = B * 0.36
    top = deck + TRUNK_HEIGHT
    b.xy_plate(top, x0, x1, -half, half, material='deck')
    for y in (-half, half):
        b.xz_plate(y, x0, x1, deck, top, dz=TRUNK_HEIGHT, material='trunk')
    segs = layout['hold_segments'][hold]
    if seg == 1 and hold == layout['holds']:      # aft end of the cargo area
        b.yz_plate(x0 + 0.05, -half, half, deck, top, material='trunk')
    if seg == segs and hold == 1:                  # forward end
        b.yz_plate(x1 - 0.05, -half, half, deck, top, material='trunk')
    mid = (x0 + x1) / 2
    if seg == max(1, segs // 2):
        m.cylinder((mid, 0.0, top), 'z', 1.9, 2.6, 'fitting')          # liquid dome
        m.cylinder((mid, 0.0, top + 2.6), 'z', 0.9, 1.4, 'pipe')
    if seg == max(1, segs // 2) + 1:
        m.cylinder((mid, 0.0, top), 'z', 1.3, 2.0, 'fitting')          # vapour dome
    # Pipe rack on the starboard side of the trunk: supports every ~6 m, three lines along the ship.
    rack_y = -half * 0.55
    for x in steps(x0, x1, 6.0)[:-1]:
        m.box(x + 0.2, x + 0.5, rack_y - 1.8, rack_y + 1.8, top, top + 1.6, 'fitting')
    for i, (dy, r) in enumerate(((-1.2, 0.42), (0.0, 0.32), (1.2, 0.25))):
        m.cylinder((x0, rack_y + dy, top + 1.6 + r), 'x', r, x1 - x0, 'pipe', sides=10, caps=False)
    m.box(x0, x1, half * 0.15 - 0.6, half * 0.15 + 0.6, top + 0.9, top + 1.0, 'fitting')  # catwalk
    if hold == layout['manifold_hold'] and seg == 1:
        for y_sign in (1, -1):  # manifold: cross-over lines to both sides with a platform
            for k in range(4):
                xm = x0 + 3 + k * 1.6
                m.cylinder((xm, 0.0, deck + 1.6), 'y', 0.35, y_sign * (B / 2 - 2.0), 'pipe', sides=10)
            m.box(x0 + 1.5, x0 + 10, y_sign * (B / 2 - 6) - 2, y_sign * (B / 2 - 6) + 2, deck + 0.9, deck + 1.0, 'fitting')
    if hold == layout['holds'] and seg == 1:     # compressor house on the trunk deck
        m.box(x0 + 1.0, x1 - 1.0, -7.0, 7.0, top, top + 6.0, 'fitting')
        for k in range(3):
            m.cylinder((x0 + 3 + k * 3, 4.0, top + 6.0), 'z', 0.45, 2.2, 'pipe', sides=10)


def engine_casing(b, x0, x1, deck, seg, layout):
    if seg == 1:
        b.m.box(x0 + 2, x1 - 1, -6, 6, deck, deck + 3.5, 'superstructure')  # engine casing / store


def mooring(m, sign, x0, x1, deck):
    """Windlass or mooring winch with drums, plus bitts near the side."""
    xm, y = (x0 + x1) / 2, sign * 5.5
    m.box(xm - 2.2, xm + 2.2, y - 1.5, y + 1.5, deck, deck + 1.0, 'machinery')
    for dx in (-1.3, 1.3):
        m.cylinder((xm + dx, y - 1.4, deck + 2.1), 'y', 1.0, 2.8, 'machinery', sides=14)
    for dx in (-3.5, 3.5):
        m.cylinder((xm + dx, sign * 9.0, deck), 'z', 0.35, 1.2, 'machinery', sides=10)


def propeller_and_rudder(m, hull):
    """Five-bladed fixed-pitch propeller in the stern aperture and a spade rudder aft of it."""
    T = hull.T
    hub_x, hub_z, radius = hull.aperture_x * 0.62, T * 0.42, min(T * 0.38, hull.counter * 0.48)
    m.cylinder((hub_x - 0.9, 0.0, hub_z), 'x', 0.75, 1.8, 'bronze')
    m.cylinder((hub_x + 0.9, 0.0, hub_z), 'x', 0.45, 3.5, 'antifouling', sides=12)  # shaft into the skeg
    for k in range(5):  # skewed, wide blades: chord peaks around 0.6 R, pitch twists the blade along x
        a = 2 * math.pi * k / 5
        rows = []
        for i in range(9):
            t = i / 8
            r = 0.7 + (radius - 0.7) * t
            chord = 2.6 * math.sin(math.pi * (0.18 + 0.8 * t)) * (1 - 0.25 * t) + 0.25
            skew = 0.35 * t * t
            half = chord / 2 / r
            pitch = 0.55 * (1 - 0.5 * t)
            lead = (hub_x - pitch, r * math.cos(a + skew + half), hub_z + r * math.sin(a + skew + half))
            trail = (hub_x + pitch, r * math.cos(a + skew - half), hub_z + r * math.sin(a + skew - half))
            rows.append((lead, trail))
        for (l0, t0), (l1, t1) in zip(rows, rows[1:]):
            m.quad(l0, l1, t1, t0, 'bronze')
    rx0, rx1 = max(0.3, hub_x - radius - 4.5), hub_x - radius - 0.6
    zr0, zr1 = 0.8, hull.counter + 0.5
    m.box(rx0, rx1, -0.45, 0.45, zr0, zr1, 'antifouling')                       # rudder blade
    m.cylinder(((rx0 + rx1) / 2, 0.0, zr1), 'z', 0.5, 2.0, 'antifouling', sides=10)  # rudder stock


def deckhouse(b, box, tier, tiers):
    """Accommodation tier: white walls with glazed window band; top tier: bridge, wings, funnel, mast."""
    m = b.m
    x0, x1, y0, y1, z0, z1 = (box[k] for k in ('x_min', 'x_max', 'y_min', 'y_max', 'z_min', 'z_max'))
    b.xy_plate(z1, x0, x1, y0, y1, material='superstructure')
    b.xy_plate(z0 + 0.02, x0, x1, y0, y1, material='superstructure')
    top = tier == tiers
    sill, head = (z0 + 0.9, z1 - 0.9) if top else (z0 + 1.1, z0 + 2.4)
    window, pier = (2.0, 0.35) if top else (1.6, 1.0)
    for y in (y0, y1):
        b.xz_plate(y, x0, x1, z0, sill, material='superstructure')
        b.xz_plate(y, x0, x1, head, z1, material='superstructure')
        for xa in steps(x0, x1, window + pier)[:-1]:
            b.xz_plate(y, xa, xa + pier, sill, head, material='superstructure')
            b.xz_plate(y + (0.12 if y < 0 else -0.12), xa + pier, xa + window + pier, sill, head, material='glass')
    for x in (x0, x1):
        b.yz_plate(x, y0, y1, z0, sill, material='superstructure')
        b.yz_plate(x, y0, y1, head, z1, material='superstructure')
        for ya in steps(y0, y1, window + pier)[:-1]:
            b.yz_plate(x, ya, ya + pier, sill, head, material='superstructure')
            b.yz_plate(x + (0.12 if x < x1 else -0.12), ya + pier, ya + window + pier, sill, head, material='glass')
    if not top:
        return
    wing = b.hull.B / 2 - 0.5
    m.box(x1 - 3.5, x1, y1, wing, z1 - 0.35, z1, 'superstructure')       # bridge wings
    m.box(x1 - 3.5, x1, -wing, y0, z1 - 0.35, z1, 'superstructure')
    for s in (1, -1):
        m.box(x1 - 3.5, x1, s * wing - 0.05 * s, s * wing, z1, z1 + 1.1, 'superstructure')
    fx, fy = x0 + 3.5, 0.0                                                # funnel aft on the top deck
    m.box(fx - 2.6, fx + 2.6, fy - 3.8, fy + 3.8, z1, z1 + 9.0, 'funnel')
    m.box(fx - 2.65, fx + 2.65, fy - 3.85, fy + 3.85, z1 + 6.0, z1 + 7.6, 'funnelband')
    for dy in (-1.6, 0.0, 1.6):
        m.cylinder((fx, fy + dy, z1 + 9.0), 'z', 0.5, 1.6, 'boottop', sides=10)
    mx = x1 - 6.0                                                         # radar mast
    m.cylinder((mx, 0.0, z1), 'z', 0.35, 8.0, 'fitting', sides=10)
    m.box(mx - 0.3, mx + 0.3, -3.5, 3.5, z1 + 6.0, z1 + 6.3, 'fitting')
    m.box(mx - 1.6, mx + 1.6, -0.3, 0.3, z1 + 8.0, z1 + 8.2, 'fitting')


def load(path):
    rows = [json.loads(line) for line in Path(path).read_text(encoding='utf-8').splitlines() if line.strip()]
    by_action = {}
    for row in rows:
        by_action.setdefault(row['action'], []).append(row['data'])
    return by_action


def layout_of(hull_id, boxes):
    """Counts the generator's segments: holds, deck segments per hold, deckhouse tiers."""
    codes = [b['block'][len(hull_id) + 1:] for b in boxes]
    holds = {}
    for code in codes:
        if code[0] == '2' and code[2] == '3':
            holds[int(code[1])] = max(holds.get(int(code[1]), 0), int(code[4:] or 1))
    tiers = max((int(c[2]) for c in codes if c[0] == '5'), default=1)
    count = max(holds) if holds else 1
    return {'holds': count, 'hold_segments': holds, 'house_tiers': tiers, 'manifold_hold': max(1, (count + 1) // 2)}


def write_mtl(path):
    lines = ['# Paint palette for generated ShipyardTwin block models']
    for name, (r, g, b) in PAINT.items():
        lines += [f'newmtl {name}', f'Kd {r:.3f} {g:.3f} {b:.3f}', 'Ka 0 0 0', 'Ks 0.05 0.05 0.05', '']
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text('\n'.join(lines), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('input', type=Path, help='generate_blocks.py가 만든 JSONL')
    parser.add_argument('-o', '--output', type=Path, default=MODELS)
    parser.add_argument('--no-preview', action='store_true', help='전체 선박 미리보기 OBJ를 만들지 않음')
    args = parser.parse_args()

    data = load(args.input)
    geometry = data['hull_geometry'][0]
    hull_id = geometry['hull']
    vessel = next((v for v in data.get('vessel_details', []) if v['hull'] == hull_id), None)
    if not vessel:
        raise SystemExit('입력에 vessel_details가 필요합니다(전장·선폭). --existing-hull 없이 생성한 파일을 사용하세요.')
    hull = HullForm(vessel['length_m'], vessel['beam_m'], geometry['depth_m'], geometry['draft_m'])
    params = section_params(vessel['beam_m'])
    layout = layout_of(hull_id, data['block_geometry'])
    write_mtl(args.output / hull_id / 'paint.mtl')
    preview = Mesh((0.0, 0.0, 0.0))
    total_vertices = 0
    for box in data['block_geometry']:
        code = box['block'][len(hull_id) + 1:]
        mesh = Mesh((box['cog_x'], box['cog_y'], box['cog_z']))
        build_block(Builder(mesh, box, hull), code, box, geometry['frame_spacing_m'], params, geometry['depth_m'], layout)
        asset = box.get('model_asset') or f'{hull_id}/{box["block"]}.obj'
        mesh.write(args.output / asset, [
            f'{box["block"]} illustrative block model (generated, not design CAD)',
            'units: m, origin: block centre of gravity, axes: obj = (ship x fwd, ship z up, -ship y)',
            f'cog (ship): {box["cog_x"]} {box["cog_y"]} {box["cog_z"]}'], mtl='paint.mtl')
        total_vertices += len(mesh.vertices)
        if not args.no_preview:
            offset = len(preview.vertices)
            ox, oy, oz = box['cog_x'], box['cog_z'], -box['cog_y']
            preview.vertices += [(x + ox, y + oy, z + oz) for x, y, z in mesh.vertices]
            preview.faces += [(a + offset, b + offset, c + offset, mat) for a, b, c, mat in mesh.faces]
    if not args.no_preview:
        preview.write(args.output / hull_id / f'{hull_id}-ship.obj',
                      [f'{hull_id} whole-ship preview (generated, not design CAD)',
                       'units: m, origin: aft end / centre line / baseline, axes: obj = (ship x, ship z, -ship y)'], mtl='paint.mtl')
    files = sorted((args.output / hull_id).glob('*.obj'))
    size = sum(f.stat().st_size for f in files)
    print(f'{args.output / hull_id}: OBJ {len(files)}개 + paint.mtl, {size / 1e6:.1f}MB, 블록 정점 {total_vertices:,}개')


if __name__ == '__main__':
    main()
