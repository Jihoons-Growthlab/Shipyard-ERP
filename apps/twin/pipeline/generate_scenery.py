"""Generate shipyard scenery for the twin: building models sized to the yard facilities, vehicle and equipment
models, and a scenery plan (roads, parked props, vehicle routes) laid out around generate_yard.FACILITIES.

Illustrative models (not design data). Proportions follow published descriptions: multi-span portal-frame
assembly halls (~40 m spans, ridge skylights, full-height gable doors), multi-axle low-deck block transporters,
rail-mounted level-luffing jib cranes on the fitting-out quays.

Outputs
- data/props/<model>.obj (+ paint.mtl): buildings per facility (e.g. GY-ASSY1.obj) and shared props
  (transporter, truck, forklift, car_*, luffing_crane, light_tower, office, gatehouse, container_*, plate_stack, pipe_stack).
  Local axes: x = facility/vehicle forward (yard +x for heading 0), y = left, z = up, origin at the base centre;
  written in OBJ axes (x, z, -y) like the block models.
- data/scenery.json: roads, props and routes in yard metres (x seaward, y along shore, heading in degrees).
- With --unity (default) both are copied into unity/.../Resources/ShipyardTwin (Props/<model>.obj.bytes, scenery.json).
"""

import argparse
import json
import math
import shutil
from pathlib import Path

from generate_models import Mesh
from generate_yard import FACILITIES
from paths import DATA, UNITY_RESOURCES

PAINT = {
    'wall_panel': (0.78, 0.80, 0.83), 'wall_rib': (0.66, 0.69, 0.73), 'wall_base': (0.35, 0.38, 0.42),
    'roof': (0.30, 0.48, 0.70), 'skylight': (0.82, 0.88, 0.93), 'door': (0.52, 0.56, 0.60), 'roof_vent': (0.62, 0.64, 0.66),
    'wall_white': (0.92, 0.92, 0.90), 'glass': (0.10, 0.15, 0.22), 'stack': (0.74, 0.75, 0.76), 'vehicle_yellow': (0.95, 0.70, 0.10),
    'tire': (0.10, 0.10, 0.11), 'lamp': (1.0, 0.95, 0.75), 'lamp_orange': (1.0, 0.55, 0.10), 'car_white': (0.93, 0.93, 0.93),
    'car_silver': (0.70, 0.72, 0.74), 'car_black': (0.10, 0.11, 0.12), 'car_red': (0.70, 0.12, 0.12), 'car_blue': (0.15, 0.30, 0.60),
    'trailer': (0.38, 0.40, 0.43), 'steel': (0.45, 0.43, 0.41), 'pipe': (0.74, 0.76, 0.78), 'fitting': (0.86, 0.87, 0.87),
    'quay_crane': (0.95, 0.76, 0.10), 'crane_dark': (0.16, 0.17, 0.19), 'container_blue': (0.13, 0.33, 0.62),
    'container_orange': (0.85, 0.42, 0.12), 'primer': (0.56, 0.54, 0.50), 'antifouling': (0.55, 0.13, 0.10),
}

FACILITY = {f[0]: f for f in FACILITIES}


def mesh():
    return Mesh((0.0, 0.0, 0.0))


# ---------------------------------------------------------------------------------------------- buildings

