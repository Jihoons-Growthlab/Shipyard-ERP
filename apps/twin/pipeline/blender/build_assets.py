"""Blender (bpy/bmesh) builder for detailed ShipyardTwin assets. Run through pipeline/build_hq_assets.py:

    Blender -b --factory-startup -P pipeline/blender/build_assets.py -- <layout.json> <out_dir>

Every asset is one mesh built with bmesh: bevelled boxes, cylinders, beams, trusses and lattices, with paint
materials (same names as the Unity ShipPalette), sharp-by-angle normals and box-projected UVs in metres.
Local axes: x forward (along rails / facility x), y left (across), z up; origin at the base centre. Exported as
OBJ with Blender's default axes (-Z forward, Y up), i.e. obj = (x, z, -y), the convention of the other models.
Illustrative models, not design data.
"""
import json
import math
import sys
from pathlib import Path

import bmesh
import bpy
from mathutils import Matrix, Vector

PAINT = {
    'wall_panel': (0.78, 0.80, 0.83), 'wall_rib': (0.66, 0.69, 0.73), 'wall_base': (0.35, 0.38, 0.42), 'roof': (0.30, 0.48, 0.70),
    'skylight': (0.82, 0.88, 0.93), 'door': (0.52, 0.56, 0.60), 'roof_vent': (0.62, 0.64, 0.66), 'wall_white': (0.92, 0.92, 0.90),
    'glass': (0.10, 0.15, 0.22), 'stack': (0.74, 0.75, 0.76), 'vehicle_yellow': (0.95, 0.70, 0.10), 'tire': (0.10, 0.10, 0.11),
    'rim': (0.70, 0.72, 0.74), 'lamp': (1.0, 0.95, 0.75), 'lamp_orange': (1.0, 0.55, 0.10), 'car_white': (0.93, 0.93, 0.93),
    'car_silver': (0.70, 0.72, 0.74), 'car_black': (0.10, 0.11, 0.12), 'car_red': (0.70, 0.12, 0.12), 'car_blue': (0.15, 0.30, 0.60),
    'trailer': (0.38, 0.40, 0.43), 'steel': (0.45, 0.43, 0.41), 'pipe': (0.74, 0.76, 0.78), 'fitting': (0.86, 0.87, 0.87),
    'quay_crane': (0.95, 0.76, 0.10), 'crane_dark': (0.16, 0.17, 0.19), 'crane_white': (0.92, 0.93, 0.93), 'crane_red': (0.76, 0.12, 0.10),
    'crane_yellow': (0.96, 0.74, 0.08), 'grating': (0.30, 0.31, 0.33), 'container_blue': (0.13, 0.33, 0.62),
    'container_orange': (0.85, 0.42, 0.12), 'primer': (0.56, 0.54, 0.50), 'antifouling': (0.55, 0.13, 0.10), 'concrete': (0.63, 0.63, 0.61),
    'rock': (0.42, 0.40, 0.37),
}


