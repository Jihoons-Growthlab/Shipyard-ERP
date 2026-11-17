"""Stitch VWorld satellite tiles into one image and build the terrain height grid. Run by fetch_satellite.py:

    Blender -b --factory-startup -P stitch_satellite.py -- <tiles_dir> <satellite.json> <out.jpg> <terrain_dir> <terrain.bytes>
        <inner_tiles_dir> <inner.jpg>

Water is classified by colour, kept only where connected to known open-sea points, smoothed. Heights come from
Terrarium tiles (h = R*256 + G + B/256 - 32768 m), sampled at each grid vertex; the water mask sets the coastline:
water at most -3 m (below the twin's sea plane), land at least +0.5 m. A coastal profile then replaces the
one-cell cliff between them: land rises at most 1:2 from the waterline and the sea floor shelves gently.
The inner (detail) imagery is only stitched; heights come from the outer grid.
"""
import json
import math
import sys
from pathlib import Path

import bpy
import numpy as np


def box_blur(a, r):
    """Separable box blur with wrap-free edges (cumulative sums)."""
    for axis in (0, 1):
        pad = [(0, 0), (0, 0)]
        pad[axis] = (r + 1, r)
        c = np.cumsum(np.pad(a, pad, mode='edge'), axis=axis)
        a = (np.take(c, range(2 * r + 1, c.shape[axis]), axis=axis) - np.take(c, range(0, c.shape[axis] - 2 * r - 1), axis=axis)) / (2 * r + 1)
    return a