def hall(sx, sy, h, kind, name):
    """Portal-frame hall: spans across y, ridges along x, ribbed cladding, ridge skylights, gable doors, annex."""
    m = mesh()
    x0, x1, y0, y1 = -sx / 2, sx / 2, -sy / 2, sy / 2
    spans = 1 if kind == '창고' else max(1, round(sy / 38))
    span_w = sy / spans
    rise = span_w * 0.09
    base = 2.5
    # Long walls (y = y0, y1) with a dark base course and vertical ribs every 6 m.
    for y in (y0, y1):
        m.quad((x0, y, 0), (x1, y, 0), (x1, y, base), (x0, y, base), 'wall_base')
        m.quad((x0, y, base), (x1, y, base), (x1, y, h), (x0, y, h), 'wall_panel')
        for x in frange(x0 + 3, x1, 6):
            m.box(x - 0.15, x + 0.15, y - 0.25 if y < 0 else y, y if y < 0 else y + 0.25, base, h, 'wall_rib')
        m.box(x0, x1, y - 0.3 if y < 0 else y, y if y < 0 else y + 0.3, h * 0.55, h * 0.55 + 1.2, 'skylight')  # wall light band
    # Gable ends with one large door per span (blocks leave towards the docks at +x).
    for x in (x0, x1):
        for i in range(spans):
            a, b = y0 + i * span_w, y0 + (i + 1) * span_w
            m.quad((x, a, 0), (x, b, 0), (x, b, h), (x, a, h), 'wall_panel')
            m.tri((x, a, h), (x, b, h), (x, (a + b) / 2, h + rise), 'wall_panel')
            door_w = span_w * (0.62 if kind != '창고' else 0.35)
            d = 0.12 if x > 0 else -0.12
            m.quad((x + d, (a + b) / 2 - door_w / 2, 0), (x + d, (a + b) / 2 + door_w / 2, 0),
                   (x + d, (a + b) / 2 + door_w / 2, h * 0.78), (x + d, (a + b) / 2 - door_w / 2, h * 0.78), 'door')
            for k in frange((a + b) / 2 - door_w / 2 + 1.5, (a + b) / 2 + door_w / 2, 3.0):  # door panel joints
                m.box(x + d - 0.05, x + d + 0.05, k - 0.08, k + 0.08, 0, h * 0.78, 'wall_rib')
    # Pitched roofs, ridge skylights and roof ventilators.
    for i in range(spans):
        a, b = y0 + i * span_w, y0 + (i + 1) * span_w
        r = (a + b) / 2
        m.quad((x0 - 0.6, a - 0.4, h), (x1 + 0.6, a - 0.4, h), (x1 + 0.6, r, h + rise), (x0 - 0.6, r, h + rise), 'roof')
        m.quad((x0 - 0.6, r, h + rise), (x1 + 0.6, r, h + rise), (x1 + 0.6, b + 0.4, h), (x0 - 0.6, b + 0.4, h), 'roof')
        for side in (-1, 1):
            ya, yb = r + side * 1.0, r + side * 3.2
            za, zb = h + rise - rise * 1.0 / (span_w / 2) + 0.08, h + rise - rise * 3.2 / (span_w / 2) + 0.08
            m.quad((x0 + 4, ya, za), (x1 - 4, ya, za), (x1 - 4, yb, zb), (x0 + 4, yb, zb), 'skylight')
        for x in frange(x0 + 10, x1 - 5, 20):
            m.box(x - 1.2, x + 1.2, r - 0.9, r + 0.9, h + rise, h + rise + 1.6, 'roof_vent')
    # Single-storey annex (offices, lockers) along the -y side.
    if kind != '창고':
        ax0, ax1 = x0 + sx * 0.15, x0 + sx * 0.65
        m.box(ax0, ax1, y0 - 9, y0, 0, 7, 'wall_white')
        m.box(ax0 - 0.3, ax1 + 0.3, y0 - 9.3, y0, 7, 7.4, 'roof')
        for x in frange(ax0 + 2, ax1 - 2, 4):
            m.quad((x, y0 - 9.05, 2.6), (x + 2.4, y0 - 9.05, 2.6), (x + 2.4, y0 - 9.05, 4.8), (x, y0 - 9.05, 4.8), 'glass')
    else:  # warehouse loading bays on the -y side
        for x in frange(x0 + 12, x1 - 6, 16):
            m.quad((x - 3, y0 - 0.12, 0), (x + 3, y0 - 0.12, 0), (x + 3, y0 - 0.12, 5), (x - 3, y0 - 0.12, 5), 'door')
            m.box(x - 3.6, x + 3.6, y0 - 3, y0, 5.4, 5.7, 'wall_rib')
    if 'PAINT' in name:  # blasting/painting shop: exhaust stacks and roof ducts
        for x in frange(x0 + 15, x1 - 10, 28):
            m.cylinder((x, y1 + 3.5, 0), 'z', 1.3, h + rise + 9, 'stack', sides=14)
            m.box(x - 1.0, x + 1.0, y1 - 6, y1 + 3.5, h + 2, h + 4, 'stack')
            m.box(x - 4, x + 4, y1 + 6, y1 + 12, 0, 6, 'wall_panel')  # dehumidifier unit
    return m


def office():
    """Four-storey office block, 50 x 18 m, glazed bands, entrance canopy facing +x."""
    m = mesh()
    lx, ly, floor = 50, 18, 3.6
    m.box(-lx / 2, lx / 2, -ly / 2, ly / 2, 0, floor * 4, 'wall_white')
    for f in range(4):
        z = f * floor + 1.0
        for side in (-1, 1):
            y = side * (ly / 2 + 0.06)
            m.quad((-lx / 2 + 1, y, z), (lx / 2 - 1, y, z), (lx / 2 - 1, y, z + 1.8), (-lx / 2 + 1, y, z + 1.8), 'glass')
        for side in (-1, 1):
            x = side * (lx / 2 + 0.06)
            m.quad((x, -ly / 2 + 1, z), (x, ly / 2 - 1, z), (x, ly / 2 - 1, z + 1.8), (x, -ly / 2 + 1, z + 1.8), 'glass')
    m.box(-lx / 2 - 0.3, lx / 2 + 0.3, -ly / 2 - 0.3, ly / 2 + 0.3, floor * 4, floor * 4 + 1.0, 'wall_rib')
    m.box(lx / 2, lx / 2 + 6, -5, 5, 3.6, 4.0, 'wall_rib')
    m.box(lx / 2 + 0.06, lx / 2 + 0.2, -3, 3, 0, 3.2, 'glass')
    m.box(-6, 6, -4, 4, floor * 4 + 1, floor * 4 + 3, 'roof_vent')
    return m


