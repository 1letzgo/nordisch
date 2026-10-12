#!/usr/bin/env python3
"""Logo concepts for Nordisch. Renders 1024 px app icons + wordmark lockups and a comparison sheet.

    python3 Branding/concepts/make_concepts.py
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageFilter

OUT = Path(__file__).parent
SS = 4  # supersampling
NAVY = (26, 58, 78)
NAVY_DEEP = (18, 42, 58)
MUSTARD = (227, 168, 43)
WHITE = (255, 255, 255)
BRICK = (178, 74, 52)
SKY = (110, 170, 225)
ROCKWELL = "/System/Library/Fonts/Supplemental/Rockwell.ttc"


def font(size, bold=True):
    # Rockwell.ttc faces: 0 Regular, 1 Italic, 2 Bold, 3 Bold Italic
    for idx in ((2,) if bold else (0,)):
        try:
            return ImageFont.truetype(ROCKWELL, size, index=idx)
        except OSError:
            continue
    return ImageFont.load_default()


def canvas(size, color):
    return Image.new("RGBA", (size * SS, size * SS), color + (255,))


def finish(img, size):
    return img.resize((size, size), Image.LANCZOS)


def S(v):
    return v * SS


def poly(d, pts, fill):
    d.polygon([(S(x), S(y)) for x, y in pts], fill=fill)


def rect(d, x0, y0, x1, y1, fill, r=0):
    if r:
        d.rounded_rectangle([S(x0), S(y0), S(x1), S(y1)], radius=S(r), fill=fill)
    else:
        d.rectangle([S(x0), S(y0), S(x1), S(y1)], fill=fill)


def waves(d, y, color, width=26, amp=22, period=170, x0=-40, x1=1064):
    """Thick sine-ish wave band drawn as overlapping circles (smooth at any scale)."""
    import math
    x = x0
    while x < x1:
        yy = y + math.sin(x / period * 2 * math.pi) * amp
        d.ellipse([S(x - width / 2), S(yy - width / 2), S(x + width / 2), S(yy + width / 2)], fill=color)
        x += 2


# ---------------------------------------------------------------- concept 1: lighthouse

def icon_lighthouse():
    img = canvas(1024, NAVY)
    d = ImageDraw.Draw(img, "RGBA")
    # Light beams
    poly(d, [(512, 300), (60, 170), (60, 330)], MUSTARD + (90,))
    poly(d, [(512, 300), (964, 170), (964, 330)], MUSTARD + (90,))
    # Glow
    d.ellipse([S(432), S(220), S(592), S(380)], fill=MUSTARD + (255,))
    # Dune
    d.ellipse([S(170), S(690), S(860), S(1000)], fill=MUSTARD)
    # Tower (tapered), red/white bands
    top_w, bot_w, top_y, bot_y = 120, 200, 360, 790
    def tw(y):
        t = (y - top_y) / (bot_y - top_y)
        return top_w + (bot_w - top_w) * t
    bands = [(top_y, 470, WHITE), (470, 580, BRICK), (580, 690, WHITE), (690, bot_y, BRICK)]
    for y0, y1, c in bands:
        w0, w1 = tw(y0) / 2, tw(y1) / 2
        poly(d, [(512 - w0, y0), (512 + w0, y0), (512 + w1, y1), (512 - w1, y1)], c)
    # Lantern room + roof
    rect(d, 452, 300, 572, 362, WHITE, 8)
    rect(d, 470, 312, 554, 352, MUSTARD, 4)
    poly(d, [(432, 300), (592, 300), (512, 228)], BRICK)
    rect(d, 420, 356, 604, 372, WHITE, 6)  # gallery
    # Waves in front
    waves(d, 860, WHITE, width=28, amp=18, period=200)
    waves(d, 935, (255, 255, 255, 160), width=22, amp=16, period=200, x0=-140)
    return finish(img, 1024)


# ---------------------------------------------------------------- concept 2: gables

def house(d, x, w, base, h_wall, h_roof, wall, roof_line=None, win=WHITE):
    poly(d, [(x, base), (x, base - h_wall), (x + w / 2, base - h_wall - h_roof), (x + w, base - h_wall), (x + w, base)], wall)
    # Windows: 2 x 2 + round attic window
    ww, wh = w * 0.18, h_wall * 0.2
    for cx in (x + w * 0.3, x + w * 0.7):
        for cy in (base - h_wall * 0.32, base - h_wall * 0.72):
            rect(d, cx - ww / 2, cy - wh / 2, cx + ww / 2, cy + wh / 2, win, 4)
    r = w * 0.08
    cy = base - h_wall - h_roof * 0.42
    d.ellipse([S(x + w / 2 - r), S(cy - r), S(x + w / 2 + r), S(cy + r)], fill=win)


def icon_gables():
    img = canvas(1024, NAVY)
    d = ImageDraw.Draw(img, "RGBA")
    base = 720
    house(d, 150, 230, base, 230, 150, BRICK, win=(250, 236, 214))
    house(d, 400, 230, base, 300, 170, MUSTARD, win=NAVY)
    house(d, 650, 230, base, 200, 140, WHITE, win=NAVY)
    rect(d, 120, base, 904, base + 18, WHITE)
    waves(d, 800, MUSTARD, width=30, amp=20, period=190)
    waves(d, 880, WHITE, width=24, amp=16, period=190, x0=-90)
    return finish(img, 1024)


# ---------------------------------------------------------------- concept 3: compass N

def icon_compass():
    img = canvas(1024, NAVY)
    d = ImageDraw.Draw(img, "RGBA")
    # Compass ring
    d.ellipse([S(132), S(132), S(892), S(892)], outline=(255, 255, 255, 70), width=S(10))
    for a in range(0, 360, 30):
        import math
        if a in (0, 180):
            continue  # hidden behind the needle tips
        r0, r1 = 362, 380 if a % 90 else 340
        x0 = 512 + math.sin(math.radians(a)) * r0
        y0 = 512 - math.cos(math.radians(a)) * r0
        x1 = 512 + math.sin(math.radians(a)) * r1
        y1 = 512 - math.cos(math.radians(a)) * r1
        d.line([S(x0), S(y0), S(x1), S(y1)], fill=(255, 255, 255, 110), width=S(8))
    # Big N
    f = font(S(500), bold=True)
    bbox = d.textbbox((0, 0), "N", font=f)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    d.text((S(512) - tw / 2 - bbox[0], S(530) - th / 2 - bbox[1]), "N", font=f, fill=WHITE)
    # Needle: mustard north half, white-ish south half, slightly tilted
    poly(d, [(512, 150), (562, 512), (462, 512)], MUSTARD)
    poly(d, [(512, 874), (562, 512), (462, 512)], (74, 112, 138, 255))
    d.ellipse([S(487), S(487), S(537), S(537)], fill=NAVY_DEEP)
    d.ellipse([S(500), S(500), S(524), S(524)], fill=MUSTARD)
    return finish(img, 1024)


# ---------------------------------------------------------------- lockups & sheet

def wordmark(height, color):
    f_bold, f_reg = font(S(height), True), font(S(height), False)
    tmp = Image.new("RGBA", (S(height * 8), S(height * 2)), (0, 0, 0, 0))
    d = ImageDraw.Draw(tmp)
    x = S(10)
    d.text((x, S(10)), "NORD", font=f_bold, fill=color)
    x += d.textlength("NORD", font=f_bold)
    d.text((x, S(10)), "isch", font=f_reg, fill=color)
    bbox = tmp.getbbox()
    tmp = tmp.crop(bbox)
    return tmp.resize((tmp.width // SS, tmp.height // SS), Image.LANCZOS)


def rounded_icon(icon, size):
    ic = icon.resize((size, size), Image.LANCZOS)
    mask = Image.new("L", (size * 4, size * 4), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size * 4, size * 4], radius=int(size * 4 * 0.225), fill=255)
    mask = mask.resize((size, size), Image.LANCZOS)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(ic, (0, 0), mask)
    return out


def lockup(icon, bg, fg):
    w, h = 1400, 420
    img = Image.new("RGBA", (w, h), bg + (255,))
    ic = rounded_icon(icon, 300)
    img.alpha_composite(ic, (60, 60))
    wm = wordmark(150, fg)
    img.alpha_composite(wm, (420, (h - wm.height) // 2))
    return img


def export_final(icon):
    """Final brand assets: Branding/logo.png (1024 icon, opaque) + Branding/wordmark.png (white, transparent)."""
    root = OUT.parent
    old = root / "logo.png"
    if old.exists() and not (root / "logo_v1.png").exists():
        old.rename(root / "logo_v1.png")
    icon.convert("RGB").save(root / "logo.png")
    wordmark(220, WHITE).save(root / "wordmark.png")
    lockup(icon, NAVY, WHITE).convert("RGB").save(root / "lockup_dark.png")
    lockup(icon, (255, 255, 255), NAVY).convert("RGB").save(root / "lockup_light.png")


def main():
    concepts = {
        "1_leuchtturm": icon_lighthouse(),
        "2_giebel": icon_gables(),
        "3_kompass": icon_compass(),
    }
    sheet = Image.new("RGBA", (3 * 520 + 40, 1060), (244, 241, 234, 255))
    d = ImageDraw.Draw(sheet)
    label_font = ImageFont.truetype("/System/Library/Fonts/Avenir Next.ttc", 30)
    for i, (name, icon) in enumerate(concepts.items()):
        icon.convert("RGB").save(OUT / f"{name}_icon.png")
        lockup(icon, (255, 255, 255), NAVY).convert("RGB").save(OUT / f"{name}_lockup_light.png")
        lockup(icon, NAVY, WHITE).convert("RGB").save(OUT / f"{name}_lockup_dark.png")
        x = 20 + i * 520
        sheet.alpha_composite(rounded_icon(icon, 440), (x + 20, 30))
        # Small sizes: home screen & spotlight
        sheet.alpha_composite(rounded_icon(icon, 120), (x + 20, 500))
        sheet.alpha_composite(rounded_icon(icon, 60), (x + 160, 530))
        lk = lockup(icon, NAVY, WHITE)
        lk = lk.resize((480, int(480 * lk.height / lk.width)), Image.LANCZOS)
        sheet.alpha_composite(lk, (x + 20, 650))
        lk2 = lockup(icon, (255, 255, 255), NAVY)
        lk2 = lk2.resize((480, int(480 * lk2.height / lk2.width)), Image.LANCZOS)
        sheet.alpha_composite(lk2, (x + 20, 820))
        d.text((x + 250, 520), name.split("_", 1)[1].capitalize(), font=label_font, fill=NAVY)
    sheet.convert("RGB").save(OUT / "concepts_sheet.png")
    if "--final" in __import__("sys").argv:
        export_final(concepts["3_kompass"])
    print("written to", OUT)


if __name__ == "__main__":
    main()
