"""
Generates StructuredViews' icons (standard library only):

  resources/StructuredViews.ico       plug-in and panel icon
  dist/icon.png                   package icon

Run from the repository root:  python3 tools/make_icon.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


# --- tiny PNG / ICO writer --------------------------------------------------

def png(w, h, rgba):
    raw = b''.join(b'\x00' + rgba[y * w * 4:(y + 1) * w * 4] for y in range(h))

    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)

    return (b'\x89PNG\r\n\x1a\n'
            + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9))
            + chunk(b'IEND', b''))


def ico(images):
    """images: list of (size, png bytes)."""
    header = struct.pack('<HHH', 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b''
    for size, data in images:
        entries += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    return header + entries + b''.join(d for _, d in images)


# --- icon drawing (unit square, y down) -------------------------------------

# A camera, like Rhino's own Named Views icon, but blue so the two panels are easy to tell apart.
BODY = (0x3A, 0x8D, 0xDE)
TOP = (0x2A, 0x6C, 0xB5)
RING = (0xFF, 0xFF, 0xFF)
GLASS = (0x1E, 0x4E, 0x8C)
FLASH = (0xBF, 0xDD, 0xFA)


def rounded(x, y, x0, y0, x1, y1, r):
    if not (x0 <= x <= x1 and y0 <= y <= y1):
        return False
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def colour(x, y):
    # Lens first (it sits on top of the body): white ring, dark glass, a small highlight.
    d2 = (x - 0.5) ** 2 + (y - 0.585) ** 2
    if d2 <= 0.235 ** 2:
        if (x - 0.43) ** 2 + (y - 0.515) ** 2 <= 0.045 ** 2:
            return RING
        return GLASS if d2 <= 0.15 ** 2 else RING
    if rounded(x, y, 0.75, 0.37, 0.87, 0.45, 0.025):
        return FLASH
    if rounded(x, y, 0.04, 0.29, 0.96, 0.88, 0.11):
        return BODY
    if rounded(x, y, 0.30, 0.15, 0.64, 0.36, 0.06):
        return TOP
    return None


def render(size, ss=4):
    out = bytearray()
    for py in range(size):
        for px in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(ss):
                for sx in range(ss):
                    c = colour((px + (sx + 0.5) / ss) / size, (py + (sy + 0.5) / ss) / size)
                    if c:
                        acc[0] += c[0]; acc[1] += c[1]; acc[2] += c[2]; acc[3] += 1
            n = ss * ss
            if acc[3]:
                out += bytes((acc[0] // acc[3], acc[1] // acc[3], acc[2] // acc[3], 255 * acc[3] // n))
            else:
                out += bytes(4)
    return png(size, size, bytes(out))


if __name__ == '__main__':
    images = [(s, render(s)) for s in (16, 24, 32, 48, 256)]
    with open(os.path.join(ROOT, 'resources', 'StructuredViews.ico'), 'wb') as f:
        f.write(ico(images))
    os.makedirs(os.path.join(ROOT, 'dist'), exist_ok=True)
    with open(os.path.join(ROOT, 'dist', 'icon.png'), 'wb') as f:
        f.write(dict(images)[256])
    print('wrote resources/StructuredViews.ico and dist/icon.png')