def gatehouse():
    m = mesh()
    m.box(-3, 3, -2, 2, 0, 3, 'wall_white')
    m.box(-3.4, 3.4, -2.4, 2.4, 3, 3.3, 'roof')
    for y in (-2.05, 2.05):
        m.quad((-2.5, y, 1.1), (2.5, y, 1.1), (2.5, y, 2.4), (-2.5, y, 2.4), 'glass')
    m.box(3.5, 4.0, 2.5, 3.0, 0, 1.1, 'crane_dark')            # barrier post
    m.box(3.6, 3.9, 3.0, 12.0, 0.9, 1.1, 'lamp_orange')        # barrier arm across the lane
    return m


# ---------------------------------------------------------------------------------------------- vehicles

def wheels(m, xs, ys, r, width=0.5):
    for x in xs:
        for y in ys:
            m.cylinder((x, y - width / 2, r), 'y', r, width, 'tire', sides=12)


def transporter(load=False):
    """Self-propelled block transporter: 22 m low deck on 10 axle lines, cab at the front."""
    m = mesh()
    L, W, deck = 22.0, 6.5, 1.9
    m.box(-L / 2, L / 2, -W / 2, W / 2, 1.0, deck, 'vehicle_yellow')
    m.box(-L / 2, L / 2, -W / 2 - 0.05, -W / 2, 1.0, 1.25, 'crane_dark')
    m.box(-L / 2, L / 2, W / 2, W / 2 + 0.05, 1.0, 1.25, 'crane_dark')
    m.box(L / 2 - 2.4, L / 2, W / 2 - 2.6, W / 2, deck, deck + 2.1, 'vehicle_yellow')   # cab
    m.quad((L / 2 + 0.03, W / 2 - 2.4, deck + 0.8), (L / 2 + 0.03, W / 2 - 0.2, deck + 0.8),
           (L / 2 + 0.03, W / 2 - 0.2, deck + 1.9), (L / 2 + 0.03, W / 2 - 2.4, deck + 1.9), 'glass')
    m.box(L / 2 - 1.4, L / 2 - 1.0, W / 2 - 1.6, W / 2 - 1.2, deck + 2.1, deck + 2.5, 'lamp_orange')
    wheels(m, [-L / 2 + 1.2 + i * (L - 2.4) / 9 for i in range(10)], [-W / 2 + 0.6, -W / 2 + 1.4, W / 2 - 1.4, W / 2 - 0.6], 0.45, 0.45)
    if load:  # a primed double-bottom block on timber supports
        for x in (-6, 0, 6):
            m.box(x - 0.4, x + 0.4, -W / 2 + 0.5, W / 2 - 0.5, deck, deck + 0.4, 'steel')
        m.box(-8.5, 8.5, -W / 2 - 1.5, W / 2 + 1.5, deck + 0.4, deck + 3.4, 'primer')
        m.box(-8.5, 8.5, -W / 2 - 1.5, W / 2 + 1.5, deck + 0.4, deck + 0.7, 'antifouling')
    return m


def truck():
    """Tractor unit with a flatbed trailer carrying steel plates."""
    m = mesh()
    m.box(4.2, 7.0, -1.25, 1.25, 1.0, 3.4, 'car_blue')
    m.quad((7.03, -1.1, 2.2), (7.03, 1.1, 2.2), (7.03, 1.1, 3.1), (7.03, -1.1, 3.1), 'glass')
    m.box(2.6, 4.2, -1.1, 1.1, 1.0, 1.5, 'crane_dark')
    m.box(-9.5, 3.6, -1.25, 1.25, 1.2, 1.6, 'trailer')
    for i, (lx, ly) in enumerate(((10, 2.0), (9.5, 1.9), (9.0, 1.8))):
        m.box(-8.5 + i * 0.2, -8.5 + lx, -ly / 2, ly / 2, 1.6 + i * 0.12, 1.72 + i * 0.12, 'steel')
    wheels(m, [5.6, 3.3, 2.1], [-1.05, 1.05], 0.52)
    wheels(m, [-6.0, -7.3, -8.6], [-1.05, 1.05], 0.5)
    return m


def forklift():
    m = mesh()
    m.box(-1.6, 1.2, -0.65, 0.65, 0.4, 1.4, 'vehicle_yellow')
    m.box(-1.6, -0.9, -0.6, 0.6, 1.4, 2.1, 'crane_dark')           # counterweight
    for y in (-0.6, 0.6):
        m.box(-0.6, -0.5, y - 0.05, y + 0.05, 1.4, 2.4, 'crane_dark')  # overhead guard posts
    m.box(-0.6, 0.9, -0.65, 0.65, 2.35, 2.45, 'crane_dark')
    m.box(1.25, 1.4, -0.4, 0.4, 0.2, 3.3, 'crane_dark')              # mast
    for y in (-0.3, 0.3):
        m.box(1.4, 2.6, y - 0.07, y + 0.07, 0.15, 0.25, 'crane_dark')  # forks
    wheels(m, [0.8, -1.1], [-0.6, 0.6], 0.35, 0.3)
    return m


