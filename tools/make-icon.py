"""Génère assets/OptiTech.ico : pastille de verre teintée (turquoise, bleu, violet), reflet et éclair blanc.
Usage : python tools/make-icon.py   (nécessite Pillow)"""
import os
from PIL import Image, ImageDraw, ImageFilter

S = 1024  # dessin en grand, réduit ensuite pour un bord net
C1, C2, C3 = (79, 227, 193), (79, 140, 255), (155, 107, 255)

def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

def draw():
    # Dégradé diagonal en trois couleurs
    grad = Image.new("RGB", (S, S))
    px = grad.load()
    for y in range(S):
        for x in range(S):
            t = (x + y) / (2.0 * (S - 1))
            px[x, y] = lerp(C1, C2, t * 2) if t < 0.5 else lerp(C2, C3, (t - 0.5) * 2)
    mask = Image.new("L", (S, S), 0)
    r = int(S * 0.235)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], r, fill=255)
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    img.paste(grad, (0, 0), mask)

    # Reflet de verre : blanc translucide sur la moitié haute, fondu vers le bas
    sheen = Image.new("L", (S, S), 0)
    sp = sheen.load()
    for y in range(int(S * 0.56)):
        a = int(118 * (1 - y / (S * 0.56)) ** 1.4)
        for x in range(S): sp[x, y] = a
    inner = Image.new("L", (S, S), 0)
    m = int(S * 0.045)
    ImageDraw.Draw(inner).rounded_rectangle([m, m, S - 1 - m, S - 1 - m], r - m, fill=255)
    from PIL import ImageChops
    sheen = ImageChops.multiply(sheen, inner)
    img = Image.alpha_composite(img, Image.merge("RGBA", (Image.new("L", (S, S), 255),) * 3 + (sheen,)))

    # Liseré clair
    edge = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(edge).rounded_rectangle([6, 6, S - 7, S - 7], r - 6, outline=(255, 255, 255, 120), width=12)
    img = Image.alpha_composite(img, edge)

    # Éclair (tracé Feather « zap », grille de 24) avec une ombre douce
    k = S / 24.0 * 0.60
    off = (S - 24 * k) / 2
    pts = [(13, 2), (3, 14), (12, 14), (11, 22), (21, 10), (12, 10)]
    poly = [(off + x * k, off + y * k) for x, y in pts]
    shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).polygon([(x + 10, y + 22) for x, y in poly], fill=(20, 30, 90, 110))
    img = Image.alpha_composite(img, shadow.filter(ImageFilter.GaussianBlur(26)))
    bolt = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(bolt)
    d.polygon(poly, fill=(255, 255, 255, 255))
    w = k * 1.3
    for i, (x, y) in enumerate(poly):  # contour arrondi : un trait par arête, un disque par sommet
        d.line([(x, y), poly[(i + 1) % len(poly)]], fill=(255, 255, 255, 255), width=int(w))
        d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=(255, 255, 255, 255))
    img = Image.alpha_composite(img, bolt)
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(img, (0, 0), mask)
    return out

if __name__ == "__main__":
    root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
    big = draw()
    sizes = [256, 64, 48, 32, 24, 16]
    big.resize((256, 256), Image.LANCZOS).save(os.path.join(root, "assets", "OptiTech.ico"), sizes=[(s, s) for s in sizes])
    big.resize((512, 512), Image.LANCZOS).save(os.path.join(root, "docs", "img", "logo-512.png"))
    print("ok")
