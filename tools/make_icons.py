#!/usr/bin/env python3
"""Generates the GitWidget icon set (PNG + multi-size ICO) without third-party dependencies.

The glyph is a small git graph (three commits, one branch) on a rounded square.
Optional status badges (red/amber/gray dot) are rendered for tray state icons.

Usage: python3 tools/make_icons.py src/GitStatusWidget.Desktop/Assets
"""
import math
import struct
import sys
import zlib
from pathlib import Path

SUPERSAMPLE = 4

BACKGROUND = (37, 99, 235)  # blue-600
FOREGROUND = (255, 255, 255)
BADGES = {
    "failed": (220, 38, 38),   # red-600
    "warning": (217, 119, 6),  # amber-600
    "offline": (107, 114, 128) # gray-500
}


def rounded_rect_coverage(x, y, size, radius):
    """Returns 1.0 if (x, y) lies inside a rounded square of the given size."""
    cx = min(max(x, radius), size - radius)
    cy = min(max(y, radius), size - radius)
    return 1.0 if (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2 else 0.0


def dist_to_segment(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    length_sq = dx * dx + dy * dy
    t = 0.0 if length_sq == 0 else max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / length_sq))
    qx, qy = ax + t * dx, ay + t * dy
    return math.hypot(px - qx, py - qy)


def render(size, badge=None):
    """Renders an RGBA image as a list of rows of (r, g, b, a) tuples."""
    s = size * SUPERSAMPLE
    unit = s / 32.0
    radius = 7 * unit
    # git graph geometry in a 32x32 design grid
    main_x = 11 * unit
    top, middle, bottom = 8 * unit, 16 * unit, 24 * unit
    branch_x, branch_y = 22 * unit, 12 * unit
    node_r = 3.2 * unit
    stroke = 1.3 * unit

    rows = []
    for y in range(size):
        row = []
        for x in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(SUPERSAMPLE):
                for sx in range(SUPERSAMPLE):
                    px = x * SUPERSAMPLE + sx + 0.5
                    py = y * SUPERSAMPLE + sy + 0.5
                    color, alpha = BACKGROUND, rounded_rect_coverage(px, py, s, radius)
                    if alpha:
                        on_line = (
                            dist_to_segment(px, py, main_x, top, main_x, bottom) <= stroke
                            or dist_to_segment(px, py, main_x, middle + 2 * unit, branch_x, branch_y) <= stroke
                        )
                        on_node = any(
                            math.hypot(px - nx, py - ny) <= node_r
                            for nx, ny in ((main_x, top), (main_x, bottom), (branch_x, branch_y))
                        )
                        if on_line or on_node:
                            color = FOREGROUND
                    if badge:
                        bx, by, br = 25 * unit, 25 * unit, 6.5 * unit
                        d = math.hypot(px - bx, py - by)
                        if d <= br:
                            color, alpha = (BADGES[badge], 1.0) if d <= br - 1.4 * unit else (FOREGROUND, 1.0)
                    for i in range(3):
                        acc[i] += color[i] * alpha
                    acc[3] += alpha
            n = SUPERSAMPLE * SUPERSAMPLE
            a = acc[3] / n
            if a == 0:
                row.append((0, 0, 0, 0))
            else:
                row.append(tuple(int(round(acc[i] / acc[3])) for i in range(3)) + (int(round(a * 255)),))
        rows.append(row)
    return rows


def encode_png(rows):
    height, width = len(rows), len(rows[0])
    raw = b"".join(b"\x00" + bytes(c for px in row for c in px) for row in rows)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def encode_ico(pngs):
    """Builds an ICO container with PNG-compressed entries (supported since Windows Vista)."""
    header = struct.pack("<HHH", 0, 1, len(pngs))
    offset = 6 + 16 * len(pngs)
    entries, blobs = b"", b""
    for size, data in pngs:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    return header + entries + blobs


def main():
    out = Path(sys.argv[1] if len(sys.argv) > 1 else ".")
    out.mkdir(parents=True, exist_ok=True)
    (out / "gitwidget.png").write_bytes(encode_png(render(256)))
    (out / "gitwidget.ico").write_bytes(encode_ico([(s, encode_png(render(s))) for s in (16, 24, 32, 48, 256)]))
    for state in (None, *BADGES):
        name = "tray" if state is None else f"tray-{state}"
        (out / f"{name}.png").write_bytes(encode_png(render(64, state)))
    print(f"icons written to {out}")


if __name__ == "__main__":
    main()
