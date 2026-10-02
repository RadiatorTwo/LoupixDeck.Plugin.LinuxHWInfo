#!/usr/bin/env python3
"""LoupixDeck LinuxHwInfo plugin icon (terminal with a reading history, matte, graphite).

Same shading and accent as the other plugin icons. The subject is a terminal: a matte bezel
around a dark screen with a shell prompt and a glowing line of readings. The graphite tile with a
soft top light sets it apart from the night blue of the HWiNFO icon.

Requires: pip install pillow numpy
Usage:    python make_icon.py [output_dir]
Writes icon_{256,128,64,32,16}.png (RGBA, transparent corners).
"""
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

SIZE = 256   # design size (px)
SS = 4       # supersampling
N = SIZE * SS
YY, XX = np.mgrid[0:N, 0:N].astype(np.float32)
XX = (XX + 0.5) / SS
YY = (YY + 0.5) / SS


def oklch(L, C, h, a=1.0):
    hr = math.radians(h)
    A, B = C * math.cos(hr), C * math.sin(hr)
    l = (L + 0.3963377774 * A + 0.2158037573 * B) ** 3
    m = (L - 0.1055613458 * A - 0.0638541728 * B) ** 3
    s = (L - 0.0894841775 * A - 1.2914855480 * B) ** 3
    lin = [4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
           -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
           -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s]
    out = [12.92 * c if c <= 0.0031308 else 1.055 * max(c, 0) ** (1 / 2.4) - 0.055 for c in lin]
    return (*[min(max(c, 0.0), 1.0) for c in out], a)


# Colors: accent shared with the Audio icon, background graphite instead of night blue
BG_HUE = 230
BG_TOP = oklch(0.30, 0.012, BG_HUE)
BG_BOTTOM = oklch(0.19, 0.012, BG_HUE)
EDGE = oklch(0.38, 0.012, BG_HUE)
ACCENT = oklch(0.80, 0.13, 200)

# Reading history on the screen: values (0..1), oldest first
HISTORY = [0.30, 0.45, 0.35, 0.62, 0.50, 0.78, 0.66]

# ---------- Masks ----------
def _mask(draw_fn):
    im = Image.new("L", (N, N), 0)
    draw_fn(ImageDraw.Draw(im))
    return np.asarray(im, dtype=np.float32) / 255.0


def circle(cx, cy, r):
    return _mask(lambda d: d.ellipse([(cx - r) * SS, (cy - r) * SS, (cx + r) * SS - 1, (cy + r) * SS - 1], fill=255))


def rrect(x, y, w, h, r):
    return _mask(lambda d: d.rounded_rectangle([x * SS, y * SS, (x + w) * SS - 1, (y + h) * SS - 1], radius=r * SS, fill=255))


def polygon(points):
    return _mask(lambda d: d.polygon([(x * SS, y * SS) for x, y in points], fill=255))


def blur(mask, px):
    if px <= 0:
        return mask
    im = Image.fromarray((np.clip(mask, 0, 1) * 255).astype(np.uint8))
    im = im.filter(ImageFilter.GaussianBlur(px / 2 * SS))  # CSS blur = 2*sigma
    return np.asarray(im, dtype=np.float32) / 255.0


def shift(mask, dx, dy, fill=0.0):
    out = np.full_like(mask, fill)
    sx, sy = int(round(dx * SS)), int(round(dy * SS))
    h, w = mask.shape
    out[max(sy, 0):h + min(sy, 0), max(sx, 0):w + min(sx, 0)] = mask[max(-sy, 0):h + min(-sy, 0), max(-sx, 0):w + min(-sx, 0)]
    return out


# ---------- Compositing ----------
canvas = np.zeros((N, N, 4), dtype=np.float32)  # straight RGBA


def paint(color, alpha):
    """color: RGBA tuple or HxWx3 array; alpha: HxW mask (multiplied by the color's alpha)."""
    global canvas
    if isinstance(color, tuple):
        rgb = np.array(color[:3], dtype=np.float32)[None, None, :]
        a = alpha * color[3]
    else:
        rgb, a = color, alpha
    a = a[..., None]
    ca = canvas[..., 3:4]
    oa = a + ca * (1 - a)
    orgb = (rgb * a + canvas[..., :3] * ca * (1 - a)) / np.maximum(oa, 1e-6)
    canvas = np.concatenate([orgb, oa], axis=-1)


def drop_shadow(shape, dx, dy, blur_px, color, clip):
    paint(color, blur(shift(shape, dx, dy), blur_px) * clip)