class Asset:
    def __init__(self, name):
        self.name = name
        self.bm = bmesh.new()
        self.paints = []

    def _paint(self, paint):
        if paint not in self.paints:
            self.paints.append(paint)
        return self.paints.index(paint)

    def _tag(self, verts, paint):
        index = self._paint(paint)
        for face in {f for v in verts for f in v.link_faces}:
            face.material_index = index

    def box(self, x0, x1, y0, y1, z0, z1, paint, bevel=0.0):
        if x1 <= x0 or y1 <= y0 or z1 <= z0:
            return
        size = (x1 - x0, y1 - y0, z1 - z0)
        matrix = Matrix.Translation(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2)) @ Matrix.Diagonal((*size, 1.0))
        verts = bmesh.ops.create_cube(self.bm, size=1.0, matrix=matrix)['verts']
        self._tag(verts, paint)
        if bevel > 0:
            edges = list({e for v in verts for e in v.link_edges})
            bmesh.ops.bevel(self.bm, geom=edges, offset=min(bevel, min(size) * 0.3), segments=2, profile=0.5, affect='EDGES')

    def oriented_box(self, centre, x_axis, y_axis, size, paint, bevel=0.0):
        """Box with its local x/y axes along the given directions (z = x × y)."""
        x = Vector(x_axis).normalized()
        y = Vector(y_axis).normalized()
        z = x.cross(y).normalized()
        y = z.cross(x)
        rotation = Matrix((x, y, z)).transposed().to_4x4()
        matrix = Matrix.Translation(centre) @ rotation @ Matrix.Diagonal((*size, 1.0))
        verts = bmesh.ops.create_cube(self.bm, size=1.0, matrix=matrix)['verts']
        self._tag(verts, paint)
        if bevel > 0:
            edges = list({e for v in verts for e in v.link_edges})
            bmesh.ops.bevel(self.bm, geom=edges, offset=min(bevel, min(size) * 0.3), segments=1, profile=0.5, affect='EDGES')

    def beam(self, a, b, t, paint, tw=None):
        a, b = Vector(a), Vector(b)
        d = b - a
        if d.length < 1e-4:
            return
        up = Vector((0, 0, 1)) if abs(d.normalized().z) < 0.95 else Vector((0, 1, 0))
        side = d.cross(up).normalized()
        self.oriented_box((a + b) / 2, d, side, (d.length, tw or t, t), paint)

    def cyl(self, centre, axis, r, length, paint, segments=16, r2=None):
        axis = Vector(axis).normalized()
        rotation = axis.to_track_quat('Z', 'Y').to_matrix().to_4x4()
        matrix = Matrix.Translation(Vector(centre) + axis * length / 2) @ rotation
        verts = bmesh.ops.create_cone(self.bm, cap_ends=True, cap_tris=False, segments=segments, radius1=r,
                                      radius2=r if r2 is None else r2, depth=length, matrix=matrix)['verts']
        self._tag(verts, paint)

    def poly(self, points, paint):
        verts = [self.bm.verts.new(p) for p in points]
        face = self.bm.faces.new(verts)
        face.material_index = self._paint(paint)

    def wheel(self, centre, r, width, axis=(0, 1, 0)):
        axis = Vector(axis).normalized()
        c = Vector(centre)
        self.cyl(c - axis * width / 2, axis, r, width, 'tire', segments=20)
        self.cyl(c - axis * (width / 2 + 0.02), axis, r * 0.55, width + 0.04, 'rim', segments=12)

    def truss(self, a, b, up, panels, chord, web, paint):
        """Planar truss: chords a→b and (a+up)→(b+up), verticals and alternating diagonals."""
        a, b, up = Vector(a), Vector(b), Vector(up)
        self.beam(a, b, chord, paint)
        self.beam(a + up, b + up, chord, paint)
        for i in range(panels + 1):
            p = a.lerp(b, i / panels)
            self.beam(p, p + up, web, paint)
            if i < panels:
                q = a.lerp(b, (i + 1) / panels)
                self.beam(p if i % 2 == 0 else p + up, q + up if i % 2 == 0 else q, web, paint)

    def lattice(self, a, b, w0, w1, h0, h1, panel, chord, web, paint):
        """Four-chord lattice boom from a to b, cross-section w×h tapering from (w0, h0) to (w1, h1)."""
        a, b = Vector(a), Vector(b)
        d = (b - a).normalized()
        side = d.cross(Vector((0, 0, 1))).normalized()
        if side.length < 1e-4:
            side = Vector((0, 1, 0))
        up = side.cross(d).normalized()
        n = max(1, round((b - a).length / panel))
        def corner(t, s, u):
            w, h = w0 + (w1 - w0) * t, h0 + (h1 - h0) * t
            return a.lerp(b, t) + side * s * w / 2 + up * u * h / 2
        corners = [(1, 1), (-1, 1), (-1, -1), (1, -1)]
        for s, u in corners:
            self.beam(corner(0, s, u), corner(1, s, u), chord, paint)
        for i in range(n):
            t0, t1 = i / n, (i + 1) / n
            for (s0, u0), (s1, u1) in zip(corners, corners[1:] + corners[:1]):
                self.beam(corner(t0, s0, u0), corner(t0, s1, u1), web, paint)
                self.beam(corner(t0, s0, u0) if i % 2 == 0 else corner(t0, s1, u1),
                          corner(t1, s1, u1) if i % 2 == 0 else corner(t1, s0, u0), web, paint)

    def handrail(self, a, b, height=1.1, paint='crane_yellow'):
        a, b = Vector(a), Vector(b)
        n = max(1, round((b - a).length / 1.8))
        for i in range(n + 1):
            p = a.lerp(b, i / n)
            self.box(p.x - 0.03, p.x + 0.03, p.y - 0.03, p.y + 0.03, p.z, p.z + height, paint)
        up = Vector((0, 0, height))
        self.beam(a + up, b + up, 0.05, paint)
        self.beam(a + up * 0.5, b + up * 0.5, 0.04, paint)

    def stairs(self, base, direction, rise, width, paint):
        """Straight stair flight with stringers, treads and handrails."""
        base, d = Vector(base), Vector(direction).normalized()
        run = rise / math.tan(math.radians(40))
        side = d.cross(Vector((0, 0, 1))).normalized()
        top = base + d * run + Vector((0, 0, rise))
        for s in (-1, 1):
            self.beam(base + side * s * width / 2, top + side * s * width / 2, 0.18, paint, tw=0.06)
            self.handrail(base + side * s * width / 2, top + side * s * width / 2, 1.0, paint)
        steps = max(2, int(rise / 0.2))
        for i in range(1, steps):
            p = base.lerp(top, i / steps)
            self.oriented_box(p, d, side, (0.25, width, 0.04), 'grating')
        return top

    def finish(self, smooth_angle=35.0):
        mesh = bpy.data.meshes.new(self.name)
        uv = self.bm.loops.layers.uv.new('UVMap')
        for face in self.bm.faces:
            n = face.normal
            ax, ay, az = abs(n.x), abs(n.y), abs(n.z)
            for loop in face.loops:
                co = loop.vert.co
                loop[uv].uv = (co.y, co.z) if ax >= ay and ax >= az else (co.x, co.z) if ay >= az else (co.x, co.y)
        self.bm.to_mesh(mesh)
        self.bm.free()
        for paint in self.paints:
            material = bpy.data.materials.get(paint) or bpy.data.materials.new(paint)
            material.diffuse_color = (*PAINT.get(paint, (0.6, 0.6, 0.6)), 1.0)
            mesh.materials.append(material)
        for polygon in mesh.polygons:
            polygon.use_smooth = True
        mesh.set_sharp_from_angle(angle=math.radians(smooth_angle))
        obj = bpy.data.objects.new(self.name, mesh)
        bpy.context.scene.collection.objects.link(obj)
        return obj


# ------------------------------------------------------------------------------------------- goliath crane

