#!/usr/bin/env python3
"""Build the YuukaOCR application icon from the artwork in Assets/.

    python tools/makeicon.py                 # regenerate Assets/app.ico + Assets/app-icon.png
    python tools/makeicon.py --check         # verify the generated files are up to date

Why a script and no NuGet image library: the solution deliberately has no external packages, and the
Windows SDK ships nothing that can resize an image from a console build. Everything below is stdlib
(zlib + struct): a PNG decoder for the source (8-bit RGB/RGBA, non-interlaced), an area-average
downscaler (a 1254 px artwork only ever shrinks, so averaging is exactly the right filter), a rounded
corner mask with an anti-aliased edge, a PNG encoder for the 256 px window icon and the large ICO
entries, and the ICO container itself.

The source artwork is never modified — it stays the master copy next to the generated files. The
artwork is a square tile, so the generated icons are rounded: a hard 90° corner reads as a white
sticker on a dark taskbar or in a title bar, and the four corners of the tile are empty background.
"""

from __future__ import annotations

import argparse
import struct
import sys
import zlib
from pathlib import Path

ASSETS = Path(__file__).resolve().parent.parent / "src" / "YuukaOCR.App" / "Assets"
SOURCE = ASSETS / "18 thg 8 2026.png"
ICO = ASSETS / "app.ico"
WINDOW_PNG = ASSETS / "app-icon.png"

# Every size Windows asks for: the shell (16/20/24/32/40/48/64/96/128/256), Alt+Tab, the taskbar and
# the title bar. 256 is the largest size a .ico may carry.
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
WINDOW_ICON_SIZE = 256

# Corner radius as a fraction of the side. 0.20 is the rounded-tile look Windows itself uses for app
# icons: visibly round at 256 px, still a corner rather than a circle at 16 px.
CORNER_RADIUS = 0.20

# Sub-samples per pixel and axis for the corner mask (16 samples per pixel): the difference between a
# stair-stepped corner and a smooth one at 16 and 20 px.
SUPERSAMPLE = 4


# --------------------------------------------------------------------------- PNG decode

def _decode_png(path: Path) -> tuple[int, int, int, list[bytearray]]:
    """Decode an 8-bit RGB/RGBA non-interlaced PNG into raw per-channel rows."""
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise SystemExit(f"{path} is not a PNG file")

    pos = 8
    width = height = depth = color_type = interlace = -1
    idat = bytearray()

    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        kind = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + length]
        pos += 12 + length

        if kind == b"IHDR":
            width, height, depth, color_type, _, _, interlace = struct.unpack(">IIBBBBB", chunk)
        elif kind == b"IDAT":
            idat += chunk
        elif kind == b"IEND":
            break

    if depth != 8 or color_type not in (2, 6):
        raise SystemExit(f"unsupported PNG (depth {depth}, colour type {color_type}); expected 8-bit RGB/RGBA")
    if interlace != 0:
        raise SystemExit("interlaced PNGs are not supported")

    channels = 3 if color_type == 2 else 4
    stride = width * channels
    raw = zlib.decompress(bytes(idat))

    rows: list[bytearray] = []
    previous = bytearray(stride)
    offset = 0

    for _ in range(height):
        filter_type = raw[offset]
        offset += 1
        line = bytearray(raw[offset:offset + stride])
        offset += stride

        if filter_type == 1:      # Sub
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif filter_type == 2:    # Up
            for i in range(stride):
                line[i] = (line[i] + previous[i]) & 0xFF
        elif filter_type == 3:    # Average
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((left + previous[i]) >> 1)) & 0xFF
        elif filter_type == 4:    # Paeth
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = previous[i]
                c = previous[i - channels] if i >= channels else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                predictor = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                line[i] = (line[i] + predictor) & 0xFF
        elif filter_type != 0:
            raise SystemExit(f"unknown PNG filter {filter_type}")

        previous = line
        rows.append(line)

    return width, height, channels, rows


def read_png(path: Path) -> tuple[int, int, list[bytearray]]:
    """Decode to RGB rows: the artwork is opaque, so the alpha channel is dropped."""
    width, height, channels, rows = _decode_png(path)
    if channels == 3:
        return width, height, rows

    flat = [bytearray(width * 3) for _ in range(height)]
    for y, line in enumerate(rows):
        out = flat[y]
        for x in range(width):
            src = x * 4
            dst = x * 3
            out[dst] = line[src]
            out[dst + 1] = line[src + 1]
            out[dst + 2] = line[src + 2]
    return width, height, flat


def read_alpha(path: Path) -> list[bytearray] | None:
    """The alpha plane of a generated icon, or None for an opaque (RGB) file."""
    width, height, channels, rows = _decode_png(path)
    if channels == 3:
        return None
    return [bytearray(line[i] for i in range(3, len(line), 4)) for line in rows]


