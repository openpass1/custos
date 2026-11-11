"""Draws the site icon of CUSTOS Monitor: the same shield as the Windows client's icon (client/tools/make-icons.py).

Usage: python make-favicon.py <public folder>     (needs Pillow: pip install pillow)
Writes favicon.svg, favicon.ico (16, 32, 48) and apple-touch-icon.png (180). The results are committed in
server/public; run this only to change the drawing, and keep the shape in step with the client script.
"""
import sys
from PIL import Image, ImageDraw

SS = 8  # supersampling factor
BLUE = (31, 94, 168, 255)
FACET = (255, 255, 255, 235)


def bezier(p0, p1, p2, n=24):
    return [((1-t)**2*p0[0] + 2*(1-t)*t*p1[0] + t*t*p2[0], (1-t)**2*p0[1] + 2*(1-t)*t*p1[1] + t*t*p2[1])
            for t in (i/n for i in range(n+1))]


def shield(cx, cy, w, h):
    """Shield outline centred at (cx, cy) with width w and height h (as in the client script)."""
    l, r, t, b = cx - w/2, cx + w/2, cy - h/2, cy + h/2
    pts = [(cx, t), (r, t + h*0.12), (r, t + h*0.50)]
    pts += bezier((r, t + h*0.50), (r, b - h*0.12), (cx, b))[1:]
    pts += bezier((cx, b), (l, b - h*0.12), (l, t + h*0.50))[1:]
    pts += [(l, t + h*0.12)]
    return pts


def left_half(pts, cx):
    return [(min(x, cx), y) for x, y in pts]


def draw(size, background=None, scale=1.0):
    S = size * SS
    im = Image.new("RGBA", (S, S), background or (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    w, h = S*0.80*scale, S*0.92*scale
    cx, cy = S*0.5, S*0.5
    d.polygon(shield(cx, cy, w, h), fill=BLUE)
    d.polygon(left_half(shield(cx, cy + h*0.02, w*0.56, h*0.60), cx), fill=FACET)
    return im.resize((size, size), Image.LANCZOS)


def svg(size=64):
    """The same shield as an SVG path: the curves of the drawing are quadratic Bézier curves, so they map one to one."""
    def outline(cx, cy, w, h):
        l, r, t, b = cx - w/2, cx + w/2, cy - h/2, cy + h/2
        return l, r, t, b, h
    f = lambda v: f"{v:.2f}".rstrip("0").rstrip(".")
    S = size
    w, h = S*0.80, S*0.92
    cx = cy = S*0.5
    l, r, t, b, _ = outline(cx, cy, w, h)
    body = (f"M{f(cx)} {f(t)}L{f(r)} {f(t + h*0.12)}L{f(r)} {f(t + h*0.5)}"
            f"Q{f(r)} {f(b - h*0.12)} {f(cx)} {f(b)}Q{f(l)} {f(b - h*0.12)} {f(l)} {f(t + h*0.5)}L{f(l)} {f(t + h*0.12)}Z")
    iw, ih = w*0.56, h*0.60
    il, ir, it, ib, _ = outline(cx, cy + h*0.02, iw, ih)
    facet = (f"M{f(cx)} {f(it)}L{f(cx)} {f(ib)}Q{f(il)} {f(ib - ih*0.12)} {f(il)} {f(it + ih*0.5)}"
             f"L{f(il)} {f(it + ih*0.12)}Z")
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {S} {S}">'
            f'<path fill="#1f5ea8" d="{body}"/>'
            f'<path fill="#fff" fill-opacity="{FACET[3]/255:.2f}" d="{facet}"/></svg>\n')


out = sys.argv[1]
with open(f"{out}/favicon.svg", "w", encoding="ascii", newline="\n") as file:
    file.write(svg())
sizes = [48, 32, 16]
images = [draw(s) for s in sizes]
images[0].save(f"{out}/favicon.ico", format="ICO", sizes=[(s, s) for s in sizes], append_images=images[1:])
# Phones put this icon on a home screen: no transparency there, so the shield sits on white with a margin.
draw(180, background=(255, 255, 255, 255), scale=0.8).convert("RGB").save(f"{out}/apple-touch-icon.png", optimize=True)