def goliath_frame(span, height):
    """Rigid A-frame leg (+y), flexible leg (-y), twin box girders with walkways, stairs, bogies, cab."""
    a = Asset('goliath_frame')
    half = span / 2
    top = height - 3.5
    for s in (-1, 1):  # rigid leg: tapered box columns + lattice bracing between them
        a.beam((s * 13, half, 3.5), (s * 4.5, half, top), 3.6, 'crane_white', tw=3.0)
    a.truss((-11.5, half - 1.6, 8), (11.5, half - 1.6, 8), (0, 0, height * 0.32), 6, 0.9, 0.45, 'crane_white')
    a.box(-10, 10, half - 1.4, half + 1.4, height * 0.42, height * 0.42 + 2.4, 'crane_white', bevel=0.15)
    a.box(-7, 7, half - 1.3, half + 1.3, height * 0.75, height * 0.75 + 2.0, 'crane_white', bevel=0.15)
    a.box(-7.5, 7.5, half - 2.5, half + 2.5, top - 2, top + 2, 'crane_white', bevel=0.3)
    for s in (-1, 1):  # flexible leg: slender columns with K-bracing
        a.beam((s * 5, -half, 3.5), (s * 4.5, -half, top), 2.4, 'crane_white', tw=2.0)
    for z in (height * 0.3, height * 0.6):
        a.beam((-5, -half, z), (0, -half, z + 8), 0.5, 'crane_white')
        a.beam((5, -half, z), (0, -half, z + 8), 0.5, 'crane_white')
        a.box(-5, 5, -half - 0.6, -half + 0.6, z - 0.7, z + 0.7, 'crane_white', bevel=0.1)
    a.box(-6, 6, -half - 2, -half + 2, top - 1.6, top + 1.6, 'crane_white', bevel=0.3)
    for y, l in ((half, 17.0), (-half, 9.0)):  # sill beams, bogies with four wheels and drive motors
        a.box(-l, l, y - 1.5, y + 1.5, 1.8, 4.2, 'crane_red', bevel=0.2)
        for s in (-1, 1):
            cx = s * (l - 2.8)
            a.box(cx - 2.8, cx + 2.8, y - 1.3, y + 1.3, 0.6, 1.9, 'crane_dark', bevel=0.12)
            for w in (-1.8, -0.6, 0.6, 1.8):
                a.wheel((cx + w, y, 0.55), 0.5, 0.35)
            a.box(cx - 0.6, cx + 0.6, y + 1.3, y + 2.1, 1.0, 2.0, 'crane_dark', bevel=0.08)
    length = span + 14
    for s in (-1, 1):  # box girders: plates, stiffener ribs, stripe, rails, walkways with handrails, lights
        x = s * 3.8
        a.box(x - 1.6, x + 1.6, -length / 2, length / 2, height, height + 7.0, 'crane_white', bevel=0.12)
        for y in [i * 3.0 - length / 2 for i in range(int(length / 3) + 1)]:
            a.box(x + s * 1.6 - 0.04 if s > 0 else x - 1.64, x + s * 1.64 if s > 0 else x - 1.6 + 0.04, y - 0.08, y + 0.08, height + 0.2, height + 6.8, 'crane_white')
        a.box(x + s * 1.6 + (0 if s > 0 else -0.06), x + s * 1.6 + (0.06 if s > 0 else 0), -length / 2, length / 2, height + 4.6, height + 6.2, 'crane_red')
        a.box(x - 0.35, x + 0.35, -length / 2 + 2, length / 2 - 2, height + 7.0, height + 7.3, 'crane_dark')
        wx = x + s * 2.7
        a.box(wx - 1.0, wx + 1.0, -length / 2 + 1, length / 2 - 1, height - 0.1, height + 0.05, 'grating')
        a.handrail((wx + s * 1.0, -length / 2 + 1, height + 0.05), (wx + s * 1.0, length / 2 - 1, height + 0.05))
        for y in (-length / 2 + 0.8, length / 2 - 0.8):
            a.cyl((x, y, height + 7.3), (0, 0, 1), 0.35, 0.7, 'crane_red', segments=10)
    for y in (half, -half):
        a.box(-6, 6, y - 1.6, y + 1.6, height - 0.5, height + 6.5, 'crane_white', bevel=0.2)
    landing = a.stairs((9.5, half + 3.2, 4.2), (-1, 0, 0), 9.0, 1.2, 'crane_yellow')  # access stairs on the rigid leg
    a.box(landing.x - 2.0, landing.x + 1.0, half + 2.4, half + 4.0, landing.z - 0.1, landing.z, 'grating')
    a.stairs((landing.x - 1.0, half + 3.2, landing.z), (1, 0, 0), 9.0, 1.2, 'crane_yellow')
    a.box(-7.2, -3.8, half - 16, half - 12, height - 3.6, height - 0.6, 'glass', bevel=0.1)          # operator cab
    a.box(-7.4, -3.6, half - 16.2, half - 11.8, height - 0.7, height - 0.4, 'crane_white', bevel=0.05)
    return a.finish()


def goliath_upper_trolley():
    a = Asset('goliath_upper_trolley')
    a.box(-6, 6, -5.5, 5.5, 0, 2.8, 'crane_red', bevel=0.15)
    for x in (-3.8, 3.8):
        for y in (-4.5, 4.5):
            a.wheel((x, y, 0.45), 0.45, 0.3, axis=(1, 0, 0))
    a.box(-5, 5, -2.5, 5.0, 2.8, 6.8, 'crane_white', bevel=0.2)                                    # machinery house
    a.box(-5.1, 5.1, -2.6, 5.1, 6.8, 7.1, 'crane_red', bevel=0.05)
    for x in (-4, -2.5, 2.5, 4):
        a.box(x - 0.5, x + 0.5, 5.0, 5.06, 4.2, 5.8, 'grating')                                     # louvres
    for x in (-2.6, 2.6):
        a.cyl((x - 1.2, -4.0, 3.4), (1, 0, 0), 0.9, 2.4, 'crane_dark', segments=20)              # hoist drums
        a.box(x - 0.5, x + 0.5, -1.0, 1.0, -0.6, 0.2, 'crane_dark', bevel=0.05)                    # sheave block
    a.handrail((-6, -5.5, 2.8), (6, -5.5, 2.8))
    a.handrail((-6, 5.5, 7.1), (6, 5.5, 7.1))
    return a.finish()


def goliath_lower_trolley():
    a = Asset('goliath_lower_trolley')
    a.box(-4.8, 4.8, -3.0, 3.0, -3.8, -0.2, 'crane_red', bevel=0.15)
    a.box(-0.12, 0.12, -0.12, 0.12, -10.3, -3.8, 'crane_dark')
    a.box(-1.0, 1.0, -0.65, 0.65, -12.3, -9.7, 'crane_yellow', bevel=0.1)
    return a.finish()