def car(paint):
    m = mesh()
    m.box(-2.3, 2.3, -0.9, 0.9, 0.3, 1.0, paint)
    m.box(-1.2, 1.0, -0.82, 0.82, 1.0, 1.45, paint)
    for y in (-0.84, 0.84):
        m.quad((-1.1, y, 1.05), (0.9, y, 1.05), (0.9, y, 1.4), (-1.1, y, 1.4), 'glass')
    m.quad((1.02, -0.75, 1.05), (1.02, 0.75, 1.05), (1.02, 0.75, 1.4), (1.02, -0.75, 1.4), 'glass')
    wheels(m, [1.45, -1.45], [-0.82, 0.82], 0.33, 0.25)
    return m


# ---------------------------------------------------------------------------------------------- equipment

def luffing_crane():
    """Rail-mounted level-luffing jib crane: 10 m portal, slewing house, A-frame, 48 m jib over the berth (+x)."""
    m = mesh()
    for x in (-5, 5):
        for y in (-5, 5):
            m.box(x - 0.6, x + 0.6, y - 0.6, y + 0.6, 0, 12, 'quay_crane')
        m.box(x - 1.2, x + 1.2, -6, 6, 0, 1.0, 'crane_dark')            # bogies on the rails
    for y in (-5, 5):
        m.box(-5, 5, y - 0.5, y + 0.5, 11, 12.5, 'quay_crane')
    m.box(-5.5, 5.5, -5.5, 5.5, 12.5, 13.5, 'quay_crane')
    m.cylinder((0, 0, 13.5), 'z', 3.2, 2.0, 'crane_dark', sides=16)       # slewing ring
    m.box(-9, 3, -3.5, 3.5, 15.5, 21, 'quay_crane')                      # machinery house
    m.box(-8.6, -2, -3.55, 3.55, 18, 19.2, 'glass')                      # cab windows band
    m.box(-12, -8, -2.5, 2.5, 15.5, 19, 'crane_dark')                     # counterweight
    for y in (-2.2, 2.2):                                                 # A-frame
        m.quad((-4, y, 21), (2, y, 21), (-1, y, 34), (-1.6, y, 34), 'quay_crane')
    # Jib from the pivot to the tip at ~48 m outreach, as two slim box chords.
    pivot, tip = (2.0, 0.0, 22.0), (48.0, 0.0, 38.0)
    for y in (-0.9, 0.9):
        beam(m, (pivot[0], y, pivot[2]), (tip[0], y, tip[2]), 0.7, 'quay_crane')
    beam(m, (-1.3, 0, 34), (tip[0] - 4, 0, tip[2] + 1.5), 0.25, 'crane_dark')  # luffing tie
    m.box(tip[0] - 0.8, tip[0] + 0.8, -0.9, 0.9, tip[2] - 1.2, tip[2] + 0.6, 'quay_crane')
    m.box(tip[0] - 0.04, tip[0] + 0.04, -0.04, 0.04, tip[2] - 18, tip[2] - 1.2, 'crane_dark')  # hoist rope
    m.box(tip[0] - 0.5, tip[0] + 0.5, -0.35, 0.35, tip[2] - 19.5, tip[2] - 18, 'vehicle_yellow')  # hook block
    return m


def beam(m, a, b, t, paint):
    """Square beam between two points (boxes along the segment, approximated with 4 long quads)."""
    ax, ay, az = a
    bx, by, bz = b
    dx, dy, dz = bx - ax, by - ay, bz - az
    length = math.sqrt(dx * dx + dy * dy + dz * dz)
    ux, uy, uz = dx / length, dy / length, dz / length
    # Perpendiculars: one horizontal (across y), one in the x-z plane.
    px, py, pz = 0.0, 1.0, 0.0
    qx, qy, qz = uy * pz - uz * py, uz * px - ux * pz, ux * py - uy * px
    h = t / 2
    corners = [(+1, +1), (-1, +1), (-1, -1), (+1, -1)]
    def at(p, s1, s2):
        return (p[0] + h * (s1 * px + s2 * qx), p[1] + h * (s1 * py + s2 * qy), p[2] + h * (s1 * pz + s2 * qz))
    for (s1, s2), (t1, t2) in zip(corners, corners[1:] + corners[:1]):
        m.quad(at(a, s1, s2), at(b, s1, s2), at(b, t1, t2), at(a, t1, t2), paint)


def light_tower():
    m = mesh()
    m.cylinder((0, 0, 0), 'z', 0.35, 28, 'pipe', sides=10)
    m.box(-1.8, 1.8, -0.4, 0.4, 27.5, 28.2, 'fitting')
    for y in (-1.3, -0.45, 0.45, 1.3):
        m.box(-0.7, 0.7, y - 0.35, y + 0.35, 27.1, 27.5, 'lamp')
    return m


