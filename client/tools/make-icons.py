"""Draws the CUSTOS shield icons (app icon and tray states) as multi-size .ico files.

Usage: python make-icons.py <output folder> [<macOS output folder>]     (needs Pillow: pip install pillow)
The results are committed in src/Custos.Gui/Resources; run this only to change the drawing. With a second folder
(src/Custos.Mac/Assets) it also draws the icons of macOS there: custos.icns (the shield on the rounded square of
macOS app icons) and the menu bar states as tray-*.png of 44 pixels (22 points on a Retina screen).
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
        elif mark == "play":
            d.polygon([(bx - r*0.32, by - r*0.50), (bx + r*0.55, by), (bx - r*0.32, by + r*0.50)], fill="white")
        elif mark == "bang":
            d.line([(bx, by - r*0.55), (bx, by + r*0.12)], fill="white", width=lw)
            dot = lw*0.62
            d.ellipse((bx - dot, by + r*0.50 - dot, bx + dot, by + r*0.50 + dot), fill="white")
        elif mark == "cross":
            k = r*0.42
            d.line([(bx - k, by - k), (bx + k, by + k)], fill="white", width=lw)
            d.line([(bx - k, by + k), (bx + k, by - k)], fill="white", width=lw)
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
save(f"{out}/tray-running.ico", tray, body=BLUE, facet=BLUE_FACET, badge=((0, 120, 212, 255), "play"))
save(f"{out}/tray-warning.ico", tray, body=BLUE, facet=BLUE_FACET, badge=((224, 160, 0, 255), "bang"))
save(f"{out}/tray-error.ico", tray, body=BLUE, facet=BLUE_FACET, badge=((209, 52, 56, 255), "cross"))

if len(sys.argv) > 2:
    mac = sys.argv[2]

    def app_icon(size):
        S = size * SS // 4 if size >= 256 else size * SS
        im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        d = ImageDraw.Draw(im)
        # The grid of macOS app icons: an 824 of 1024 rounded square in the middle, radius about 185.
        m, r = S * 100 / 1024, S * 185 / 1024
        d.rounded_rectangle((m, m + S*0.01, S - m, S - m + S*0.01), radius=r, fill=(0, 0, 0, 60))
        d.rounded_rectangle((m, m, S - m, S - m), radius=r, fill=(244, 246, 250, 255))
        w, h = S * 0.50, S * 0.58
        d.polygon(shield(S/2, S/2, w, h), fill=BLUE)
        d.polygon(left_half(shield(S/2, S/2 + h*0.02, w*0.56, h*0.60), S/2), fill=BLUE_FACET)
        return im.resize((size, size), Image.LANCZOS)

    icns = [app_icon(s) for s in (1024, 512, 256, 128, 64, 32, 16)]
    icns[0].save(f"{mac}/custos.icns", format="ICNS", append_images=icns[1:])
    for name, body, facet, badge in [
        ("ok", BLUE, BLUE_FACET, ((46, 160, 67, 255), "check")),
        ("down", GREY, GREY_FACET, ((110, 116, 122, 255), "dash")),
        ("running", BLUE, BLUE_FACET, ((0, 120, 212, 255), "play")),
        ("warning", BLUE, BLUE_FACET, ((224, 160, 0, 255), "bang")),
        ("error", BLUE, BLUE_FACET, ((209, 52, 56, 255), "cross")),
    ]:
        draw(44, body=body, facet=facet, badge=badge).save(f"{mac}/tray-{name}.png")