def main():
    args = sys.argv[sys.argv.index('--') + 1:]
    tiles, meta, out = Path(args[0]), json.loads(Path(args[1]).read_text()), Path(args[2])
    n, size, zoom = meta['tiles'], meta['size_px'], meta['zoom']
    rgb = mosaic(tiles, zoom, meta['tile_x0'], meta['tile_y0'], n)
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    luma = 0.3 * r + 0.59 * g + 0.11 * b
    # Candidate water: blue at least on par with green and above red (dark sea and turquoise shallows); forest is
    # green-dominant, fields red/brown. Shadows can pass this test, so only water connected to the open sea counts.
    candidate = ((b > g * 0.93) & (b > r * 1.08) & (b - r > 0.025) & (luma < 0.6)).astype(np.float32)
    k = 4
    small = candidate.reshape(size // k, k, size // k, k).mean((1, 3)) > 0.5
    small = box_blur(small.astype(np.float32), 1) > 0.4                     # bridge thin fish-farm lines
    region = np.zeros_like(small)
    for px, py in meta.get('sea_seeds', []):                                 # known open-sea points (image pixels)
        cy, cx = int(py) // k, int(px) // k
        if 0 <= cy < small.shape[0] and 0 <= cx < small.shape[1]:
            region[max(0, cy - 2):cy + 3, max(0, cx - 2):cx + 3] = True
    region &= small
    for _ in range(4000):                                                    # grow through connected water
        grown = region.copy()
        grown[1:, :] |= region[:-1, :]
        grown[:-1, :] |= region[1:, :]
        grown[:, 1:] |= region[:, :-1]
        grown[:, :-1] |= region[:, 1:]
        grown &= small
        if (grown == region).all():
            break
        region = grown
    water = np.repeat(np.repeat(region.astype(np.float32), k, 0), k, 1)
    water = box_blur(water, 6)
    water = (water > 0.35).astype(np.float32)                                 # swallow small land specks in the sea
    water = np.clip(box_blur(water, 2) * 1.5 - 0.25, 0, 1)                    # soft shoreline
    save_jpeg(rgb, out)
    heights = terrain(meta, Path(args[3]), water)
    if len(args) > 6:
        inner = mosaic(Path(args[5]), meta['inner_zoom'], meta['inner_tile_x0'], meta['inner_tile_y0'], meta['inner_tiles'])
        save_jpeg(inner, args[6])
    heights.astype('<f4').tofile(args[4])
    print(f'stitched {size}x{size}, water {water.mean() * 100:.0f}%, terrain {heights.shape[0]}x{heights.shape[1]} '
          f'{heights.min():.0f}..{heights.max():.0f} m')


def mosaic(folder, zoom, x0, y0, n):
    rgb = np.zeros((n * 256, n * 256, 3), dtype=np.float32)          # row 0 = north (top of the map)
    for row in range(n):
        for col in range(n):
            image = bpy.data.images.load(str(folder / f"{zoom}_{y0 + row}_{x0 + col}.jpeg"))
            px = np.empty(256 * 256 * 4, dtype=np.float32)
            image.pixels.foreach_get(px)
            rgb[row * 256:(row + 1) * 256, col * 256:(col + 1) * 256] = px.reshape(256, 256, 4)[::-1, :, :3]
            bpy.data.images.remove(image)
    return rgb


def save_jpeg(rgb, path):
    size = rgb.shape[0]
    rgba = np.concatenate([rgb, np.ones((size, size, 1), dtype=np.float32)], -1)
    img = bpy.data.images.new(Path(path).stem, size, size, alpha=False)
    img.pixels.foreach_set(rgba[::-1].ravel())
    img.filepath_raw = str(path)
    img.file_format = 'JPEG'
    bpy.context.scene.render.image_settings.quality = 90
    img.save()
    bpy.data.images.remove(img)


def distance_from(source, within, steps):
    """Grid steps from the source cells into 'within' cells (4-neighbour growth), up to 'steps'."""
    d = np.full(source.shape, float(steps), dtype=np.float32)
    front = source.copy()
    for k in range(1, steps + 1):
        grown = front.copy()
        grown[1:, :] |= front[:-1, :]
        grown[:-1, :] |= front[1:, :]
        grown[:, 1:] |= front[:, :-1]
        grown[:, :-1] |= front[:, 1:]
        new = grown & ~front & within
        d[new] = k
        front |= new
    return d


def terrain(meta, folder, water):
    """Height grid (grid+1)² aligned with the image: vertex (i, j) = image pixel (i, j) * size / grid."""
    tz, nx, ny = meta['terrain_zoom'], meta['terrain_tiles_x'], meta['terrain_tiles_y']
    elevation = np.zeros((ny * 256, nx * 256), dtype=np.float32)
    for row in range(ny):
        for col in range(nx):
            image = bpy.data.images.load(str(folder / f"{tz}_{meta['terrain_y0'] + row}_{meta['terrain_x0'] + col}.png"))
            image.colorspace_settings.name = 'Non-Color'
            px = np.empty(256 * 256 * 4, dtype=np.float32)
            image.pixels.foreach_get(px)
            t = np.round(px.reshape(256, 256, 4)[::-1, :, :3] * 255.0)
            elevation[row * 256:(row + 1) * 256, col * 256:(col + 1) * 256] = t[..., 0] * 256 + t[..., 1] + t[..., 2] / 256 - 32768
            bpy.data.images.remove(image)
    grid, size, scale = meta['grid'], meta['size_px'], 2 ** (meta['zoom'] - tz)
    g = np.arange(grid + 1) * size / grid                                   # image pixels of the vertices
    # Global pixel at the terrain zoom, relative to the terrain mosaic origin.
    ex = (meta['tile_x0'] * 256 + g) / scale - meta['terrain_x0'] * 256
    ey = (meta['tile_y0'] * 256 + g) / scale - meta['terrain_y0'] * 256
    x0 = np.clip(np.floor(ex).astype(int), 0, elevation.shape[1] - 2)
    y0 = np.clip(np.floor(ey).astype(int), 0, elevation.shape[0] - 2)
    fx, fy = np.clip(ex - x0, 0, 1), np.clip(ey - y0, 0, 1)
    e00 = elevation[y0[:, None], x0[None, :]]
    e01 = elevation[y0[:, None], x0[None, :] + 1]
    e10 = elevation[y0[:, None] + 1, x0[None, :]]
    e11 = elevation[y0[:, None] + 1, x0[None, :] + 1]
    h = (e00 * (1 - fx)[None, :] + e01 * fx[None, :]) * (1 - fy)[:, None] + (e10 * (1 - fx)[None, :] + e11 * fx[None, :]) * fy[:, None]
    gi = np.clip(np.round(g).astype(int), 0, size - 1)
    w = water[gi[:, None], gi[None, :]]                                       # imagery decides the coastline
    h = np.clip(h, -60.0, 800.0)                                             # source has rare void spikes
    sea = w > 0.5
    h = np.where(sea, np.minimum(h, -3.0), np.maximum(h, 0.5))
    # Coastal profile (metres from the shoreline). The 30 m DEM puts hill heights right next to water cells, which
    # draws vertical cliffs: land rises at most 1:2 from the waterline (at least 1:50), the sea floor shelves
    # between 1:12 and 1:4 so the shore enters the water gently.
    cell = 2 * math.pi * 6378137.0 * math.cos(math.radians(meta['centre_lat'])) / (256 * 2 ** meta['zoom']) * size / grid
    d_sea = distance_from(~sea, sea, 60) * cell
    d_land = distance_from(sea, ~sea, 60) * cell
    land = np.clip(h, 0.3 + 0.02 * d_land, 0.3 + 0.5 * d_land)
    seabed = np.clip(h, -(0.5 + 0.25 * d_sea), -(0.3 + 0.08 * d_sea))
    h = np.where(sea, seabed, land)
    return h


main()
