"""Renders the app icon: the whole Mandelbrot set (the home view) on a rounded tile.

    python tools/make_icon.py            -> Fractal/Assets/fractal.ico (+ fractal.png preview)

Each icon size is rendered on its own (supersampled), so small sizes stay crisp instead of
being shrunk from the big one. Needs numpy and Pillow.
"""
import os
import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, 'Fractal', 'Assets')
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]

# The app's "Classic" palette (positions 0..1, RGB), as in Palette.BuiltIns.
STOPS = [(0.0, 0x000764), (0.16, 0x206BCB), (0.42, 0xEDFFFF), (0.6425, 0xFFAA00), (0.8575, 0x000200)]


def palette(t):
    """Colour at t (0..1, wraps) by linear interpolation through STOPS."""
    t = np.mod(t, 1.0)
    pos = np.array([s[0] for s in STOPS] + [1.0])
    cols = np.array([[(c >> 16) & 255, (c >> 8) & 255, c & 255] for _, c in STOPS] + [[0, 7, 100]], float)
    i = np.clip(np.searchsorted(pos, t, side='right') - 1, 0, len(STOPS) - 1)
    f = (t - pos[i]) / (pos[i + 1] - pos[i])
    return cols[i] + (cols[i + 1] - cols[i]) * f[..., None]


def render(size):
    ss = 4 if size >= 64 else 8          # supersampling per axis
    n = size * ss
    # The set spans about -2 .. 0.47 across and -1.13 .. 1.13 up; frame it with a small margin.
    cx, cy, width = -0.76, 0.0, 2.95
    xs = cx + (np.arange(n) + 0.5 - n / 2) * width / n
    ys = cy - (np.arange(n) + 0.5 - n / 2) * width / n
    c = xs[None, :] + 1j * ys[:, None]
    z = np.zeros_like(c)
    smooth = np.full(c.shape, -1.0)
    alive = np.ones(c.shape, bool)
    for it in range(400):
        z[alive] = z[alive] * z[alive] + c[alive]
        mag = np.abs(z)
        esc = alive & (mag > 16)
        smooth[esc] = it + 1 - np.log2(np.log2(mag[esc]))
        alive &= ~esc
    inside = smooth < 0

    # Glow: far from the set is deep blue, near the edge white and gold (like the app's Classic).
    t = np.sqrt(np.maximum(smooth, 0)) / 7.5
    rgb = palette(np.clip(t, 0, 0.62))
    rgb[inside] = 0

    # Rounded tile with a transparent outside.
    yy, xx = np.mgrid[0:n, 0:n] + 0.5
    r = n * 0.18
    dx = np.maximum(np.abs(xx - n / 2) - (n / 2 - r), 0)
    dy = np.maximum(np.abs(yy - n / 2) - (n / 2 - r), 0)
    alpha = (np.hypot(dx, dy) <= r).astype(float) * 255

    img = np.dstack([rgb, alpha]).reshape(size, ss, size, ss, 4).mean(axis=(1, 3))
    return Image.fromarray(np.clip(img + 0.5, 0, 255).astype(np.uint8), 'RGBA')


def main():
    os.makedirs(OUT, exist_ok=True)
    images = {s: render(s) for s in SIZES}
    big = images[256]
    big.save(os.path.join(OUT, 'fractal.png'))
    big.save(os.path.join(OUT, 'fractal.ico'), sizes=[(s, s) for s in SIZES],
             append_images=[images[s] for s in SIZES if s != 256])
    print('wrote', os.path.join(OUT, 'fractal.ico'))


if __name__ == '__main__':
    main()
