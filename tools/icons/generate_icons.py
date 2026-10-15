#!/usr/bin/env python3
"""
Generates Bridge's icon set from one geometric design (no external files):
a blue rounded square with a white arch joining two nodes - the phone and the PC.

  python3 tools/icons/generate_icons.py            # writes into the repo
  python3 tools/icons/generate_icons.py --preview out.png
  python3 tools/icons/generate_icons.py --android-paths   # path data for the Android vectors
  python3 tools/icons/generate_icons.py --check           # verify the committed assets

Windows: windows/Bridge.Windows/Assets/*.png (MRT scale/targetsize variants) + Bridge.ico
Android: the vector drawables are hand-written to the same geometry (see android/.../res/drawable);
this script prints their path data so the two stay in sync.
Needs Pillow.
"""
import math
import os
import sys
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ASSETS = os.path.join(ROOT, "windows", "Bridge.Windows", "Assets")

BLUE_TOP = (0, 120, 212)      # #0078D4
BLUE_BOTTOM = (0, 84, 166)    # #0054A6
WHITE = (255, 255, 255, 255)

# Design on a 100x100 grid (the Android vectors use the same numbers, offset into a 108dp canvas):
# an arch bridge - a deck across the top, an arch under it, posts between.
DECK_Y = 28.0
DECK_X = (12.0, 88.0)
DECK_W = 9.0
ARCH_FROM = (17.0, 74.0)
ARCH_TO = (83.0, 74.0)
ARCH_CTRL_Y = 34.0
ARCH_W = 9.0
HANGERS = (27.0, 39.0, 50.0, 61.0, 73.0)
HANGER_W = 5.0


def bezier(t):
    (x0, y0), (x3, y3) = ARCH_FROM, ARCH_TO
    c1, c2 = (x0 + 4, ARCH_CTRL_Y), (x3 - 4, ARCH_CTRL_Y)
    mt = 1 - t
    return (mt**3 * x0 + 3 * mt * mt * t * c1[0] + 3 * mt * t * t * c2[0] + t**3 * x3,
            mt**3 * y0 + 3 * mt * mt * t * c1[1] + 3 * mt * t * t * c2[1] + t**3 * y3)


def arch_y(x):
    lo, hi = 0.0, 1.0
    for _ in range(40):
        mid = (lo + hi) / 2
        if bezier(mid)[0] < x: lo = mid
        else: hi = mid
    return bezier(lo)[1]


def stroke(d, pts, w, scale, ox, oy, color):
    r = w * scale / 2
    for (x, y) in pts:
        cx, cy = ox + x * scale, oy + y * scale
        d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=color)


def draw_glyph(d, scale, ox, oy, color):
    """The bridge glyph in 100-unit design space, scaled by `scale`."""
    stroke(d, [bezier(i / 300) for i in range(301)], ARCH_W, scale, ox, oy, color)
    stroke(d, [(DECK_X[0] + (DECK_X[1] - DECK_X[0]) * i / 300, DECK_Y) for i in range(301)], DECK_W, scale, ox, oy, color)
    for hx in HANGERS:
        bottom = arch_y(hx)
        stroke(d, [(hx, DECK_Y + (bottom - DECK_Y) * i / 100) for i in range(101)], HANGER_W, scale, ox, oy, color)


def android_paths():
    """Path data for the Android vectors (108dp canvas, design centred in the 66dp safe zone)."""
    k, o = 0.66, 21.0  # 100 units -> 66dp, offset into the safe zone
    f = lambda v: f"{o + v * k:.2f}"
    (x0, y0), (x3, y3) = ARCH_FROM, ARCH_TO
    arch = f"M{f(x0)},{f(y0)} C{f(x0 + 4)},{f(ARCH_CTRL_Y)} {f(x3 - 4)},{f(ARCH_CTRL_Y)} {f(x3)},{f(y3)}"
    deck = f"M{f(DECK_X[0])},{f(DECK_Y)} L{f(DECK_X[1])},{f(DECK_Y)}"
    hangers = " ".join(f"M{f(h)},{f(DECK_Y)} L{f(h)},{f(arch_y(h))}" for h in HANGERS)
    return {"arch": (arch, ARCH_W * k), "deck": (deck, DECK_W * k), "hangers": (hangers, HANGER_W * k)}


def gradient(size):
    w, h = size
    g = Image.new("RGBA", size)
    px = g.load()
    for y in range(h):
        t = y / max(1, h - 1)
        c = tuple(int(BLUE_TOP[i] * (1 - t) + BLUE_BOTTOM[i] * t) for i in range(3)) + (255,)
        for x in range(w):
            px[x, y] = c
    return g


def icon(px, plate=True, glyph_color=WHITE, margin=0.0):
    """Square icon. plate=False: glyph only (transparent background)."""
    ss = 4
    S = px * ss
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    inner = S * (1 - 2 * margin)
    off = S * margin
    if plate:
        mask = Image.new("L", (S, S), 0)
        ImageDraw.Draw(mask).rounded_rectangle([off, off, off + inner - 1, off + inner - 1], radius=inner * 0.22, fill=255)
        img.paste(gradient((S, S)), (0, 0), mask)
    d = ImageDraw.Draw(img)
    draw_glyph(d, inner / 100.0, off, off, glyph_color)
    return img.resize((px, px), Image.LANCZOS)


