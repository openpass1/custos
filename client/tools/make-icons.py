"""Draws the CUSTOS shield icons (app icon and tray states) as multi-size .ico files.

Usage: python make-icons.py <output folder>     (needs Pillow: pip install pillow)
The results are committed in src/Custos.Gui/Resources; run this only to change the drawing.
"""
import sys
from PIL import Image, ImageDraw

SS = 8  # supersampling factor

def bezier(p0, p1, p2, n=24):
    return [((1-t)**2*p0[0] + 2*(1-t)*t*p1[0] + t*t*p2[0], (1-t)**2*p0[1] + 2*(1-t)*t*p1[1] + t*t*p2[1])
            for t in (i/n for i in range(n+1))]

def shield(cx, cy, w, h):
    """Shield outline centred at (cx, cy) with width w and height h."""
    l, r, t, b = cx - w/2, cx + w/2, cy - h/2, cy + h/2
    pts = [(cx, t), (r, t + h*0.12), (r, t + h*0.50)]
    pts += bezier((r, t + h*0.50), (r, b - h*0.12), (cx, b))[1:]
    pts += bezier((cx, b), (l, b - h*0.12), (l, t + h*0.50))[1:]
    pts += [(l, t + h*0.12)]
    return pts

def left_half(pts, cx):
    return [(min(x, cx), y) for x, y in pts]

def draw(size, body, facet, badge=None, shield_scale=1.0):
    S = size * SS
    im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    w, h = S*0.80*shield_scale, S*0.92*shield_scale
    cx = S*0.5 if badge is None else S*0.44
    cy = S*0.5 if badge is None else S*0.47
    d.polygon(shield(cx, cy, w, h), fill=body)
    inner = shield(cx, cy + h*0.02, w*0.56, h*0.60)
    d.polygon(left_half(inner, cx), fill=facet)
    if badge is not None:
        color, mark = badge
        r = S*0.25
        bx, by = S - r - S*0.02, S - r - S*0.02
        ring = r + S*0.06
        # transparent ring separates the badge from the shield
        d.ellipse((bx-ring, by-ring, bx+ring, by+ring), fill=(0, 0, 0, 0))
        d.ellipse((bx-r, by-r, bx+r, by+r), fill=color)
        lw = max(SS, int(r*0.30))
        if mark == "check":
            d.line([(bx - r*0.50, by + r*0.02), (bx - r*0.12, by + r*0.40), (bx + r*0.52, by - r*0.38)],
                   fill="white", width=lw, joint="curve")
        elif mark == "dash":
            d.line([(bx - r*0.50, by), (bx + r*0.50, by)], fill="white", width=lw)
    return im.resize((size, size), Image.LANCZOS)

def save(path, sizes, **kw):
    images = [draw(s, **kw) for s in sizes]
    images[0].save(path, format="ICO", sizes=[(s, s) for s in sizes], append_images=images[1:])

out = sys.argv[1]
BLUE, BLUE_FACET = (31, 94, 168, 255), (255, 255, 255, 235)
GREY, GREY_FACET = (128, 134, 140, 255), (255, 255, 255, 200)
save(f"{out}/custos.ico", [256, 64, 48, 40, 32, 24, 20, 16], body=BLUE, facet=BLUE_FACET)
tray = [48, 40, 32, 24, 20, 16]
save(f"{out}/tray-ok.ico", tray, body=BLUE, facet=BLUE_FACET, badge=((46, 160, 67, 255), "check"))
save(f"{out}/tray-down.ico", tray, body=GREY, facet=GREY_FACET, badge=((110, 116, 122, 255), "dash"))
