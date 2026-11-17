"""Seamless detail textures for ShipyardTwin, generated with numpy inside Blender:

    Blender -b --factory-startup -P pipeline/blender/build_textures.py -- <out_dir>

Each texture tiles perfectly (noise is filtered white noise in the frequency domain, which is periodic).
Albedo maps are near-white greyscale detail (the paint colour comes from the material and is multiplied);
normal maps are derived from height maps (RGB, OpenGL convention). The real size of one tile in metres is in
TILES and must match ShipPalette's texture table in Unity.
"""
import sys
from pathlib import Path

import bpy
import numpy as np

SIZE = 1024
RNG = np.random.default_rng(20260417)
TILES = {'concrete': 12.0, 'asphalt': 8.0, 'steel': 4.0, 'hull': 10.0, 'cladding': 2.0, 'water': 24.0}


def noise(scale, octaves=5, falloff=0.55):
    """Tileable fractal noise in [0, 1]; scale = base feature frequency per tile."""
    out = np.zeros((SIZE, SIZE))
    amplitude = 1.0
    fy = np.fft.fftfreq(SIZE)[:, None] * SIZE
    fx = np.fft.fftfreq(SIZE)[None, :] * SIZE
    radius = np.sqrt(fx * fx + fy * fy)
    for octave in range(octaves):
        f = scale * 2 ** octave
        band = np.exp(-((radius - f) ** 2) / (2 * (0.6 * f + 0.5) ** 2))
        layer = np.real(np.fft.ifft2(np.fft.fft2(RNG.standard_normal((SIZE, SIZE))) * band))
        out += amplitude * layer / (np.abs(layer).max() + 1e-9)
        amplitude *= falloff
    return (out - out.min()) / (out.max() - out.min() + 1e-9)


def streaks(scale_x, length):
    """Vertical run-down streaks: noise stretched along y (rows)."""
    out = np.zeros((SIZE, SIZE))
    fy = np.fft.fftfreq(SIZE)[:, None] * SIZE
    fx = np.fft.fftfreq(SIZE)[None, :] * SIZE
    band = np.exp(-(fx / scale_x) ** 2 - (fy / (scale_x / length)) ** 2) * (np.abs(fx) > 1)
    layer = np.real(np.fft.ifft2(np.fft.fft2(RNG.standard_normal((SIZE, SIZE))) * band))
    return (layer - layer.min()) / (layer.max() - layer.min() + 1e-9)


def lines(period_px, width_px, axis):
    """Soft lines every period_px pixels (0 = away, 1 = on the line)."""
    coord = np.arange(SIZE)
    d = np.abs(((coord + period_px / 2) % period_px) - period_px / 2)
    profile = np.clip(1 - d / width_px, 0, 1) ** 1.5
    return profile[None, :].repeat(SIZE, 0) if axis == 'x' else profile[:, None].repeat(SIZE, 1)


def normal_from_height(height, strength):
    dx = (np.roll(height, -1, 1) - np.roll(height, 1, 1)) * strength
    dy = (np.roll(height, -1, 0) - np.roll(height, 1, 0)) * strength
    n = np.stack([-dx, -dy, np.ones_like(height)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n * 0.5 + 0.5


def save(name, rgb, out, linear=False):
    image = bpy.data.images.new(name, SIZE, SIZE, alpha=True, float_buffer=False)
    image.colorspace_settings.name = 'Non-Color' if linear else 'sRGB'
    rgba = np.concatenate([np.clip(rgb, 0, 1), np.ones((SIZE, SIZE, 1))], -1)
    image.pixels.foreach_set(rgba[::-1].astype(np.float32).ravel())   # Blender rows start at the bottom
    image.filepath_raw = str(out / f'{name}.png')
    image.file_format = 'PNG'
    image.save()
    print(f'texture {name}')


def grey(v):
    return np.repeat(v[..., None], 3, -1)


def main():
    out = Path(sys.argv[sys.argv.index('--') + 1])
    out.mkdir(parents=True, exist_ok=True)
    px = lambda tile, metres: SIZE / TILES[tile] * metres

    # Concrete: slab joints every 6 m, trowel mottling, stains.
    joints = np.maximum(lines(px('concrete', 6), 2.5, 'x'), lines(px('concrete', 6), 2.5, 'y'))
    mottle, stains = noise(6), noise(2, 3)
    save('concrete_albedo', grey(0.92 + 0.05 * mottle - 0.05 * np.clip(stains - 0.6, 0, 1) * 2.5 - 0.3 * joints), out)
    save('concrete_normal', normal_from_height(0.3 * noise(40, 3) - 1.5 * joints, 4.0), out, linear=True)

    # Asphalt: aggregate speckle, tyre-polished bands, a few cracks.
    speckle = noise(90, 2)
    save('asphalt_albedo', grey(0.85 + 0.15 * speckle - 0.08 * noise(3, 2)), out)
    save('asphalt_normal', normal_from_height(speckle, 6.0), out, linear=True)

    # Painted steel: mottled paint, run-down streaks, edge-free so it suits cranes, vehicles and decks.
    run = streaks(40, 18)
    save('steel_albedo', grey(0.94 + 0.05 * noise(5) - 0.12 * np.clip(run - 0.55, 0, 1) * 2), out)
    save('steel_normal', normal_from_height(0.15 * noise(60, 2) + 0.2 * noise(8, 2), 3.0), out, linear=True)

    # Hull plating: seams every 2.5 m (rows) and 5 m (butts) with weld beads, slight plate distortion.
    seams = np.maximum(lines(px('hull', 2.5), 3.0, 'y'), lines(px('hull', 5.0), 3.0, 'x'))
    distortion = noise(3, 2)
    save('hull_albedo', grey(0.95 + 0.04 * noise(6) - 0.10 * seams - 0.08 * np.clip(streaks(30, 25) - 0.6, 0, 1) * 2), out)
    save('hull_normal', normal_from_height(0.6 * distortion + 2.0 * seams, 3.5), out, linear=True)

    # Profiled cladding: trapezoidal corrugation every 0.25 m across x, light weathering.
    coord = (np.arange(SIZE) % px('cladding', 0.25)) / px('cladding', 0.25)
    trapezoid = np.clip(np.minimum(coord, 1 - coord) * 4 - 0.3, 0, 1)[None, :].repeat(SIZE, 0)
    save('cladding_albedo', grey(0.95 + 0.04 * noise(4) - 0.06 * trapezoid), out)
    save('cladding_normal', normal_from_height(trapezoid, 6.0), out, linear=True)

    # Water: wind ripples (normal only).
    save('water_normal', normal_from_height(noise(10, 4, 0.6), 7.0), out, linear=True)


main()