def container(paint):
    m = mesh()
    m.box(-6.1, 6.1, -1.22, 1.22, 0, 2.6, paint)
    for x in frange(-5.5, 6.0, 0.9):
        for y in (-1.25, 1.25):
            m.box(x - 0.08, x + 0.08, y - 0.03, y + 0.03, 0.1, 2.5, paint)
    return m


def plate_stack():
    m = mesh()
    for i in range(6):
        m.box(-6 + i * 0.05, 6 - i * 0.05, -1.5, 1.5, 0.3 + i * 0.22, 0.5 + i * 0.22, 'steel')
    for x in (-4, 0, 4):
        m.box(x - 0.2, x + 0.2, -1.6, 1.6, 0, 0.3, 'wall_base')
    return m


def pipe_stack():
    m = mesh()
    for row in range(3):
        for k in range(5 - row):
            y = -1.6 + k * 0.8 + row * 0.4
            m.cylinder((-6, y, 0.4 + row * 0.7), 'x', 0.38, 12, 'pipe', sides=10)
    for x in (-4, 0, 4):
        m.box(x - 0.2, x + 0.2, -2.2, 2.2, 0, 0.1, 'wall_base')
    return m


# ---------------------------------------------------------------------------------------------- layout

def frange(start, stop, step):
    values, x = [], start
    while x < stop - 1e-9:
        values.append(x)
        x += step
    return values


# Paved site outline, mirrored from Unity (YardEnvironment.SiteOutline): convex hull of the land footprints (docks,
# shops, yards and the root of each quay that starts at the shoreline) grown by 28 m, cut at the shoreline.
SITE_MARGIN = 28.0
DOCK_WALL = 6.0          # YardLayout.WallThickness
PIER_ROOT = 1.0          # m of quay taken as land at the shoreline so the paving reaches the pier deck


def rect(x0, x1, y0, y1):
    return [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]


def footprint(f, grow=0.0):
    return rect(f[3] - f[5] / 2 - grow, f[3] + f[5] / 2 + grow, f[4] - f[6] / 2 - grow, f[4] + f[6] / 2 + grow)


def shoreline():
    return max(f[3] + f[5] / 2 for f in FACILITIES if f[1] == '도크')


def land_footprints():
    """Facilities as (x, y, size_x, size_y) rows the site outline is built from (same rule as Unity)."""
    shore = shoreline()
    rows = [f for f in FACILITIES if f[1] != '안벽']
    for f in FACILITIES:
        x0 = f[3] - f[5] / 2
        if f[1] == '안벽' and abs(x0 - shore) <= SITE_MARGIN:
            rows.append((f[0] + '-ROOT', '안벽', f[2], shore - PIER_ROOT / 2, f[4], PIER_ROOT, f[6], 0))
    return rows


def convex_hull(points):
    pts = sorted(set(points))
    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    lower, upper = [], []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]          # counter-clockwise


def clip_x_max(poly, x_max):
    out = []
    for i, p in enumerate(poly):
        q = poly[(i + 1) % len(poly)]
        if p[0] <= x_max:
            out.append(p)
        if (p[0] <= x_max) != (q[0] <= x_max):
            t = (x_max - p[0]) / (q[0] - p[0])
            out.append((x_max, p[1] + t * (q[1] - p[1])))
    return out


def site_outline():
    corners = [c for f in land_footprints() for c in footprint(f, SITE_MARGIN)]
    return clip_x_max(convex_hull(corners), shoreline())


def inside(poly, p, margin=0.0):
    """Point inside a counter-clockwise convex polygon, at least `margin` m from every edge."""
    for i, a in enumerate(poly):
        b = poly[(i + 1) % len(poly)]
        ex, ey = b[0] - a[0], b[1] - a[1]
        length = math.hypot(ex, ey)
        if length < 1e-9:
            continue
        if (ex * (p[1] - a[1]) - ey * (p[0] - a[0])) / length < margin:
            return False
    return True


def road_box(r):
    """Rendered road surface (Unity BuildRoad draws length + width: half a width past each end)."""
    x0, y0, x1, y1, w = r['x0'], r['y0'], r['x1'], r['y1'], r['width']
    h = w / 2
    return (min(x0, x1) - h, max(x0, x1) + h, min(y0, y1) - h, max(y0, y1) + h)


def overlap(a, b):
    return a[0] < b[1] and b[0] < a[1] and a[2] < b[3] and b[2] < a[3]


