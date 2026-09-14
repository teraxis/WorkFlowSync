"""Generate app.ico: two arrows forming a cycle (mirror) over a rounded square, sizes 16/32/48/256.

No image libraries available, so PNG chunks and the ICO container are written by hand.
"""
import math, struct, zlib, os

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src", "WorkFlowSync.App", "Assets", "app.ico")

# Palette: Fluent-ish blue plate, white glyph.
PLATE = (0x21, 0x6B, 0xC4)
PLATE_DARK = (0x1A, 0x55, 0x9B)
GLYPH = (0xFF, 0xFF, 0xFF)


def blend(dst, src, a):
    return tuple(int(round(d + (s - d) * a)) for d, s in zip(dst, src))


def rounded_rect_cov(x, y, w, r, ss):
    """Coverage of a rounded square inset by 1/16 of the canvas, supersampled."""
    inset = w / 16.0
    x0, y0, x1, y1 = inset, inset, w - inset, w - inset
    hits = 0
    for sy in range(ss):
        for sx in range(ss):
            px = x + (sx + 0.5) / ss
            py = y + (sy + 0.5) / ss
            if px < x0 or px > x1 or py < y0 or py > y1:
                continue
            cx = min(max(px, x0 + r), x1 - r)
            cy = min(max(py, y0 + r), y1 - r)
            if (px - cx) ** 2 + (py - cy) ** 2 <= r * r:
                hits += 1
    return hits / (ss * ss)


def arc_cov(x, y, w, ss, cx, cy, radius, thick, a_from, a_to):
    """Coverage of an annulus sector (angles in degrees, y down, counter-clockwise)."""
    hits = 0
    for sy in range(ss):
        for sx in range(ss):
            px = x + (sx + 0.5) / ss
            py = y + (sy + 0.5) / ss
            dx, dy = px - cx, py - cy
            d = math.hypot(dx, dy)
            if abs(d - radius) > thick / 2:
                continue
            ang = math.degrees(math.atan2(-dy, dx)) % 360
            lo, hi = a_from % 360, a_to % 360
            inside = lo <= ang <= hi if lo <= hi else (ang >= lo or ang <= hi)
            if inside:
                hits += 1
    return hits / (ss * ss)


def tri_cov(x, y, w, ss, pts):
    def sign(p, a, b):
        return (p[0] - b[0]) * (a[1] - b[1]) - (a[0] - b[0]) * (p[1] - b[1])

    hits = 0
    for sy in range(ss):
        for sx in range(ss):
            p = (x + (sx + 0.5) / ss, y + (sy + 0.5) / ss)
            d1, d2, d3 = sign(p, pts[0], pts[1]), sign(p, pts[1], pts[2]), sign(p, pts[2], pts[0])
            neg = (d1 < 0) or (d2 < 0) or (d3 < 0)
            pos = (d1 > 0) or (d2 > 0) or (d3 > 0)
            if not (neg and pos):
                hits += 1
    return hits / (ss * ss)


def render(size):
    ss = 4 if size >= 48 else 6
    w = float(size)
    c = w / 2
    radius = w * 0.30            # arc radius
    thick = max(w * 0.11, 1.6)   # arc thickness
    corner = w * 0.22
    head = w * 0.115             # arrow head half-size

    # Two arcs with arrow heads: top-right going right-down, bottom-left going left-up.
    top = (20, 160)
    bot = (200, 340)

    def head_pts(angle_deg, flip):
        a = math.radians(angle_deg)
        tipx, tipy = c + radius * math.cos(a), c - radius * math.sin(a)
        # tangent direction (counter-clockwise), flipped for the other arrow
        tx, ty = -math.sin(a), -math.cos(a)
        if flip:
            tx, ty = -tx, -ty
        nx, ny = -ty, tx
        return [
            (tipx + tx * head * 1.6, tipy + ty * head * 1.6),
            (tipx + nx * head, tipy + ny * head),
            (tipx - nx * head, tipy - ny * head),
        ]

    rows = []
    for y in range(size):
        row = bytearray()
        for x in range(size):
            plate = rounded_rect_cov(x, y, w, corner, ss)
            # vertical gradient on the plate
            base = blend(PLATE_DARK, PLATE, 1.0 - y / w)
            g = arc_cov(x, y, w, ss, c, c, radius, thick, *top)
            g = max(g, arc_cov(x, y, w, ss, c, c, radius, thick, *bot))
            g = max(g, tri_cov(x, y, w, ss, head_pts(top[1], False)))
            g = max(g, tri_cov(x, y, w, ss, head_pts(bot[1], True)))
            rgb = blend(base, GLYPH, min(g, 1.0)) if plate > 0 else GLYPH
            a = int(round(255 * plate))
            row += bytes((rgb[0], rgb[1], rgb[2], a))
        rows.append(bytes(row))
    return rows


def png(size, rows):
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


sizes = [16, 32, 48, 256]
images = [png(s, render(s)) for s in sizes]

os.makedirs(os.path.dirname(OUT), exist_ok=True)
offset = 6 + 16 * len(images)
header = struct.pack("<HHH", 0, 1, len(images))
entries = b""
for s, img in zip(sizes, images):
    entries += struct.pack("<BBBBHHII", 0 if s >= 256 else s, 0 if s >= 256 else s, 0, 0, 1, 32, len(img), offset)
    offset += len(img)
with open(OUT, "wb") as f:
    f.write(header + entries + b"".join(images))
print("wrote", OUT, os.path.getsize(OUT), "bytes", sizes)