# --------------------------------------------------------------------------- resize

def resize_area(width: int, height: int, rows: list[bytearray], size: int) -> list[bytearray]:
    """Area-average downscale to size×size.

    Every destination pixel is the average of the source pixels it covers, which is a box filter — the
    correct (and alias-free) filter when shrinking. Sizes are integer divisors of the source for the
    small steps and fractional for the rest, so both paths are handled with fractional weights.
    """
    out: list[bytearray] = []

    for dy in range(size):
        y0 = dy * height / size
        y1 = (dy + 1) * height / size
        line = bytearray(size * 3)

        for dx in range(size):
            x0 = dx * width / size
            x1 = (dx + 1) * width / size

            r = g = b = total = 0.0
            for sy in range(int(y0), min(int(y1) + 1, height)):
                wy = min(y1, sy + 1) - max(y0, sy)
                if wy <= 0:
                    continue
                row = rows[sy]
                for sx in range(int(x0), min(int(x1) + 1, width)):
                    wx = min(x1, sx + 1) - max(x0, sx)
                    if wx <= 0:
                        continue
                    weight = wx * wy
                    base = sx * 3
                    r += row[base] * weight
                    g += row[base + 1] * weight
                    b += row[base + 2] * weight
                    total += weight

            total = total or 1.0
            at = dx * 3
            line[at] = min(255, round(r / total))
            line[at + 1] = min(255, round(g / total))
            line[at + 2] = min(255, round(b / total))

        out.append(line)

    return out


# --------------------------------------------------------------------------- rounded corners

def rounded_alpha(size: int, radius_ratio: float = CORNER_RADIUS) -> list[bytearray]:
    """Coverage of a rounded square: 255 inside, 0 outside the corner arc, soft along the edge.

    The artwork is a full square tile with a circular emblem in the middle, so its four corners are
    plain background. Rounding them (and making the outside transparent) is what stops the icon from
    looking like a white sticker on a dark taskbar; the anti-aliasing keeps the 16 px entry from
    showing a stair step where the 256 px one shows a curve.
    """
    radius = max(0.0, min(size / 2.0, size * radius_ratio))
    if radius <= 0:
        return [bytearray(b"\xff" * size) for _ in range(size)]

    steps = SUPERSAMPLE
    share = 255.0 / (steps * steps)
    alpha: list[bytearray] = []

    for y in range(size):
        line = bytearray(size)
        # A row that lies entirely between the two corner arcs never touches the mask: full coverage.
        straight_y = radius <= y and y + 1 <= size - radius

        for x in range(size):
            if straight_y or (radius <= x and x + 1 <= size - radius):
                line[x] = 255
                continue

            covered = 0
            for sy in range(steps):
                py = y + (sy + 0.5) / steps
                dy = py - min(max(py, radius), size - radius)
                for sx in range(steps):
                    px = x + (sx + 0.5) / steps
                    dx = px - min(max(px, radius), size - radius)
                    if dx * dx + dy * dy <= radius * radius:
                        covered += 1

            line[x] = min(255, round(covered * share))

        alpha.append(line)

    return alpha


def with_alpha(rows: list[bytearray], alpha: list[bytearray]) -> list[bytearray]:
    """RGB rows + an alpha plane → RGBA rows."""
    out: list[bytearray] = []
    for y, line in enumerate(rows):
        mask = alpha[y]
        rgba = bytearray(len(mask) * 4)
        for x in range(len(mask)):
            src = x * 3
            dst = x * 4
            rgba[dst] = line[src]
            rgba[dst + 1] = line[src + 1]
            rgba[dst + 2] = line[src + 2]
            rgba[dst + 3] = mask[x]
        out.append(rgba)
    return out


# --------------------------------------------------------------------------- PNG encode

def encode_png(rows: list[bytearray], size: int) -> bytes:
    raw = bytearray()
    for line in rows:
        raw.append(0)          # filter type 0 (None): the images are tiny, size does not matter
        raw += line

    def chunk(kind: bytes, payload: bytes) -> bytes:
        return (struct.pack(">I", len(payload)) + kind + payload +
                struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF))

    # Colour type 6 = RGBA: the rounded corners need a real alpha channel, not a baked-in white.
    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) +
            chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


# --------------------------------------------------------------------------- ICO encode