def check(scenery):
    """Roads, vehicles and props stay on the paving: not over docks, rails, buildings, yards, water or bare terrain."""
    outline = site_outline()
    shore = shoreline()
    quays = [f for f in FACILITIES if f[1] == '안벽']
    problems = []
    def on_land_or_quay(x, y, margin):
        if inside(outline, (x, y), margin):
            return True
        return any(f[3] - f[5] / 2 <= x <= f[3] + f[5] / 2 - margin and abs(y - f[4]) <= f[6] / 2 - margin for f in quays)
    blocked = []
    for f in FACILITIES:
        if f[1] == '도크':
            # Pit plus the goliath rails (GantryCraneView: 8 m beyond the walls, from 100 m inland of the pit to the
            # gate) with 1 m clearance.
            pit = f[5] / 2 - DOCK_WALL
            blocked.append((f[0], (f[3] - pit - 100 - 1, f[3] + f[5] / 2, f[4] - f[6] / 2 - 9.6, f[4] + f[6] / 2 + 9.6)))
        elif f[1] in ('작업장', '창고', '야드'):
            blocked.append((f[0], tuple(v for v in (f[3] - f[5] / 2, f[3] + f[5] / 2, f[4] - f[6] / 2, f[4] + f[6] / 2))))
    for r in scenery['roads']:
        box = road_box(r)
        for x in (box[0], box[1]):
            for y in (box[2], box[3]):
                if not on_land_or_quay(x, y, 1.0):
                    problems.append(f'road {r} corner ({x:.1f}, {y:.1f}) off the paving')
        for name, b in blocked:
            if overlap(box, b):
                problems.append(f'road {r} crosses {name}')
    boxes = [road_box(r) for r in scenery['roads']]
    def on_road(x, y):
        return any(b[0] <= x <= b[1] and b[2] <= y <= b[3] for b in boxes)
    for route in scenery['routes']:
        pts = list(zip(route['path'][::2], route['path'][1::2]))
        legs = list(zip(pts, pts[1:])) + ([(pts[-1], pts[0])] if route['loop'] else [])
        for a, b in legs:
            for k in range(0, 21):
                x, y = a[0] + (b[0] - a[0]) * k / 20, a[1] + (b[1] - a[1]) * k / 20
                if not on_road(x, y):
                    problems.append(f'route {route["model"]} leaves the roads at ({x:.1f}, {y:.1f})')
                    break
    for p in scenery['props']:
        if p['model'].startswith('GY-'):
            continue
        if not on_land_or_quay(p['x'], p['y'], 3.0):
            problems.append(f'prop {p["model"]} at ({p["x"]}, {p["y"]}) off the paving')
        if p['model'] in ('light_tower', 'office', 'gatehouse', 'container_blue', 'container_orange', 'plate_stack', 'pipe_stack') \
                and on_road(p['x'], p['y']):
            problems.append(f'prop {p["model"]} at ({p["x"]}, {p["y"]}) stands on a road')
    return problems