def wide(w, h):
    """Wide tile: the icon centred on a transparent canvas."""
    canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    side = int(h * 0.62)
    canvas.alpha_composite(icon(side), ((w - side) // 2, (h - side) // 2))
    return canvas


def save(img, name):
    path = os.path.join(ASSETS, name)
    img.save(path, optimize=True)
    return path


EXPECTED = {  # base name -> size at 100%
    "Square44x44Logo": (44, 44), "Square150x150Logo": (150, 150), "Wide310x150Logo": (310, 150),
    "StoreLogo": (50, 50), "LockScreenLogo": (24, 24), "SplashScreen": (620, 300),
}


def check():
    """Every asset the manifest and XAML use exists, at the size its scale/targetsize says."""
    import re
    problems = []
    manifest = open(os.path.join(ROOT, "windows", "Bridge.Windows", "Package.appxmanifest"), encoding="utf-8").read()
    for ref in sorted(set(re.findall(r'Assets\\([A-Za-z0-9]+)\.png', manifest))):
        if not os.path.exists(os.path.join(ASSETS, ref + ".png")):
            problems.append(f"manifest uses Assets\\{ref}.png, which is missing")
    for base, (w, h) in EXPECTED.items():
        for q, k in {"": 1.0, ".scale-125": 1.25, ".scale-150": 1.5, ".scale-200": 2.0, ".scale-400": 4.0}.items():
            f = os.path.join(ASSETS, f"{base}{q}.png")
            if not os.path.exists(f): problems.append(f"missing {base}{q}.png"); continue
            if Image.open(f).size != (round(w * k), round(h * k)): problems.append(f"{base}{q}.png is {Image.open(f).size}")
        if os.path.exists(os.path.join(ASSETS, f"{base}.scale-100.png")):
            problems.append(f"{base}.scale-100.png duplicates {base}.png (MRT build error)")
    for t in (16, 24, 32, 48, 256):
        for alt in ("", "_altform-unplated"):
            f = os.path.join(ASSETS, f"Square44x44Logo.targetsize-{t}{alt}.png")
            if not os.path.exists(f) or Image.open(f).size != (t, t): problems.append(f"bad targetsize-{t}{alt}")
    ico = Image.open(os.path.join(ASSETS, "Bridge.ico"))
    if not {(16, 16), (32, 32), (48, 48), (256, 256)} <= set(ico.info.get("sizes", [])): problems.append("Bridge.ico lacks 16/32/48/256")
    for p in problems: print("PROBLEM", p)
    print(f"asset check: {len(problems)} problems")
    return 1 if problems else 0


def main():
    if len(sys.argv) == 2 and sys.argv[1] == "--check":
        sys.exit(check())
    if len(sys.argv) == 2 and sys.argv[1] == "--android-paths":
        for name, (data, width) in android_paths().items():
            print(f"{name}: strokeWidth={width:.2f} pathData=\"{data}\"")
        return
    if len(sys.argv) == 3 and sys.argv[1] == "--preview":
        sheet = Image.new("RGBA", (16 + 24 + 32 + 48 + 64 + 128 + 256 + 8 * 9, 272), (240, 240, 240, 255))
        x = 8
        for s in (16, 24, 32, 48, 64, 128, 256):
            sheet.alpha_composite(icon(s), (x, 8))
            x += s + 8
        sheet.save(sys.argv[2])
        return

    os.makedirs(ASSETS, exist_ok=True)
    for f in os.listdir(ASSETS):
        if f.endswith(".png") or f.endswith(".ico"):
            os.remove(os.path.join(ASSETS, f))
    scales = {"": 1.0, ".scale-125": 1.25, ".scale-150": 1.5, ".scale-200": 2.0, ".scale-400": 4.0}
    written = []
    for q, k in scales.items():
        written.append(save(icon(round(44 * k)), f"Square44x44Logo{q}.png"))
        written.append(save(icon(round(150 * k), margin=0.16), f"Square150x150Logo{q}.png"))
        written.append(save(wide(round(310 * k), round(150 * k)), f"Wide310x150Logo{q}.png"))
        written.append(save(icon(round(50 * k)), f"StoreLogo{q}.png"))
        written.append(save(icon(round(24 * k), plate=False), f"LockScreenLogo{q}.png"))
        written.append(save(wide(round(620 * k), round(300 * k)), f"SplashScreen{q}.png"))
    # Taskbar / Start / Explorer sizes (targetsize), plated and unplated.
    for t in (16, 24, 32, 48, 256):
        written.append(save(icon(t), f"Square44x44Logo.targetsize-{t}.png"))
        written.append(save(icon(t), f"Square44x44Logo.targetsize-{t}_altform-unplated.png"))
    # .ico for the window, taskbar (unpackaged) and tray.
    ico_sizes = [(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 256)]
    icon(256).save(os.path.join(ASSETS, "Bridge.ico"), sizes=ico_sizes)
    written.append(os.path.join(ASSETS, "Bridge.ico"))
    print(f"wrote {len(written)} files to {os.path.relpath(ASSETS, ROOT)}")


if __name__ == "__main__":
    main()