def dib_entry(rows: list[bytearray], size: int) -> bytes:
    """32-bit bottom-up DIB + 1-bit AND mask, the classic ICO payload used for the small sizes."""
    header = struct.pack("<IiiHHIIiiII",
                         40, size, size * 2, 1, 32, 0, size * size * 4, 0, 0, 0, 0)

    pixels = bytearray()
    for y in range(size - 1, -1, -1):          # bottom-up
        line = rows[y]
        for x in range(size):
            base = x * 4
            pixels += bytes((line[base + 2], line[base + 1], line[base], line[base + 3]))   # BGRA

    # The AND mask is the pre-alpha fallback: a set bit means "transparent here". Without it, renderers
    # that ignore the alpha channel paint the rounded corner black instead of leaving it out.
    mask_stride = ((size + 31) // 32) * 4      # 1 bit per pixel, rows padded to 4 bytes
    mask = bytearray(mask_stride * size)
    for y in range(size):
        line = rows[y]
        row_at = (size - 1 - y) * mask_stride   # bottom-up, like the pixels
        for x in range(size):
            if line[x * 4 + 3] == 0:
                mask[row_at + (x >> 3)] |= 0x80 >> (x & 7)

    return header + bytes(pixels) + bytes(mask)


def build_ico(images: dict[int, list[bytearray]]) -> bytes:
    entries: list[bytes] = []
    payloads: list[bytes] = []

    for size in sorted(images):
        rows = images[size]
        # PNG for the big entries (Vista+ reads them), a plain DIB for the small ones: some shell
        # paths still refuse a PNG-compressed 16 px icon.
        payload = encode_png(rows, size) if size >= 128 else dib_entry(rows, size)
        payloads.append(payload)

        entries.append(struct.pack("<BBBBHHII",
                                   size if size < 256 else 0,
                                   size if size < 256 else 0,
                                   0, 0, 1, 32, len(payload), 0))

    offset = 6 + 16 * len(entries)
    directory = bytearray(struct.pack("<HHH", 0, 1, len(entries)))

    for index, entry in enumerate(entries):
        directory += entry[:12] + struct.pack("<I", offset)
        offset += len(payloads[index])

    return bytes(directory) + b"".join(payloads)


# --------------------------------------------------------------------------- main

def build() -> None:
    width, height, rows = read_png(SOURCE)
    if width != height:
        # A non-square artwork is centre-cropped to a square first: an application icon must be square.
        side = min(width, height)
        left = (width - side) // 2
        top = (height - side) // 2
        rows = [row[left * 3:(left + side) * 3] for row in rows[top:top + side]]
        width = height = side

    images: dict[int, list[bytearray]] = {}
    for size in SIZES:
        resized = resize_area(width, height, rows, size)
        images[size] = with_alpha(resized, rounded_alpha(size))

    ICO.write_bytes(build_ico(images))
    WINDOW_PNG.write_bytes(encode_png(images[WINDOW_ICON_SIZE], WINDOW_ICON_SIZE))

    print(f"source      : {SOURCE.name}  {width}x{height}")
    print(f"corners     : radius {CORNER_RADIUS:.0%} of the side, {SUPERSAMPLE}x{SUPERSAMPLE} anti-aliased, "
          f"{round(WINDOW_ICON_SIZE * CORNER_RADIUS)} px at {WINDOW_ICON_SIZE}")
    print(f"icon        : {ICO.name}  {ICO.stat().st_size:,} bytes  sizes={SIZES}")
    print(f"window icon : {WINDOW_PNG.name}  {WINDOW_PNG.stat().st_size:,} bytes  {WINDOW_ICON_SIZE}x{WINDOW_ICON_SIZE}")


def check() -> int:
    if not ICO.exists() or not WINDOW_PNG.exists():
        print("generated icon files are missing — run tools/makeicon.py")
        return 1

    data = ICO.read_bytes()
    count = struct.unpack("<H", data[4:6])[0]
    sizes = [data[6 + 16 * i] or 256 for i in range(count)]
    if sizes != SIZES:
        print(f"app.ico holds {sizes}, expected {SIZES}")
        return 1

    # The rounding is the part a header check cannot see: prove it in the pixels.
    alpha = read_alpha(WINDOW_PNG)
    if alpha is None:
        print("app-icon.png has no alpha channel — the corners are not rounded")
        return 1

    side = len(alpha)
    corners = [alpha[0][0], alpha[0][side - 1], alpha[side - 1][0], alpha[side - 1][side - 1]]
    centre = alpha[side // 2][side // 2]
    soft = sum(1 for line in alpha for a in line if 0 < a < 255)

    if any(corners):
        print(f"app-icon.png corners are not transparent: {corners}")
        return 1
    if centre != 255:
        print(f"app-icon.png centre is not opaque: {centre}")
        return 1
    if soft == 0:
        print("app-icon.png has no anti-aliased edge — the corner is a hard step")
        return 1

    print(f"app.ico ok: {count} sizes {sizes}; window icon {WINDOW_PNG.stat().st_size:,} bytes; "
          f"corners transparent, {soft:,} soft edge pixels")
    return 0


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Build the YuukaOCR application icon.")
    parser.add_argument("--check", action="store_true", help="verify the generated files instead of writing them")
    args = parser.parse_args()
    if args.check:
        sys.exit(check())
    build()