def goliath_hook():
    """Hook block with sheaves (origin at its centre; falls attach above, slings below)."""
    a = Asset('goliath_hook')
    a.box(-1.2, 1.2, -0.7, 0.7, -1.3, 1.7, 'crane_yellow', bevel=0.12)
    for z in (-0.9, 0.0, 0.9):
        a.box(-1.22, 1.22, -0.72, 0.72, z, z + 0.3, 'crane_dark')
    for x in (-0.6, 0.0, 0.6):
        a.cyl((x, -0.75, 1.0), (0, 1, 0), 0.45, 1.5, 'crane_dark', segments=16)
    a.cyl((0, 0, -1.3), (0, 0, -1), 0.25, 0.8, 'crane_dark', segments=12)
    for i in range(10):  # curved hook as a ring of short segments
        t0, t1 = math.radians(200 + i * 22), math.radians(200 + (i + 1) * 22)
        p0 = Vector((0.35 + 0.55 * math.cos(t0), 0, -2.6 + 0.55 * math.sin(t0)))
        p1 = Vector((0.35 + 0.55 * math.cos(t1), 0, -2.6 + 0.55 * math.sin(t1)))
        a.beam(p0, p1, 0.32, 'crane_dark')
    return a.finish()


# ------------------------------------------------------------------------------------------- vehicles

def transporter(load):
    a = Asset('transporter_loaded' if load else 'transporter')
    L, W, deck = 22.0, 6.5, 1.9
    a.box(-L / 2, L / 2, -W / 2, W / 2, 1.1, deck, 'vehicle_yellow', bevel=0.12)
    for s in (-1, 1):
        a.box(-L / 2 + 0.3, L / 2 - 0.3, s * W / 2 - 0.12, s * W / 2 + 0.12, 0.75, 1.15, 'crane_dark')   # side skirts
        for x in [-L / 2 + 1.3 + i * (L - 2.6) / 9 for i in range(10)]:                              # axle lines
            a.box(x - 0.35, x + 0.35, s * 1.9 - 0.3, s * 1.9 + 0.3, 0.6, 1.1, 'crane_dark')
            a.cyl((x, s * 1.9, 0.7), (0, 0, 1), 0.12, 0.6, 'pipe', segments=8)                       # suspension ram
            for w in (-0.55, 0.55):
                a.wheel((x, s * 1.9 + w, 0.45), 0.45, 0.42)
    a.box(L / 2 - 2.6, L / 2, W / 2 - 2.6, W / 2, deck, deck + 2.2, 'vehicle_yellow', bevel=0.2)       # cab
    a.box(L / 2 - 0.02, L / 2 + 0.03, W / 2 - 2.4, W / 2 - 0.2, deck + 0.8, deck + 2.0, 'glass')
    a.box(L / 2 - 2.4, L / 2 - 0.2, W / 2 - 0.02, W / 2 + 0.03, deck + 0.8, deck + 2.0, 'glass')
    a.cyl((L / 2 - 1.3, W / 2 - 1.3, deck + 2.2), (0, 0, 1), 0.18, 0.3, 'lamp_orange', segments=10)
    a.box(L / 2 - 2.9, L / 2 - 2.6, W / 2 - 0.4, W / 2, 0.6, deck + 2.2, 'crane_dark')                 # ladder rail
    for z in (0.9, 1.4, 1.9, 2.4, 2.9, 3.4):
        a.box(L / 2 - 2.9, L / 2 - 2.6, W / 2 - 0.4, W / 2 - 0.3, z, z + 0.05, 'crane_dark')
    a.box(-L / 2 - 0.15, -L / 2, -W / 2 + 0.5, W / 2 - 0.5, 1.2, 1.7, 'crane_dark')                    # bumpers
    if load:
        for x in (-6, 0, 6):
            a.box(x - 0.4, x + 0.4, -W / 2 + 0.5, W / 2 - 0.5, deck, deck + 0.45, 'steel')
        a.box(-8.5, 8.5, -W / 2 - 1.5, W / 2 + 1.5, deck + 0.45, deck + 0.75, 'antifouling', bevel=0.05)  # bottom shell
        a.box(-8.5, 8.5, -W / 2 - 1.5, W / 2 + 1.5, deck + 3.45, deck + 3.6, 'primer', bevel=0.05)       # inner bottom
        for x in [-8.5 + i * 3.4 for i in range(6)]:
            a.box(x - 0.06, x + 0.06, -W / 2 - 1.5, W / 2 + 1.5, deck + 0.75, deck + 3.45, 'primer')    # floors
        for y in (-W / 2 - 1.5, -1.6, 1.6, W / 2 + 1.5):
            a.box(-8.5, 8.5, y - 0.06, y + 0.06, deck + 0.75, deck + 3.45, 'primer')                   # girders
    return a.finish()


def truck():
    a = Asset('truck')
    a.box(4.0, 7.0, -1.25, 1.25, 1.0, 3.5, 'car_blue', bevel=0.25)
    a.box(6.98, 7.04, -1.1, 1.1, 2.3, 3.2, 'glass')
    for s in (-1, 1):
        a.box(4.6, 6.6, s * 1.25 - 0.02, s * 1.25 + 0.02, 2.3, 3.1, 'glass')
        a.box(6.6, 6.8, s * 1.5 - 0.06, s * 1.5 + 0.06, 2.3, 3.0, 'crane_dark')                         # mirrors
        a.cyl((4.4, s * 1.15, 0.9), (1, 0, 0), 0.35, 1.2, 'rim', segments=14)                          # fuel tanks
    a.box(6.95, 7.1, -1.2, 1.2, 0.6, 1.0, 'crane_dark', bevel=0.05)
    a.box(1.6, 4.0, -1.0, 1.0, 1.0, 1.5, 'crane_dark')                                                 # fifth wheel
    a.box(-9.6, 3.4, -1.25, 1.25, 1.2, 1.6, 'trailer', bevel=0.05)
    for i, (lx, ly) in enumerate(((10, 2.0), (9.5, 1.9), (9.0, 1.8))):
        a.box(-8.6 + i * 0.2, -8.6 + lx, -ly / 2, ly / 2, 1.6 + i * 0.12, 1.72 + i * 0.12, 'steel')
    for x in (5.7, 3.3, 2.0):
        for s in (-1, 1):
            a.wheel((x, s * 1.0, 0.52), 0.52, 0.4)
    for x in (-6.0, -7.3, -8.6):
        for s in (-1, 1):
            a.wheel((x, s * 1.0, 0.5), 0.5, 0.45)
    return a.finish()