def inset_shadow(shape, dx, dy, blur_px, color):
    paint(color, blur(shift(1 - shape, dx, dy, fill=1.0), blur_px) * shape)


def linear_gradient(box, css_deg, stops):
    x, y, w, h = box
    th = math.radians(css_deg)
    dx, dy = math.sin(th), -math.cos(th)
    L = abs(w * dx) + abs(h * dy)
    t = ((XX - (x + w / 2)) * dx + (YY - (y + h / 2)) * dy) / L + 0.5
    t = np.clip(t, 0, 1)
    pos = [s[0] for s in stops]
    return np.stack([np.interp(t, pos, [s[1][i] for s in stops]) for i in range(3)], axis=-1).astype(np.float32)


# ---------- Draw ----------
C = 128
icon = rrect(0, 0, SIZE, SIZE, 58)

# Background: vertical graphite gradient, soft light from the top, 1px inner edge
paint(linear_gradient((0, 0, SIZE, SIZE), 180, [(0, BG_TOP[:3]), (1, BG_BOTTOM[:3])]), icon)
t = np.clip(np.hypot((XX - C) / 1.4, YY + 30) / 190, 0, 1)
paint((1, 1, 1, 1.0), icon * (0.07 * (1 - t)))
paint(EDGE, icon - rrect(1, 1, SIZE - 2, SIZE - 2, 57))

# Terminal: matte bezel around a dark screen
TW, TH, TR = 180, 144, 26
TX, TY = C - TW / 2, C - TH / 2 + 2
term = rrect(TX, TY, TW, TH, TR)
drop_shadow(term, 0, 16, 24, oklch(0.04, 0.04, 260, 0.80), icon)
drop_shadow(term, 0, 4, 3, oklch(0.06, 0.03, 260, 0.55), icon)
paint(linear_gradient((TX, TY, TW, TH), 160, [(0, oklch(0.93, 0.006, 260)[:3]), (1, oklch(0.76, 0.01, 260)[:3])]), term)
inset_shadow(term, 0, -3, 4, oklch(0.4, 0.02, 260, 0.35))
inset_shadow(term, 0, 2, 2, (1, 1, 1, 0.45))

SX, SY, SW, SH = TX + 12, TY + 12, TW - 24, TH - 24
screen = rrect(SX, SY, SW, SH, TR - 10)
paint(oklch(0.20, 0.02, 260), screen)
inset_shadow(screen, 0, 4, 6, oklch(0.05, 0.03, 260, 0.70))


def stroke(points, width):
    im = Image.new("L", (N, N), 0)
    d = ImageDraw.Draw(im)
    d.line([(x * SS, y * SS) for x, y in points], fill=255, width=int(width * SS), joint="curve")
    for x, y in (points[0], points[-1]):
        d.ellipse([(x - width / 2) * SS, (y - width / 2) * SS, (x + width / 2) * SS, (y + width / 2) * SS], fill=255)
    return np.asarray(im, dtype=np.float32) / 255.0


# Prompt: chevron and cursor
prompt = np.maximum(stroke([(SX + 16, SY + 15), (SX + 28, SY + 25), (SX + 16, SY + 35)], 6),
                    rrect(SX + 36, SY + 32, 22, 6, 3))

# Reading history: a line with a fading area under it
gx0, gx1, gbase, gtop = SX + 12, SX + SW - 12, SY + SH - 12, SY + 50
pts = [(gx0 + (gx1 - gx0) * k / (len(HISTORY) - 1), gbase - v * (gbase - gtop)) for k, v in enumerate(HISTORY)]
area = polygon([(gx0, gbase)] + pts + [(gx1, gbase)])
fade = np.clip((gbase - YY) / (gbase - gtop), 0, 1)
paint(ACCENT, area * fade * 0.35 * screen)
line = stroke(pts, 6) * screen
glow = np.maximum(line, prompt)
paint(ACCENT, blur(glow, 10) * 0.6 * screen)
paint(ACCENT, glow * screen)

# Clip to the icon shape
canvas[..., 3] *= icon

# ---------- Export ----------
if __name__ == "__main__":
    out_dir = sys.argv[1] if len(sys.argv) > 1 else "."
    os.makedirs(out_dir, exist_ok=True)
    big = Image.fromarray((np.clip(canvas, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA")
    for s in (256, 128, 64, 32, 16):
        path = os.path.join(out_dir, f"icon_{s}.png")
        big.resize((s, s), Image.LANCZOS).save(path)
        print("written:", path)