def plan():
    """Roads, props and routes in yard metres around the generate_yard layout, all on the paved site or the pier."""
    shops = [f for f in FACILITIES if f[1] in ('작업장', '창고')]
    docks = [f for f in FACILITIES if f[1] == '도크']
    quays = [f for f in FACILITIES if f[1] == '안벽']
    shore = shoreline()
    # The shop row is the main fabrication halls only: the quay-side outfitting shop behind the pier root sits
    # between the docks and the pier and must not pull the spine or the south road out there.
    row = sorted((f for f in shops if f[1] == '작업장' and f[3] + f[5] / 2 < min(d[3] - d[5] / 2 for d in docks)), key=lambda f: f[4])
    shop_x1 = max(f[3] + f[5] / 2 for f in row)
    heads = docks + [f for f in FACILITIES if f[0].startswith('GY-PE')]
    dock_x0 = min(f[3] - f[5] / 2 for f in heads)
    spine_a = (shop_x1 + dock_x0) / 2                       # main road between the shop row and the PE yards
    spine_b = min(f[3] - f[5] / 2 for f in row) - 17         # back road behind the shops
    gaps = [((a[4] + a[6] / 2) + (b[4] - b[6] / 2)) / 2 for a, b in zip(row, row[1:])]
    y_lo = row[0][4] - row[0][6] / 2 - 13                    # south road, clear of the lowest dock's jib cranes
    y_hi = row[-1][4] + row[-1][6] / 2 + 15
    dock_rows = sorted(docks, key=lambda f: f[4])
    dock_gaps = [((a[4] + a[6] / 2) + (b[4] - b[6] / 2)) / 2 for a, b in zip(dock_rows, dock_rows[1:])]
    top_dock = dock_rows[-1][4] + dock_rows[-1][6] / 2 + 35  # outside the goliath rail and jib cranes
    outline = site_outline()
    west = min(x for x, _ in outline)

    road = lambda x0, y0, x1, y1, w: {'x0': round(x0, 2), 'y0': round(y0, 2), 'x1': round(x1, 2), 'y1': round(y1, 2), 'width': w}

    def reach_east(y, x_from, width, x_max):
        """Furthest x a horizontal road at y can run to with its rendered end still 2 m inside the paving."""
        x = x_max
        while x > x_from and not all(inside(outline, (x + width / 2, yy), 2.0) for yy in (y - width / 2, y + width / 2)):
            x -= 1.0
        return x

    # Gate on the west edge of the paving, south of the block stockyard: the only road that meets the site edge.
    stock = FACILITY.get('GY-STOCK')
    gate_y = (stock[4] - stock[6] / 2 - 10) if stock else y_lo - 40
    spine_b_lo = gate_y
    gate_x = west + 2 + 5                                     # rendered end 2 m inside the edge
    pier_x = None
    roads = [road(spine_a, y_lo, spine_a, y_hi, 14), road(spine_b, spine_b_lo, spine_b, y_hi, 10),
             road(spine_b, y_hi, spine_a, y_hi, 10), road(gate_x, gate_y, spine_b, gate_y, 10)]
    roads += [road(spine_b, y, spine_a, y, 8) for y in gaps]
    for y in dock_gaps + [top_dock]:                          # between / beside the docks, toward the gates
        roads.append(road(spine_a, y, reach_east(y, spine_a, 10, shore - 10), y, 10))
    # Pier access: south road to a link just short of the shoreline, then onto the pier along its centre line.
    for q in quays:
        if abs(q[3] - q[5] / 2 - shore) > SITE_MARGIN:
            continue
        others = [f for f in FACILITIES if f[1] in ('작업장', '야드') and f is not q and abs(f[4] - q[4]) < q[6]]
        pier_x = max([f[3] + f[5] / 2 for f in others] + [spine_a]) + 45  # behind the quay shops, before the shoreline
        roads.append(road(spine_b, y_lo, pier_x, y_lo, 12))
        roads.append(road(pier_x, y_lo, pier_x, q[4], 10))
        roads.append(road(pier_x, q[4], q[3] + q[5] / 2 - 20, q[4], 8))
    if pier_x is None:
        roads.append(road(spine_b, y_lo, reach_east(y_lo, spine_b, 12, shore - 10), y_lo, 12))

    props = []
    add = lambda model, x, y, heading=0: props.append({'model': model, 'x': round(x, 2), 'y': round(y, 2), 'heading': heading})
    for f in shops:
        add(f[0], f[3], f[4])
    store = FACILITY.get('GY-STORE')
    office_x = (stock[3] if stock else spine_b - 60)
    office_y = ((stock[4] + stock[6] / 2) + (store[4] - store[6] / 2)) / 2 if stock and store else 55
    add('office', office_x, office_y, 90)
    add('gatehouse', gate_x + 8, gate_y - 9)                  # booth beside the lane, barrier arm across it
    for i in range(12):  # staff parking beside the office, inside the paving
        add(['car_white', 'car_silver', 'car_black', 'car_red'][i % 4], office_x - 38 - (i // 6) * 7, office_y - 15 + (i % 6) * 5, 0)
    for q in quays:
        # Level-luffing cranes every ~105 m, alternating sides so both berths are served (jib over its berth);
        # floodlights between them beside the centre road; stock at the pier root beside the road.
        n = max(2, round(q[5] / 105))
        pitch = q[5] / n
        for k in range(n):
            side = 1 if k % 2 == 0 else -1
            add('luffing_crane', q[3] - q[5] / 2 + (k + 0.5) * pitch, q[4] - side * (q[6] / 2 - 8), 90 if side > 0 else 270)
        for k in range(1, n):
            add('light_tower', q[3] - q[5] / 2 + k * pitch, q[4] + (5.5 if k % 2 else -5.5))
        add('container_blue', q[3] - q[5] / 2 + 14, q[4] + 6.5, 0)
        add('container_orange', q[3] - q[5] / 2 + 14, q[4] - 6.5, 0)
    for y in frange(y_lo + 20, y_hi, 85):                    # floodlights between the spine and the PE yards
        if all(abs(y - g) > 12 for g in dock_gaps + [top_dock]):
            add('light_tower', spine_a + 9.8, y, 90)
    for f in docks:
        # Jib cranes outside each dock's goliath rails (small lifts, outfitting in the dock), on the outer side
        # only: between the docks run both goliaths' rails and the road.
        others = [d for d in docks if d is not f]
        for y in (f[4] - f[6] / 2 - 22, f[4] + f[6] / 2 + 22):
            if any(abs(y - d[4]) < abs(f[4] - d[4]) for d in others):
                continue
            for x in (f[3] - f[5] * 0.25, f[3] + f[5] * 0.25):
                add('luffing_crane', x, y, 90 if y < f[4] else 270)
    fab = FACILITY.get('GY-FAB')
    if fab:
        for k in range(4):
            add('plate_stack', fab[3] + fab[5] / 2 + 3, fab[4] - 25 + k * 16, 90)
    outfit = FACILITY.get('GY-OUTFIT')
    if outfit:
        for k in range(3):
            add('pipe_stack', outfit[3] + outfit[5] / 2 + 3, outfit[4] - 18 + k * 18, 90)
        add('forklift', outfit[3] + outfit[5] / 2 + 3, outfit[4] + 30, 180)
    for f in row[1:3]:
        add('forklift', f[3] + f[5] / 2 + 3, f[4] + 12, 90)
    add('truck', spine_b, office_y + 40, 90)
    add('transporter', spine_a, gaps[1] if len(gaps) > 1 else y_lo, 90)

    lane = dock_gaps[0] if dock_gaps else top_dock
    lane_end = max(r['x1'] for r in roads if r['y0'] == r['y1'] == round(lane, 2)) - 25
    routes = [
        # Loaded transporter: shop gap → spine → between the docks to the dock heads and back (out and back).
        {'model': 'transporter_loaded', 'speed': 2.5, 'loop': False,
         'points': [[spine_b, gaps[1] if len(gaps) > 1 else y_lo], [spine_a, gaps[1] if len(gaps) > 1 else y_lo],
                    [spine_a, lane], [lane_end, lane]]},
        {'model': 'transporter', 'speed': 3.0, 'loop': True,
         'points': [[spine_a, y_lo], [spine_a, y_hi], [spine_b, y_hi], [spine_b, y_lo]]},
        {'model': 'forklift', 'speed': 2.0, 'loop': True,
         'points': [[spine_b, gaps[0]], [spine_a, gaps[0]], [spine_a, gaps[1]], [spine_b, gaps[1]]]} if len(gaps) > 1 else None,
    ]
    truck = [[gate_x, gate_y], [spine_b, gate_y], [spine_b, y_lo]]
    if pier_x is not None:                                    # gate → south road → link → along the pier and back
        q = next(q for q in quays if abs(q[3] - q[5] / 2 - shore) <= SITE_MARGIN)
        truck += [[pier_x, y_lo], [pier_x, q[4]], [q[3] + q[5] / 2 - 30, q[4]]]
    else:
        truck += [[spine_b, y_hi], [spine_a, y_hi]]
    routes.append({'model': 'truck', 'speed': 5.0, 'loop': False, 'points': truck})
    routes = [r for r in routes if r]
    for r in routes:  # flat [x0, y0, x1, y1, ...]: Unity's JsonUtility cannot read nested arrays
        r['path'] = [round(v, 2) for point in r.pop('points') for v in point]
    return {'format': 'shipyard-scenery/1', 'roads': roads, 'props': props, 'routes': routes}


def models():
    out = {}
    for f in FACILITIES:
        if f[1] in ('작업장', '창고'):
            out[f[0]] = hall(f[5], f[6], f[7] or 20, f[1], f[0])
    out.update({'office': office(), 'gatehouse': gatehouse(), 'transporter': transporter(), 'transporter_loaded': transporter(True),
                'truck': truck(), 'forklift': forklift(), 'luffing_crane': luffing_crane(), 'light_tower': light_tower(),
                'container_blue': container('container_blue'), 'container_orange': container('container_orange'),
                'plate_stack': plate_stack(), 'pipe_stack': pipe_stack()})
    for paint in ('car_white', 'car_silver', 'car_black', 'car_red'):
        out[paint] = car(paint)
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('-o', '--output', type=Path, default=DATA)
    parser.add_argument('--no-unity', action='store_true', help='Unity Resources로 복사하지 않음')
    args = parser.parse_args()
    props_dir = args.output / 'props'
    props_dir.mkdir(parents=True, exist_ok=True)
    lines = ['# Paint palette for generated ShipyardTwin scenery']
    for name, (r, g, b) in PAINT.items():
        lines += [f'newmtl {name}', f'Kd {r:.3f} {g:.3f} {b:.3f}', '']
    (props_dir / 'paint.mtl').write_text('\n'.join(lines), encoding='utf-8')
    built = models()
    vertices = 0
    for name, m in built.items():
        m.write(props_dir / f'{name}.obj', [f'{name} illustrative scenery model (generated)',
                                           'units: m, origin: base centre, axes: obj = (local x fwd, z up, -local y)'], mtl='paint.mtl')
        vertices += len(m.vertices)
    scenery = plan()
    problems = check(scenery)
    if problems:
        print('\n'.join(problems))
        raise SystemExit(f'배치 검사 실패 {len(problems)}건: 도로·차량·소품이 포장면 밖이나 도크·시설 위에 있습니다.')
    (args.output / 'scenery.json').write_text(json.dumps(scenery, ensure_ascii=False, indent=1), encoding='utf-8')
    print(f'{props_dir}: 모델 {len(built)}개 (정점 {vertices:,}) · 도로 {len(scenery["roads"])} · 배치 {len(scenery["props"])} · '
          f'운행 경로 {len(scenery["routes"])}')
    if not args.no_unity:
        target = UNITY_RESOURCES / 'Props'
        target.mkdir(parents=True, exist_ok=True)
        for name in built:
            shutil.copyfile(props_dir / f'{name}.obj', target / f'{name}.obj.bytes')
        shutil.copyfile(args.output / 'scenery.json', UNITY_RESOURCES / 'scenery.json')
        print(f'{UNITY_RESOURCES}: Props {len(built)}개, scenery.json 복사')


if __name__ == '__main__':
    main()