def forklift():
    a = Asset('forklift')
    a.box(-1.6, 1.2, -0.65, 0.65, 0.35, 1.35, 'vehicle_yellow', bevel=0.12)
    a.box(-1.65, -0.9, -0.62, 0.62, 1.0, 2.0, 'crane_dark', bevel=0.15)
    for y in (-0.6, 0.6):
        a.beam((-0.6, y, 1.35), (-0.8, y, 2.4), 0.07, 'crane_dark')
        a.beam((0.8, y, 1.35), (0.9, y, 2.4), 0.07, 'crane_dark')
    a.box(-0.85, 0.95, -0.65, 0.65, 2.35, 2.42, 'crane_dark')
    a.box(-0.4, 0.3, -0.35, 0.35, 1.35, 1.75, 'crane_dark', bevel=0.05)                                   # seat
    for y in (-0.35, 0.35):
        a.box(1.25, 1.38, y - 0.07, y + 0.07, 0.15, 3.4, 'crane_dark')                                    # mast
    a.box(1.38, 1.5, -0.45, 0.45, 0.15, 0.9, 'crane_dark')
    for y in (-0.3, 0.3):
        a.box(1.5, 2.7, y - 0.06, y + 0.06, 0.12, 0.2, 'crane_dark')
    for x, r in ((0.8, 0.36), (-1.1, 0.3)):
        for s in (-1, 1):
            a.wheel((x, s * 0.6, r), r, 0.26)
    return a.finish()


def car(paint):
    a = Asset(paint)
    a.box(-2.3, 2.3, -0.9, 0.9, 0.3, 0.95, paint, bevel=0.22)
    a.oriented_box((-0.15, 0, 1.2), (1, 0, 0), (0, 1, 0), (2.3, 1.62, 0.5), paint, bevel=0.18)
    for s in (-1, 1):
        a.box(-1.15, 0.9, s * 0.82 - 0.02, s * 0.82 + 0.02, 1.02, 1.38, 'glass')
    # Windscreen and rear window: thin plates whose thickness axis is the glass normal (sloping up and back/forward).
    a.oriented_box((1.12, 0, 1.17), (0.55, 0, 0.83), (0, 1, 0), (0.05, 1.5, 0.58), 'glass')
    a.oriented_box((-1.42, 0, 1.17), (-0.55, 0, 0.83), (0, 1, 0), (0.05, 1.5, 0.5), 'glass')
    for x in (1.45, -1.45):
        for s in (-1, 1):
            a.wheel((x, s * 0.8, 0.33), 0.33, 0.22)
    for s in (-1, 1):
        a.box(2.28, 2.32, s * 0.6 - 0.15, s * 0.6 + 0.15, 0.65, 0.8, 'lamp')
    return a.finish(smooth_angle=50)


# ------------------------------------------------------------------------------------------- equipment

def luffing_crane():
    a = Asset('luffing_crane')
    for x in (-5, 5):  # portal: legs with diagonal braces, bogies on the quay rails
        for y in (-5, 5):
            a.box(x - 0.6, x + 0.6, y - 0.6, y + 0.6, 1.2, 12, 'quay_crane', bevel=0.1)
        a.beam((x, -5, 2.5), (x, 5, 10.5), 0.45, 'quay_crane')
        a.box(x - 1.3, x + 1.3, -6.2, 6.2, 0.4, 1.4, 'crane_dark', bevel=0.1)
        for w in (-5.2, -4.0, 4.0, 5.2):
            a.wheel((x, w, 0.45), 0.42, 0.3, axis=(1, 0, 0))
    for y in (-5, 5):
        a.beam((-5, y, 2.5), (5, y, 10.5), 0.45, 'quay_crane')
        a.box(-5.6, 5.6, y - 0.55, y + 0.55, 11, 12.6, 'quay_crane', bevel=0.1)
    a.box(-5.8, 5.8, -5.8, 5.8, 12.6, 13.4, 'quay_crane', bevel=0.15)
    a.cyl((0, 0, 13.4), (0, 0, 1), 3.2, 1.8, 'crane_dark', segments=28)                                   # slewing ring
    a.box(-9, 3, -3.6, 3.6, 15.2, 21, 'quay_crane', bevel=0.25)                                           # machinery house
    a.box(-9.05, 3.05, -3.65, 3.65, 18.4, 19.6, 'glass')
    a.box(-12.2, -8.8, -2.6, 2.6, 15.2, 19.4, 'crane_dark', bevel=0.15)                                     # counterweight
    a.box(1.2, 4.0, 2.4, 4.6, 15.6, 18.6, 'glass', bevel=0.1)                                               # cab
    a.handrail((-9, -3.6, 21), (3, -3.6, 21))
    a.handrail((-9, 3.6, 21), (3, 3.6, 21))
    for y in (-2.2, 2.2):                                                                                   # A-frame lattice
        a.truss((-5.5, y, 21), (-2.0, y, 33.5), (2.5, 0, 0), 4, 0.4, 0.18, 'quay_crane')
    a.lattice((2.0, 0, 22.0), (48.0, 0, 38.0), 2.6, 1.0, 2.2, 1.0, 3.2, 0.32, 0.12, 'quay_crane')          # main jib
    a.lattice((47.0, 0, 38.4), (51.0, 0, 37.0), 1.0, 0.7, 1.0, 0.7, 2.0, 0.22, 0.1, 'quay_crane')          # jib nose
    a.beam((-1.0, 0, 33.8), (44.0, 0, 39.5), 0.12, 'crane_dark')                                            # luffing ties
    a.beam((-1.0, 0, 33.8), (-10.0, 0, 21.5), 0.12, 'crane_dark')
    a.cyl((51.0, -0.6, 36.8), (0, 1, 0), 0.6, 1.2, 'crane_dark', segments=16)                                # tip sheave
    a.box(50.96, 51.04, -0.04, 0.04, 18.5, 36.4, 'crane_dark')                                               # hoist rope
    a.box(50.5, 51.5, -0.4, 0.4, 16.8, 18.6, 'vehicle_yellow', bevel=0.08)
    return a.finish()


def light_tower():
    a = Asset('light_tower')
    a.cyl((0, 0, 0), (0, 0, 1), 0.45, 28, 'pipe', segments=12, r2=0.22)
    a.box(-0.8, 0.8, -0.8, 0.8, 0, 0.6, 'concrete', bevel=0.08)
    a.box(-2.0, 2.0, -0.5, 0.5, 27.6, 28.0, 'grating')
    a.handrail((-2.0, -0.5, 28.0), (2.0, -0.5, 28.0), 0.9, 'pipe')
    for x in (-1.5, -0.5, 0.5, 1.5):
        a.oriented_box((x, 0.55, 28.6), (1, 0, 0), (0, 0.6, -0.8), (0.8, 0.55, 0.3), 'lamp', bevel=0.04)
    return a.finish()


def container(paint):
    a = Asset(paint)
    a.box(-6.1, 6.1, -1.22, 1.22, 0, 2.6, paint, bevel=0.04)
    for x in [-5.6 + i * 0.7 for i in range(17)]:
        for s in (-1, 1):
            a.box(x - 0.12, x + 0.12, s * 1.22 - 0.035, s * 1.22 + 0.035, 0.15, 2.45, paint)
    for x in (-6.1, 6.1):
        for y in (-1.22, 1.22):
            for z in (0, 2.6):
                a.box(x - 0.12, x + 0.12, y - 0.12, y + 0.12, z - 0.08 if z else 0, z + 0.08 if not z else z, 'crane_dark')
    return a.finish()


def plate_stack():
    a = Asset('plate_stack')
    for i in range(6):
        a.box(-6 + i * 0.05, 6 - i * 0.05, -1.5, 1.5, 0.3 + i * 0.22, 0.5 + i * 0.22, 'steel', bevel=0.02)
    for x in (-4, 0, 4):
        a.box(x - 0.2, x + 0.2, -1.6, 1.6, 0, 0.3, 'wall_base')
    return a.finish()


def pipe_stack():
    a = Asset('pipe_stack')
    for row in range(3):
        for k in range(5 - row):
            a.cyl((-6, -1.6 + k * 0.8 + row * 0.4, 0.4 + row * 0.7), (1, 0, 0), 0.38, 12, 'pipe', segments=16)
    for x in (-4, 0, 4):
        a.box(x - 0.2, x + 0.2, -2.2, 2.2, 0, 0.1, 'wall_base')
    return a.finish()


# ------------------------------------------------------------------------------------------- shoreline

def rock(name, seed, scale):
    """Weathered boulder: icosphere with seeded radial noise, flattened, about 1 m across before scaling."""
    import random
    rng = random.Random(seed)
    a = Asset(name)
    geom = bmesh.ops.create_icosphere(a.bm, subdivisions=3, radius=0.5)
    for v in geom['verts']:
        n = v.co.normalized()
        bump = 1 + 0.22 * math.sin(3.1 * n.x + seed) * math.cos(2.7 * n.y - seed) + rng.uniform(-0.08, 0.08)
        v.co = Vector((n.x * 0.5 * bump * scale[0], n.y * 0.5 * bump * scale[1], (n.z * 0.5 * bump + 0.22) * scale[2]))
    a._tag(geom['verts'], 'rock')
    return a.finish(smooth_angle=60)


def tetrapod():
    """Concrete tetrapod armour unit (~3.5 m): four tapered legs from a core towards the corners of a tetrahedron."""
    a = Asset('tetrapod')
    centre = Vector((0, 0, 1.15))
    legs = [Vector((0, 0, -1)), Vector((0.943, 0, 0.333)), Vector((-0.471, 0.816, 0.333)), Vector((-0.471, -0.816, 0.333))]
    for d in legs:
        a.cyl(centre, d, 0.78, 1.75, 'concrete', segments=12, r2=0.42)
    geom = bmesh.ops.create_icosphere(a.bm, subdivisions=2, radius=0.85, matrix=Matrix.Translation(centre))
    a._tag(geom['verts'], 'concrete')
    return a.finish(smooth_angle=40)


# ------------------------------------------------------------------------------------------- buildings

def hall(name, sx, sy, h, kind):
    """Portal-frame hall: trapezoidal cladding, gutters and downpipes, ridge skylights, framed sliding doors with
    rails into the hall, roof ventilators, annex with entrance canopy (shops) or loading bays (warehouse)."""
    a = Asset(name)
    x0, x1, y0, y1 = -sx / 2, sx / 2, -sy / 2, sy / 2
    spans = 1 if kind == '창고' else max(1, round(sy / 38))
    span_w = sy / spans
    rise = span_w * 0.09
    base = 2.4
    for y, s in ((y0, -1), (y1, 1)):
        a.box(x0, x1, y - (0.3 if s < 0 else 0), y + (0.3 if s > 0 else 0), 0, base, 'wall_base')
        a.box(x0, x1, y - (0.15 if s < 0 else 0), y + (0.15 if s > 0 else 0), base, h, 'wall_panel')
        for x in [x0 + 0.5 + i * 0.9 for i in range(int(sx / 0.9))]:                                     # trapezoidal ribs
            a.box(x - 0.12, x + 0.12, y + s * 0.15 - 0.06, y + s * 0.15 + 0.06, base, h - 0.2, 'wall_rib')
        for x in [x0 + i * 7.5 for i in range(int(sx / 7.5) + 1)]:                                       # columns
            a.box(x - 0.35, x + 0.35, y + s * 0.15, y + s * 0.5, 0, h, 'wall_base')
        a.box(x0, x1, y + s * 0.15, y + s * 0.32, h * 0.55, h * 0.55 + 1.4, 'skylight')                  # light band
        a.box(x0 - 0.5, x1 + 0.5, y + s * 0.4 - 0.25, y + s * 0.4 + 0.25, h - 0.3, h + 0.2, 'wall_base')   # gutter
        for x in [x0 + 10 + i * 30 for i in range(int(sx / 30))]:
            a.cyl((x, y + s * 0.55, 0.3), (0, 0, 1), 0.12, h - 0.4, 'wall_base', segments=8)              # downpipes
    for x, s in ((x0, -1), (x1, 1)):
        for i in range(spans):
            ya, yb = y0 + i * span_w, y0 + (i + 1) * span_w
            mid = (ya + yb) / 2
            a.box(x - (0.15 if s < 0 else 0), x + (0.15 if s > 0 else 0), ya, yb, 0, h, 'wall_panel')
            a.poly([(x + s * 0.15, ya, h), (x + s * 0.15, yb, h), (x + s * 0.15, mid, h + rise)], 'wall_panel')
            a.poly([(x + s * 0.15, yb, h), (x + s * 0.15, ya, h), (x + s * 0.15, mid, h + rise)], 'wall_panel')
            door_w = span_w * (0.62 if kind != '창고' else 0.35)
            dh = h * 0.78
            a.box(x + s * 0.15, x + s * 0.35, mid - door_w / 2, mid + door_w / 2, 0, dh, 'door')
            for k in [mid - door_w / 2 + j * 3.0 for j in range(int(door_w / 3.0) + 1)]:
                a.box(x + s * 0.35, x + s * 0.45, k - 0.1, k + 0.1, 0, dh, 'wall_rib')
            a.box(x + s * 0.15, x + s * 0.6, mid - door_w / 2 - 0.6, mid + door_w / 2 + 0.6, dh, dh + 0.8, 'wall_base')  # door head
            for k in (-door_w / 2 - 0.4, door_w / 2 + 0.4):
                a.box(x + s * 0.15, x + s * 0.6, mid + k - 0.2, mid + k + 0.2, 0, dh, 'wall_base')             # door jambs
            if kind != '창고':
                for r in (-1.8, 1.8):                                                                           # transporter rails
                    a.box(x + s * 0.2, x + s * 25, mid + r - 0.1, mid + r + 0.1, 0.0, 0.08, 'crane_dark')
    for i in range(spans):
        ya, yb = y0 + i * span_w, y0 + (i + 1) * span_w
        r = (ya + yb) / 2
        for p, q in ((ya - 0.5, r), (r, yb + 0.5)):
            pz = h if p < r else h + rise
            qz = h + rise if p < r else h
            a.poly([(x0 - 0.7, p, pz), (x1 + 0.7, p, pz), (x1 + 0.7, q, qz), (x0 - 0.7, q, qz)], 'roof')
            a.poly([(x0 - 0.7, q, qz - 0.2), (x1 + 0.7, q, qz - 0.2), (x1 + 0.7, p, pz - 0.2), (x0 - 0.7, p, pz - 0.2)], 'roof')
        for x in [x0 + 0.5 + k * 1.0 for k in range(int(sx))][::2]:                                       # roof seams
            for p, q in ((ya - 0.5, r), (r, yb + 0.5)):
                pz = h + 0.05 if p < r else h + rise + 0.05
                qz = h + rise + 0.05 if p < r else h + 0.05
                a.beam((x, p, pz), (x, q, qz), 0.06, 'roof')
        for side in (-1, 1):
            ya2, yb2 = r + side * 1.2, r + side * 3.6
            za = h + rise - rise * 1.2 / (span_w / 2) + 0.1
            zb = h + rise - rise * 3.6 / (span_w / 2) + 0.1
            a.poly([(x0 + 4, ya2, za), (x1 - 4, ya2, za), (x1 - 4, yb2, zb), (x0 + 4, yb2, zb)] if side > 0 else
                   [(x0 + 4, yb2, zb), (x1 - 4, yb2, zb), (x1 - 4, ya2, za), (x0 + 4, ya2, za)], 'skylight')
        a.box(x0 - 0.7, x1 + 0.7, r - 0.3, r + 0.3, h + rise - 0.1, h + rise + 0.25, 'roof')               # ridge cap
        for x in [x0 + 10 + k * 18 for k in range(int((sx - 15) / 18) + 1)]:
            a.cyl((x, r, h + rise), (0, 0, 1), 0.9, 1.4, 'roof_vent', segments=14)
            a.cyl((x, r, h + rise + 1.4), (0, 0, 1), 1.3, 0.35, 'roof_vent', segments=14, r2=0.5)
    if kind != '창고':
        ax0, ax1 = x0 + sx * 0.15, x0 + sx * 0.65
        a.box(ax0, ax1, y0 - 9, y0, 0, 7, 'wall_white', bevel=0.1)
        a.box(ax0 - 0.4, ax1 + 0.4, y0 - 9.4, y0, 7, 7.5, 'roof', bevel=0.05)
        for x in [ax0 + 2 + k * 3.5 for k in range(int((ax1 - ax0 - 3) / 3.5))]:
            a.box(x, x + 2.4, y0 - 9.04, y0 - 8.98, 2.6, 4.9, 'glass')
            a.box(x - 0.08, x + 2.48, y0 - 9.1, y0 - 9.0, 2.5, 2.6, 'wall_rib')
        mid = (ax0 + ax1) / 2
        a.box(mid - 3, mid + 3, y0 - 12, y0 - 9, 3.2, 3.5, 'wall_rib')                                    # entrance canopy
        for s in (-1, 1):
            a.cyl((mid + s * 2.7, y0 - 11.7, 0), (0, 0, 1), 0.1, 3.2, 'wall_rib', segments=8)
        a.box(mid - 1.2, mid + 1.2, y0 - 9.05, y0 - 8.98, 0, 2.6, 'glass')
    else:
        for x in [x0 + 12 + k * 16 for k in range(int((sx - 6) / 16))]:
            a.box(x - 3, x + 3, y0 - 0.2, y0 - 0.05, 0, 5, 'door')
            a.box(x - 3.6, x + 3.6, y0 - 3.2, y0, 5.4, 5.7, 'wall_rib')
            a.box(x - 3.4, x + 3.4, y0 - 3.0, y0, 0, 1.2, 'concrete', bevel=0.05)                          # dock leveller
    if 'PAINT' in name:
        for x in [x0 + 15 + k * 28 for k in range(int((sx - 25) / 28) + 1)]:
            a.cyl((x, y1 + 3.5, 0), (0, 0, 1), 1.3, h + rise + 9, 'stack', segments=20)
            a.cyl((x, y1 + 3.5, h + rise + 9), (0, 0, 1), 1.45, 0.4, 'crane_dark', segments=20)
            a.box(x - 1.0, x + 1.0, y1 - 6, y1 + 3.5, h + 2, h + 4, 'stack', bevel=0.1)
            a.box(x - 4, x + 4, y1 + 6, y1 + 12, 0, 6, 'wall_panel', bevel=0.15)
            for k in range(3):
                a.cyl((x - 2.5 + k * 2.5, y1 + 9, 6), (0, 0, 1), 0.8, 0.5, 'roof_vent', segments=14)
    return a.finish(smooth_angle=30)


def office():
    a = Asset('office')
    lx, ly, floor = 50, 18, 3.6
    a.box(-lx / 2, lx / 2, -ly / 2, ly / 2, 0, floor * 4, 'wall_white', bevel=0.1)
    for f in range(4):
        z = f * floor + 1.0
        for s in (-1, 1):
            a.box(-lx / 2 + 1, lx / 2 - 1, s * ly / 2 - 0.05, s * ly / 2 + 0.05, z, z + 1.9, 'glass')
            for x in [-lx / 2 + 1 + k * 1.6 for k in range(int((lx - 2) / 1.6) + 1)]:
                a.box(x - 0.05, x + 0.05, s * ly / 2 - 0.09, s * ly / 2 + 0.09, z, z + 1.9, 'wall_rib')          # mullions
        a.box(-lx / 2 - 0.2, lx / 2 + 0.2, -ly / 2 - 0.25, ly / 2 + 0.25, f * floor + 3.3, f * floor + 3.6, 'wall_rib')
    a.box(-lx / 2 - 0.3, lx / 2 + 0.3, -ly / 2 - 0.3, ly / 2 + 0.3, floor * 4, floor * 4 + 1.0, 'wall_rib', bevel=0.05)
    for x in (-12, 0, 12):
        a.box(x - 2.5, x + 2.5, -3, 3, floor * 4 + 1, floor * 4 + 2.8, 'roof_vent', bevel=0.1)               # HVAC units
        a.cyl((x - 1, 0, floor * 4 + 2.8), (0, 0, 1), 0.9, 0.3, 'crane_dark', segments=14)
    a.box(lx / 2, lx / 2 + 6, -5, 5, 3.6, 4.0, 'wall_rib', bevel=0.05)
    for s in (-1, 1):
        a.cyl((lx / 2 + 5.6, s * 4.6, 0), (0, 0, 1), 0.15, 3.6, 'wall_rib', segments=8)
    a.box(lx / 2 - 0.02, lx / 2 + 0.06, -3, 3, 0, 3.2, 'glass')
    a.box(lx / 2 + 0.05, lx / 2 + 0.12, -6, 6, floor * 4 - 2.2, floor * 4 - 0.6, 'roof')                   # sign panel
    return a.finish()


def gatehouse():
    a = Asset('gatehouse')
    a.box(-3, 3, -2, 2, 0, 3, 'wall_white', bevel=0.08)
    a.box(-3.6, 3.6, -2.6, 2.6, 3, 3.35, 'roof', bevel=0.05)
    for s in (-1, 1):
        a.box(-2.5, 2.5, s * 2 - 0.04, s * 2 + 0.04, 1.1, 2.4, 'glass')
    a.box(3.5, 4.0, 2.5, 3.0, 0, 1.1, 'crane_dark', bevel=0.05)
    for k in range(9):
        a.box(3.6, 3.9, 3.0 + k, 4.0 + k, 0.9, 1.1, 'lamp_orange' if k % 2 == 0 else 'wall_white')
    return a.finish()


def main():
    layout_path, out = Path(sys.argv[sys.argv.index('--') + 1]), Path(sys.argv[sys.argv.index('--') + 2])
    layout = json.loads(layout_path.read_text(encoding='utf-8'))
    bpy.ops.wm.read_factory_settings(use_empty=True)
    out.mkdir(parents=True, exist_ok=True)
    builders = [lambda: goliath_frame(layout['goliath']['span'], layout['goliath']['height']), goliath_upper_trolley,
                goliath_lower_trolley, goliath_hook, lambda: transporter(False), lambda: transporter(True), truck, forklift,
                luffing_crane, light_tower, lambda: container('container_blue'), lambda: container('container_orange'),
                plate_stack, pipe_stack, office, gatehouse, tetrapod,
                lambda: rock('rock_a', 1, (1.0, 0.8, 0.6)), lambda: rock('rock_b', 2, (1.2, 1.0, 0.75)),
                lambda: rock('rock_c', 3, (0.9, 1.1, 0.9))]
    builders += [lambda p=paint: car(p) for paint in ('car_white', 'car_silver', 'car_black', 'car_red')]
    builders += [lambda f=f: hall(f['id'], f['sx'], f['sy'], f['h'], f['kind']) for f in layout['halls']]
    for build in builders:
        obj = build()
        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        bpy.ops.wm.obj_export(filepath=str(out / f'{obj.name}.obj'), export_selected_objects=True, export_materials=True,
                              export_normals=True, export_uv=True, forward_axis='NEGATIVE_Z', up_axis='Y',
                              export_triangulated_mesh=False, apply_modifiers=True, path_mode='STRIP')
        print(f'exported {obj.name}: {len(obj.data.vertices)} verts, {len(obj.data.polygons)} faces')
        bpy.data.objects.remove(obj)


main()
